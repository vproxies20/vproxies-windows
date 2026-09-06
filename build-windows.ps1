param([switch]$Offline)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeDir = Join-Path $root 'runtime'
$licensesDir = Join-Path $root 'licenses'
$depsDir = Join-Path $root 'artifacts\dependencies'
$singBoxExe = Join-Path $runtimeDir 'sing-box.exe'
$wintunDll = Join-Path $runtimeDir 'wintun.dll'

$singBoxUrl = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.0/sing-box-1.14.0-windows-amd64.zip'
$singBoxSha256 = '3ffb56267da14e287be48bd10cf7e6505260125bad940b75101fbb4d5d58e5d6'
$wintunUrl = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
$wintunSha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'

function Get-VerifiedArchive([string]$Uri, [string]$Path, [string]$ExpectedSha256) {
  Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Path
  $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $ExpectedSha256) { Remove-Item $Path -Force; throw "SHA-256 mismatch for $Uri. Expected $ExpectedSha256; received $actual." }
}

New-Item -ItemType Directory -Force -Path $runtimeDir, $licensesDir, $depsDir | Out-Null
if (-not $Offline) {
  $singBoxZip = Join-Path $depsDir 'sing-box-1.14.0-windows-amd64.zip'
  $wintunZip = Join-Path $depsDir 'wintun-0.14.1.zip'
  Get-VerifiedArchive $singBoxUrl $singBoxZip $singBoxSha256
  Get-VerifiedArchive $wintunUrl $wintunZip $wintunSha256

  $singBoxExpanded = Join-Path $depsDir 'sing-box'
  $wintunExpanded = Join-Path $depsDir 'wintun'
  Remove-Item $singBoxExpanded, $wintunExpanded -Recurse -Force -ErrorAction SilentlyContinue
  Expand-Archive $singBoxZip $singBoxExpanded
  Expand-Archive $wintunZip $wintunExpanded

  $downloadedSingBox = Get-ChildItem $singBoxExpanded -Recurse -Filter 'sing-box.exe' | Select-Object -First 1
  $downloadedWintun = Get-ChildItem $wintunExpanded -Recurse -Filter 'wintun.dll' | Where-Object { $_.FullName -match '[\\/]amd64[\\/]' } | Select-Object -First 1
  if (-not $downloadedSingBox) { throw 'sing-box.exe was not found in the verified archive.' }
  if (-not $downloadedWintun) { throw 'AMD64 wintun.dll was not found in the verified archive.' }
  Copy-Item $downloadedSingBox.FullName $singBoxExe -Force
  Copy-Item $downloadedWintun.FullName $wintunDll -Force

  $singLicense = Get-ChildItem $singBoxExpanded -Recurse -File | Where-Object { $_.Name -match '^LICENSE' } | Select-Object -First 1
  $wintunLicense = Get-ChildItem $wintunExpanded -Recurse -File | Where-Object { $_.Name -match '^LICENSE' } | Select-Object -First 1
  if ($singLicense) { Copy-Item $singLicense.FullName (Join-Path $licensesDir 'sing-box-LICENSE.txt') -Force }
  if ($wintunLicense) { Copy-Item $wintunLicense.FullName (Join-Path $licensesDir 'wintun-LICENSE.txt') -Force }
}

if (-not (Test-Path $singBoxExe)) { throw 'Missing runtime\sing-box.exe. Run without -Offline to fetch the pinned official dependency.' }
if (-not (Test-Path $wintunDll)) { throw 'Missing runtime\wintun.dll. Run without -Offline to fetch the pinned signed dependency.' }
& $singBoxExe version
if ($LASTEXITCODE -ne 0) { throw "sing-box version check failed with exit code $LASTEXITCODE." }

$publish = Join-Path $root 'artifacts\publish'
dotnet publish (Join-Path $root 'src\VProxies.App\VProxies.App.csproj') -c Release -r win-x64 --self-contained true -o $publish `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 was not found.' }
& $iscc (Join-Path $root 'installer\VProxies.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

Get-FileHash (Join-Path $root 'artifacts\VProxiesSetup-1.0.2-win-x64.exe') -Algorithm SHA256
