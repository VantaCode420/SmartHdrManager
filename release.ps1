# ---------------------------------------------------------------------------
# Smart HDR Manager - release build
#
# Builds clean packages into the output folder (default "S:\Smart HDR Manager"):
#   SmartHdrManager-vX.Y.Z-win-x64               small exe, needs the .NET 10 Desktop Runtime (x64)
#   SmartHdrManager-vX.Y.Z-win-x64-standalone    larger exe, runs on any 64-bit Windows 10/11
#   SmartHdrManager-vX.Y.Z-win-arm64             small exe for Windows on ARM, needs the .NET 10 Desktop Runtime (Arm64)
#   SmartHdrManager-vX.Y.Z-win-arm64-standalone  larger exe for Windows on ARM, nothing to install
# plus a .zip of each, an installer and SHA256 checksums:
#   SmartHdrManager-vX.Y.Z-setup.exe             installs the standalone version that fits the PC, x64 or ARM64
#                                                (per user, no admin rights)
# With -Arch x64 / -Arch arm64 the installer holds that architecture only:
#   SmartHdrManager-vX.Y.Z-win-x64-setup.exe / SmartHdrManager-vX.Y.Z-win-arm64-setup.exe
#
# The installer needs Inno Setup 6.6 or newer (winget install JRSoftware.InnoSetup.7). Without it, or with
# -NoInstaller, only the zips are built.
#
# Only whitelisted files are copied (exe, README, CHANGELOG, LICENSE, userscript), so your own
# config.json, config.json.old, logs and backup folders can never end up in a release.
#
# WARNING: everything inside the output folder is deleted first.
#
# Run from PowerShell:
#   cd S:\Tools\SmartHdrManager
#   powershell -ExecutionPolicy Bypass -File .\release.ps1                  (x64 + ARM64)
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Arch x64        (x64 only)
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Arch arm64 -Output 'S:\Smart HDR Manager ARM'
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -NoInstaller            (zips only)
# ---------------------------------------------------------------------------
param(
    [string]$Output = 'S:\Smart HDR Manager',
    [ValidateSet('all', 'x64', 'arm64')]
    [string]$Arch = 'all',
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$project = Join-Path $src 'SmartHdrManager.csproj'

function Step($text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host ''; Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

# Inno Setup's command-line compiler: on the PATH, or in the default folders of Inno Setup 7 and 6
function Find-Iscc {
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $base) { continue }
        foreach ($name in 'Inno Setup 7', 'Inno Setup 6') {
            $path = Join-Path (Join-Path $base $name) 'ISCC.exe'
            if (Test-Path $path) { return $path }
        }
    }
    return $null
}

