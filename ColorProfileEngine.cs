using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SmartHdrManager
{
    /// <summary>SMPTE ST 2084 (PQ) and sRGB transfer functions.</summary>
    internal static class TransferFunctions
    {
        private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

        public static double NitsToPq(double nits)
        {
            double y = Math.Pow(Math.Max(nits, 0) / 10000.0, M1);
            return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
        }

        public static double PqToNits(double signal)
        {
            double e = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
            return 10000.0 * Math.Pow(Math.Max(e - C1, 0) / (C2 - C3 * e), 1 / M1);
        }

        /// <summary>Linear light 0..1 to the sRGB signal.</summary>
        public static double SrgbEncode(double linear) =>
            linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

        /// <summary>The sRGB signal to linear light 0..1 (the curve Windows decodes SDR content with).</summary>
        public static double SrgbDecode(double signal) =>
            signal <= 0.04045 ? signal / 12.92 : Math.Pow((signal + 0.055) / 1.055, 2.4);
    }

    /// <summary>The SDR gamma correction, and its combination with the display's own calibration.</summary>
    internal static class GammaCorrection
    {
        /// <summary>
        /// Below SDR white: undo Windows' sRGB decode and apply the gamma. Above it: unchanged. No blend at the joint: both
        /// sides meet at W and the slope only changes by about 3% (2.2 x 1.055 / 2.4 = 0.967), far less than a visible kink.
        /// </summary>
        public static double CorrectNits(double nits, double white, double gamma) =>
            nits >= white ? nits : white * Math.Pow(TransferFunctions.SrgbEncode(Math.Max(nits, 0) / white), gamma);

        public static double CorrectPq(double signal, double white, double gamma) =>
            TransferFunctions.NitsToPq(CorrectNits(TransferFunctions.PqToNits(signal), white, gamma));

        /// <summary>Linear interpolation in a LUT over 0..1, which is how Windows reads it.</summary>
        public static double Sample(double[] lut, double x)
        {
            double position = Math.Clamp(x, 0, 1) * (lut.Length - 1);
            int low = Math.Min((int)position, lut.Length - 2);
            return lut[low] + (lut[low + 1] - lut[low]) * (position - low);
        }

        /// <summary>What the final LUT must be at x: the correction first, then the calibration.</summary>
        public static double Analytic(double x, double[] baseLut, double white, double gamma) => Sample(baseLut, CorrectPq(x, white, gamma));

        /// <summary>Rounded to what an s15Fixed16 field can hold.</summary>
        public static double Quantize(double value) => Math.Round(value * 65536.0) / 65536.0;

        public static double[] BuildLut(double[] baseLut, double white, double gamma, int entries)
        {
            var lut = new double[entries];
            for (int i = 0; i < entries; i++) lut[i] = Quantize(Analytic(i / (entries - 1.0), baseLut, white, gamma));
            return lut;
        }
    }

    /// <summary>Checks a generated LUT against the exact curve. A LUT that fails is never installed.</summary>
    internal static class LutValidator
    {
        // A quarter of a 10-bit PQ code value. Panels receive at most 10-12 bits, so an error of this size stays well below
        // one code step at the signal the display gets; a correct 4096-entry LUT is orders of magnitude closer than this.
        public const double MaxErrorPq = 0.25 / 1023;
        private const int StepsBetweenEntries = 8;
        private const double Quantum = 1.0 / 65536;

        /// <returns>null when the LUT is good, otherwise why it is not.</returns>
        public static string? Validate(double[] lut, Func<double, double> analytic)
        {
            if (lut.Length < 2) return "LUT has fewer than 2 entries.";
            int last = lut.Length - 1;
            for (int i = 0; i <= last; i++)
            {
                if (double.IsNaN(lut[i]) || lut[i] < 0 || lut[i] > 1 + Quantum) return $"Entry {i} is out of range: {lut[i]}.";
                if (i > 0 && lut[i] < lut[i - 1]) return $"Entry {i} decreases.";
            }

            // Error against the exact curve, including between entries, where Windows interpolates
            for (int i = 0; i < last; i++)
            {
                for (int step = 0; step <= StepsBetweenEntries; step++)
                {
                    double t = step / (double)StepsBetweenEntries;
                    double error = Math.Abs(lut[i] + (lut[i + 1] - lut[i]) * t - analytic((i + t) / last));
                    if (error > MaxErrorPq) return $"Error {error:0.000000} near entry {i} is above the limit of {MaxErrorPq:0.000000}.";
                }
            }

            // A flat run is only fine where the exact curve is flat too, to within rounding
            for (int start = 0; start < last;)
            {
                int end = start;
                while (end < last && lut[end + 1] == lut[start]) end++;
                if (end > start + 1 && analytic(end / (double)last) - analytic(start / (double)last) > 2 * Quantum)
                    return $"Unexplained flat run from entry {start} to {end}.";
                start = end + 1;
            }
            return null;
        }
    }

    /// <summary>Builds the corrected copy of a calibration profile.</summary>
    internal static class CorrectedProfile
    {
        public const int LutEntries = 4096;

        /// <summary>
        /// A copy of the calibration profile with the correction folded into its LUTs. Matrix, luminance values and every
        /// other tag stay as they are. Throws when the result fails validation: such a profile is never installed.
        /// </summary>
        public static byte[] FromCalibration(byte[] calibrationFile, double white, double gamma, string description)
        {
            var icc = IccProfile.Parse(calibrationFile);
            var tag = icc.Find("MHC2") ?? throw new InvalidDataException("The calibration profile has no MHC2 tag.");
            var calibration = Mhc2Tag.Parse(tag.Data);
            tag.Data = Corrected(calibration, white, gamma).Serialize();
            tag.OriginalOffset = 0;

            var desc = icc.Find("desc");
            if (desc != null)
            {
                desc.Data = Mluc(description);
                desc.OriginalOffset = 0;
            }
            return icc.Serialize();
        }

        /// <summary>For a display without a calibration profile: identity matrix and LUT, luminance as the display reports it.</summary>
        public static byte[] FromIdentity(double minNits, double maxNits, double white, double gamma, string description)
        {
            var identity = new Mhc2Tag { MinNits = minNits, MaxNits = maxNits, Matrix = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 } };
            for (int c = 0; c < 3; c++) identity.Luts[c] = new double[] { 0, 1 };

            var header = new byte[128];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x04400000);   // ICC 4.4
            Encoding.ASCII.GetBytes("mntr", header.AsSpan(12));
            Encoding.ASCII.GetBytes("RGB ", header.AsSpan(16));
            Encoding.ASCII.GetBytes("XYZ ", header.AsSpan(20));
            Encoding.ASCII.GetBytes("acsp", header.AsSpan(36));
            var now = DateTime.UtcNow;
            ushort[] date = { (ushort)now.Year, (ushort)now.Month, (ushort)now.Day, (ushort)now.Hour, (ushort)now.Minute, (ushort)now.Second };
            for (int i = 0; i < date.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24 + i * 2), date[i]);
            WriteXyz(header, 68, 0.9642, 1.0, 0.8249);   // PCS illuminant D50

            var linear = new byte[12];
            Encoding.ASCII.GetBytes("curv", linear);
            var icc = new IccProfile { Header = header };
            void Add(string signature, byte[] data) => icc.Tags.Add(new IccProfile.Tag { Signature = signature, Data = data });
            Add("desc", Mluc(description));
            Add("cprt", Mluc("Generated by Smart HDR Manager"));
            Add("wtpt", XyzTag(0.9642, 1.0, 0.8249));
            Add("lumi", XyzTag(maxNits, maxNits, maxNits));
            Add("rXYZ", XyzTag(0.4360, 0.2225, 0.0139));
            Add("gXYZ", XyzTag(0.3851, 0.7169, 0.0971));
            Add("bXYZ", XyzTag(0.1431, 0.0606, 0.7139));
            Add("rTRC", linear);
            Add("gTRC", linear);
            Add("bTRC", linear);
            Add("MHC2", Corrected(identity, white, gamma).Serialize());
            return icc.Serialize();
        }

        private static Mhc2Tag Corrected(Mhc2Tag calibration, double white, double gamma)
        {
            var result = new Mhc2Tag { MinNits = calibration.MinNits, MaxNits = calibration.MaxNits, Matrix = calibration.Matrix };
            int entries = Math.Max(LutEntries, calibration.EntryCount);
            for (int c = 0; c < 3; c++)
            {
                var baseLut = calibration.Luts[c];
                result.Luts[c] = GammaCorrection.BuildLut(baseLut, white, gamma, entries);
                string? problem = LutValidator.Validate(result.Luts[c], x => GammaCorrection.Analytic(x, baseLut, white, gamma));
                if (problem != null) throw new InvalidDataException($"Generated LUT {"RGB"[c]} failed validation: {problem}");
            }
            return result;
        }

        private static byte[] XyzTag(double x, double y, double z)
        {
            var tag = new byte[20];
            Encoding.ASCII.GetBytes("XYZ ", tag);
            WriteXyz(tag, 8, x, y, z);
            return tag;
        }

        private static void WriteXyz(byte[] target, int at, double x, double y, double z)
        {
            BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(at), (int)Math.Round(x * 65536));
            BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(at + 4), (int)Math.Round(y * 65536));
            BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(at + 8), (int)Math.Round(z * 65536));
        }

        /// <summary>An ICC 'mluc' tag with one English string.</summary>
        private static byte[] Mluc(string text)
        {
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
            var tag = new byte[28 + utf16.Length];
            Encoding.ASCII.GetBytes("mluc", tag);
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(12), 12);
            Encoding.ASCII.GetBytes("enUS", tag.AsSpan(16));
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(20), (uint)utf16.Length);
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(24), 28);
            utf16.CopyTo(tag, 28);
            return tag;
        }
    }

    /// <summary>An ICC profile split into its header and tags, so tags that are not changed are written back byte for byte.</summary>
    internal sealed class IccProfile
    {
        public sealed class Tag
        {
            public string Signature = string.Empty;
            public byte[] Data = Array.Empty<byte>();
            public int OriginalOffset;
        }

        public byte[] Header = new byte[128];
        public List<Tag> Tags = new();

        public Tag? Find(string signature) => Tags.FirstOrDefault(t => t.Signature == signature);

        public static IccProfile Parse(byte[] file)
        {
            if (file.Length < 132) throw new InvalidDataException("Too short for an ICC profile.");
            if (Encoding.ASCII.GetString(file, 36, 4) != "acsp") throw new InvalidDataException("No 'acsp' signature: not an ICC profile.");
            var profile = new IccProfile { Header = file.Take(128).ToArray() };
            uint count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(128));
            if (count > 1000 || 132 + count * 12 > file.Length) throw new InvalidDataException("Invalid ICC tag count.");
            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 4));
                uint size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
                if (offset + (ulong)size > (ulong)file.Length) throw new InvalidDataException("ICC tag outside the file.");
                profile.Tags.Add(new Tag
                {
                    Signature = Encoding.ASCII.GetString(file, entry, 4),
                    Data = file.AsSpan((int)offset, (int)size).ToArray(),
                    OriginalOffset = (int)offset
                });
            }
            return profile;
        }

        /// <summary>Writes the profile with a new size and profile ID. Tags that shared their data keep sharing it.</summary>
        public byte[] Serialize()
        {
            using var body = new MemoryStream();
            int tableEnd = 132 + Tags.Count * 12;
            var table = new byte[tableEnd - 128];
            BinaryPrimitives.WriteUInt32BigEndian(table, (uint)Tags.Count);
            var written = new Dictionary<int, (int Offset, byte[] Data)>();
            for (int i = 0; i < Tags.Count; i++)
            {
                var tag = Tags[i];
                int offset;
                if (tag.OriginalOffset != 0 && written.TryGetValue(tag.OriginalOffset, out var shared) && shared.Data.AsSpan().SequenceEqual(tag.Data))
                {
                    offset = shared.Offset;
                }
                else
                {
                    while ((tableEnd + body.Length) % 4 != 0) body.WriteByte(0);
                    offset = tableEnd + (int)body.Length;
                    body.Write(tag.Data);
                    if (tag.OriginalOffset != 0) written[tag.OriginalOffset] = (offset, tag.Data);
                }
                int entry = 4 + i * 12;
                Encoding.ASCII.GetBytes(tag.Signature, table.AsSpan(entry, 4));
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(entry + 4), (uint)offset);
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(entry + 8), (uint)tag.Data.Length);
            }
            while ((tableEnd + body.Length) % 4 != 0) body.WriteByte(0);

            var file = new byte[tableEnd + body.Length];
            Header.CopyTo(file, 0);
            table.CopyTo(file, 128);
            body.ToArray().CopyTo(file, tableEnd);
            BinaryPrimitives.WriteUInt32BigEndian(file, (uint)file.Length);
            Array.Clear(file, 84, 16);

            // ICC profile ID: MD5 of the file with the flags, rendering intent and ID fields set to zero
            var forHash = (byte[])file.Clone();
            Array.Clear(forHash, 44, 4);
            Array.Clear(forHash, 64, 4);
            MD5.HashData(forHash).CopyTo(file, 84);
            return file;
        }
    }

    /// <summary>The MHC2 tag: the matrix and per-channel LUTs Windows applies after composition.</summary>
    internal sealed class Mhc2Tag
    {
        public double MinNits;
        public double MaxNits;
        public double[] Matrix = new double[12];        // 3 x 4, row-major
        public double[][] Luts = new double[3][];

        private const double Fixed = 65536.0;

        public int EntryCount => Luts[0].Length;

        private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        private static double S15(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32BigEndian(data[at..]) / Fixed;
        private static bool IsSf32(ReadOnlySpan<byte> data, int at) => at + 4 <= data.Length && data[at] == 's' && data[at + 1] == 'f' && data[at + 2] == '3' && data[at + 3] == '2';

        public static Mhc2Tag Parse(byte[] tag)
        {
            var data = tag.AsSpan();
            if (tag.Length < 36 || Encoding.ASCII.GetString(tag, 0, 4) != "MHC2") throw new InvalidDataException("Not an MHC2 tag.");
            int entries = checked((int)U32(data, 8));
            if (entries < 2 || entries > 4096) throw new InvalidDataException($"Unexpected MHC2 LUT size: {entries}.");

            var result = new Mhc2Tag { MinNits = S15(data, 12), MaxNits = S15(data, 16) };
            int matrixAt = checked((int)U32(data, 20));
            if (IsSf32(data, matrixAt)) matrixAt += 8;
            if (matrixAt + 48 > tag.Length) throw new InvalidDataException("MHC2 matrix outside the tag.");
            for (int i = 0; i < 12; i++) result.Matrix[i] = S15(data, matrixAt + i * 4);

            for (int c = 0; c < 3; c++)
            {
                int at = checked((int)U32(data, 24 + c * 4));
                if (IsSf32(data, at)) at += 8;
                if (at + entries * 4 > tag.Length) throw new InvalidDataException("MHC2 LUT outside the tag.");
                result.Luts[c] = new double[entries];
                for (int i = 0; i < entries; i++) result.Luts[c][i] = S15(data, at + i * 4);
            }
            return result;
        }

        /// <summary>
        /// The tag in the layout the Windows HDR Calibration app writes: 36-byte header, the matrix right after it (no type
        /// header), then each LUT as an 'sf32' array.
        /// </summary>
        public byte[] Serialize()
        {
            int entries = EntryCount;
            if (entries < 2 || entries > 4096 || Luts.Any(l => l.Length != entries)) throw new InvalidDataException("Invalid MHC2 LUT size.");
            const int matrixAt = 36, firstLut = matrixAt + 48;
            int lutBytes = 8 + entries * 4;
            var tag = new byte[firstLut + 3 * lutBytes];
            Encoding.ASCII.GetBytes("MHC2", tag);
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), (uint)entries);
            WriteS15(tag, 12, MinNits);
            WriteS15(tag, 16, MaxNits);
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(20), matrixAt);
            for (int i = 0; i < 12; i++) WriteS15(tag, matrixAt + i * 4, Matrix[i]);
            for (int c = 0; c < 3; c++)
            {
                int at = firstLut + c * lutBytes;
                BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(24 + c * 4), (uint)at);
                Encoding.ASCII.GetBytes("sf32", tag.AsSpan(at));
                for (int i = 0; i < entries; i++) WriteS15(tag, at + 8 + i * 4, Luts[c][i]);
            }
            return tag;
        }

        private static void WriteS15(byte[] tag, int at, double value) =>
            BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(at), checked((int)Math.Round(value * Fixed)));

        /// <summary>True when every LUT runs straight from 0 to 1 (to within one 16-bit step).</summary>
        public bool IsIdentityLut() => Luts.All(lut =>
        {
            for (int i = 0; i < lut.Length; i++)
                if (Math.Abs(lut[i] - i / (lut.Length - 1.0)) > 1.0 / 65535) return false;
            return true;
        });
    }
}
