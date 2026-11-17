# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Dominik Poschinger
# ue Bar – delivery (M7, SPEC 3; plugins: P2-1). Builds into app/dist/:
#   ueBar-Setup-<version>.exe           installer (Inno Setup, German/English), framework-dependent, downloads the .NET 10 Desktop Runtime if needed
#   ueBar-<version>-portable.exe        one file, self-contained, without trimming (WPF does not handle it reliably)
#   UeBar.Sdk.<api version>.nupkg       the SDK plugins are built against
#   ueBar-plugin-hello-<version>.zip    sample plugin (samples/HelloPlugin), to try "Add plugin …"
#   ueBar-plugin-dummy-<version>.zip    test module for developers (not part of the installer)
# The bundled plugins (Home Assistant) go to plugins\<id>\ next to the app; the portable exe carries
# them inside and unpacks them on the first start.
# The version is in one place: app/Directory.Build.props (<Version>).
# Call (from any folder):  powershell -ExecutionPolicy Bypass -File app/build/publish.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
$app = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $app "dist"
$stage = Join-Path $dist "stage"
$project = Join-Path $app "src/UeBar.App/UeBar.App.csproj"

# Version from Directory.Build.props
[xml]$props = Get-Content (Join-Path $app "Directory.Build.props") -Raw
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> in Directory.Build.props" }
Write-Host "ue Bar $version" -ForegroundColor Cyan

# Look for Inno Setup – if it is missing: say exactly what to do and stop.
$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup 6 is missing. Please install it and run the script again:" -ForegroundColor Yellow
    Write-Host "    winget install --id JRSoftware.InnoSetup -e"
    exit 1
}

if (-not $SkipTests) {
    Write-Host "Tests …" -ForegroundColor Cyan
    dotnet test (Join-Path $app "UeBar.sln") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $dist, $stage | Out-Null

# 1) Framework-dependent for the installer (ReadyToRun: faster cold start)
Write-Host "Publishing (installer variant) …" -ForegroundColor Cyan
$installDir = Join-Path $stage "app"
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $installDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (installer) failed" }
Get-ChildItem $installDir -Recurse -Include *.pdb, UeBar.Sdk.xml | Remove-Item
$pluginsDir = Join-Path $installDir "plugins"
if (-not (Test-Path (Join-Path $pluginsDir "bundled.sha256"))) { throw "Bundled plugins are missing in $pluginsDir" }

# The bundled plugins exactly as published (precompiled, with the checksum list) – for the portable exe.
$pluginsZip = Join-Path $stage "bundled-plugins.zip"
Compress-Archive -Path (Join-Path $pluginsDir "*") -DestinationPath $pluginsZip -Force

# 2) Portable: one file, self-contained, without trimming. Native WPF parts are extracted on the first start.
Write-Host "Publishing (portable single exe) …" -ForegroundColor Cyan
$portableDir = Join-Path $stage "portable"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:DebugType=none -p:StageBundledPlugins=false "-p:BundledPluginsZip=$pluginsZip" `
    -o $portableDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (portable) failed" }
$portable = Join-Path $dist "ueBar-$version-portable.exe"
Copy-Item (Join-Path $portableDir "ueBar.exe") $portable -Force

# 3) Installer
Write-Host "Building installer …" -ForegroundColor Cyan
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$installDir" "/DOutputDir=$dist" (Join-Path $app "installer/ueBar.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
$setup = Join-Path $dist "ueBar-Setup-$version.exe"

# 4) For plugin developers: SDK package, sample plugin, test module
Write-Host "Packing SDK and plugins …" -ForegroundColor Cyan
Get-ChildItem $dist -Filter "UeBar.Sdk.*.nupkg" | Remove-Item
dotnet pack (Join-Path $app "src/UeBar.Sdk/UeBar.Sdk.csproj") -c Release -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet pack (SDK) failed" }
$sdkPackage = Get-ChildItem $dist -Filter "UeBar.Sdk.*.nupkg" | Select-Object -First 1

$pluginZips = @()
foreach ($plugin in @("samples/HelloPlugin/HelloPlugin.csproj", "src/UeBar.Modules.Dummy/UeBar.Modules.Dummy.csproj")) {
    $packDir = Join-Path $stage ("pack-" + [IO.Path]::GetFileNameWithoutExtension($plugin))
    dotnet build (Join-Path $app $plugin) -c Release -t:PackPlugin "-p:PluginStageDir=$packDir" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "PackPlugin failed for $plugin" }
    foreach ($zip in Get-ChildItem $packDir -Filter *.zip) {
        $target = Join-Path $dist ("ueBar-plugin-" + $zip.Name)
        Copy-Item $zip.FullName $target -Force
        $pluginZips += Get-Item $target
    }
}

function Size($bytes) { "{0:N1} MB" -f ($bytes / 1MB) }
$installedBytes = (Get-ChildItem $installDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host ("  Installer        {0,-38} {1}" -f (Split-Path -Leaf $setup), (Size (Get-Item $setup).Length))
Write-Host ("  Portable         {0,-38} {1}" -f (Split-Path -Leaf $portable), (Size (Get-Item $portable).Length))
Write-Host ("  Installed        {0,-38} {1}" -f "%LOCALAPPDATA%\Programs\ueBar", (Size $installedBytes))
Write-Host ("  SDK package      {0,-38} {1:N0} KB" -f $sdkPackage.Name, ($sdkPackage.Length / 1KB))
foreach ($zip in $pluginZips) { Write-Host ("  Plugin           {0,-38} {1:N0} KB" -f $zip.Name, ($zip.Length / 1KB)) }