# ---- checks ----
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet was not found. Install the .NET 10 SDK.' }
if (-not (Test-Path $project)) { Fail "Project not found: $project" }
foreach ($f in 'README.md', 'LICENSE.txt', 'SmartHdrManager_YouTube.user.js') {
    if (-not (Test-Path (Join-Path $src $f))) { Fail "Missing $f next to release.ps1" }
}
# Never write the release into the source folder (it is a git repository)
$fullOut = [System.IO.Path]::GetFullPath($Output).TrimEnd('\')
$fullSrc = [System.IO.Path]::GetFullPath($src).TrimEnd('\')
if ($fullOut -eq $fullSrc -or $fullOut.StartsWith("$fullSrc\", [System.StringComparison]::OrdinalIgnoreCase) -or
    $fullSrc.StartsWith("$fullOut\", [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "The output folder must be outside the source folder: $Output"
}

$iss = Join-Path $src 'installer\SmartHdrManager.iss'
$iscc = $null
if (-not $NoInstaller) {
    if (-not (Test-Path $iss)) { Fail "Missing $iss" }
    $iscc = Find-Iscc
    if (-not $iscc) {
        Write-Host 'Inno Setup was not found: building the zips only. Install it to get the setup.exe files too:' -ForegroundColor Yellow
        Write-Host '  winget install JRSoftware.InnoSetup.7     (or run again with -NoInstaller to hide this message)' -ForegroundColor Yellow
    }
}

[xml]$xml = Get-Content $project
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { $version = '0.0.0' }
$archs = if ($Arch -eq 'all') { @('x64', 'arm64') } else { @($Arch) }
Write-Host "Smart HDR Manager $version  ($($archs -join ', '))" -ForegroundColor Green

# ---- clean build folders ----
Step 'Cleaning old build output'
$temp = Join-Path $env:TEMP 'SmartHdrManager-release'
foreach ($dir in @($temp, (Join-Path $src 'bin\Release'), (Join-Path $src 'obj'))) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Path $temp | Out-Null

# ---- build ----
$packages = @()
foreach ($a in $archs) {
    $rid = "win-$a"
    $common = @('-c', 'Release', '-r', $rid, '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '--nologo')

    Step "Building the small version for $rid (needs .NET 10 Desktop Runtime)"
    dotnet publish $project @common --self-contained false -o (Join-Path $temp "$a-fd")
    if ($LASTEXITCODE -ne 0) { Fail "Build failed (small version, $rid). See the messages above." }

    Step "Building the standalone version for $rid (no .NET install needed)"
    dotnet publish $project @common --self-contained true '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o (Join-Path $temp "$a-sc")
    if ($LASTEXITCODE -ne 0) { Fail "Build failed (standalone version, $rid). See the messages above." }

    $baseName = "SmartHdrManager-v$version-$rid"
    $packages += @{ Name = $baseName;              Build = "$a-fd" }
    $packages += @{ Name = "$baseName-standalone"; Build = "$a-sc" }
}

# ---- assemble packages (whitelist only) ----
Step "Writing packages to $Output"
# A test copy started from the release folder locks its exe: close it first
$running = Get-Process -Name 'SmartHdrManager' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($fullOut, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host 'Closing the test copy that is running from the release folder ...' -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}
if (Test-Path $Output) { Remove-Item (Join-Path $Output '*') -Recurse -Force }
else { New-Item -ItemType Directory -Path $Output | Out-Null }

foreach ($p in $packages) {
    $exe = Join-Path (Join-Path $temp $p.Build) 'SmartHdrManager.exe'
    if (-not (Test-Path $exe)) { Fail "Build output missing: $exe" }

    $dest = Join-Path $Output $p.Name
    New-Item -ItemType Directory -Path $dest | Out-Null
    Copy-Item $exe $dest
    Copy-Item (Join-Path $src 'README.md') $dest
    if (Test-Path (Join-Path $src 'CHANGELOG.md')) { Copy-Item (Join-Path $src 'CHANGELOG.md') $dest }
    Copy-Item (Join-Path $src 'LICENSE.txt') $dest
    Copy-Item (Join-Path $src 'SmartHdrManager_YouTube.user.js') $dest

    # Safety net: nothing personal may be in a release
    $bad = Get-ChildItem $dest -Recurse | Where-Object { $_.Name -match '^config\.json|\.log$|^log\.txt$|^portable\.txt$|\.bak$|^backup' }
    if ($bad) { Fail ("Personal files found in the package: " + ($bad.Name -join ', ')) }

    Compress-Archive -Path (Join-Path $dest '*') -DestinationPath (Join-Path $Output "$($p.Name).zip") -CompressionLevel Optimal
}

# ---- installer (standalone version: nothing else to install) ----
if ($iscc) {
    $standalone = { param($a) Join-Path $Output "SmartHdrManager-v$version-win-$a-standalone" }
    if ($Arch -eq 'all') {
        Step 'Building the installer (x64 + ARM64)'
        & $iscc '/Qp' '/DArch=all' "/DAppVersion=$version" "/DSourceDirX64=$(& $standalone 'x64')" "/DSourceDirArm64=$(& $standalone 'arm64')" "/DOutputDir=$fullOut" $iss
    }
    else {
        Step "Building the installer for win-$Arch"
        & $iscc '/Qp' "/DArch=$Arch" "/DAppVersion=$version" "/DSourceDir=$(& $standalone $Arch)" "/DOutputDir=$fullOut" $iss
    }
    if ($LASTEXITCODE -ne 0) { Fail 'Installer build failed. See the messages above.' }
}

# ---- checksums ----
# Full build: SHA256SUMS.txt. Single architecture: SHA256SUMS-x64.txt / SHA256SUMS-arm64.txt,
# so it can be uploaded next to an existing SHA256SUMS.txt without replacing it.
$sumsName = if ($Arch -eq 'all') { 'SHA256SUMS.txt' } else { "SHA256SUMS-$Arch.txt" }
Step "Writing SHA256 checksums ($sumsName)"
$sums = Get-ChildItem $Output -File | Where-Object { $_.Name -like '*.zip' -or $_.Name -like '*-setup.exe' } | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
}
$sums += Get-ChildItem $Output -Recurse -Filter 'SmartHdrManager.exe' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Directory.Name)\$($_.Name)"
}
Set-Content -Path (Join-Path $Output $sumsName) -Value $sums -Encoding ASCII

Remove-Item $temp -Recurse -Force

# ---- summary ----
Step 'Done'
Get-ChildItem $Output -Recurse -File | ForEach-Object {
    '{0,10:N0} KB  {1}' -f [math]::Ceiling($_.Length / 1KB), $_.FullName.Substring($fullOut.Length + 1)
}
Write-Host ''
Write-Host 'Before publishing:' -ForegroundColor Yellow
Write-Host '  1. Run the exes you can run once and do the quick test (ARM64 exes only start on Windows on ARM).'
Write-Host '  2. Upload the zips and the setup.exe to https://www.virustotal.com and keep the report links for your release notes.'
Write-Host "  3. Attach the zips, the setup.exe and $sumsName to the release."
