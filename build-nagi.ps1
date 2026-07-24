# NagiBridge one-click build script
# Usage: paste this entire script into PowerShell and run

$ErrorActionPreference = "Stop"

# 1. Find Stardew Valley install
$gamePaths = @(
    "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
    "C:\Program Files\Steam\steamapps\common\Stardew Valley",
    "D:\Steam\steamapps\common\Stardew Valley",
    "D:\SteamLibrary\steamapps\common\Stardew Valley",
    "E:\SteamLibrary\steamapps\common\Stardew Valley",
    "E:\Steam\steamapps\common\Stardew Valley"
)

$GamePath = $null
foreach ($p in $gamePaths) {
    if (Test-Path "$p\Stardew Valley.dll") {
        $GamePath = $p
        break
    }
}

if (-not $GamePath) {
    Write-Host "Auto-detect failed. Drag your Stardew Valley folder here:" -ForegroundColor Yellow
    $GamePath = Read-Host "Game path"
    $GamePath = $GamePath.Trim('"')
    if (-not (Test-Path "$GamePath\Stardew Valley.dll")) {
        Write-Host "ERROR: Stardew Valley.dll not found in $GamePath" -ForegroundColor Red
        exit 1
    }
}
Write-Host "Found game: $GamePath" -ForegroundColor Green

# 2. Setup working directory (use timestamp to avoid locked file issues)
$workDir = "$env:TEMP\nagi-build-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

# 3. Download .NET SDK (reuse if recent build exists, otherwise download)
$dotnetDir = "$env:TEMP\nagi-dotnet-sdk"
if (-not (Test-Path "$dotnetDir\dotnet.exe")) {
    Write-Host "Downloading .NET SDK (first time only, ~200MB)..." -ForegroundColor Cyan
    $sdkUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.423/dotnet-sdk-8.0.423-win-x64.zip"
    $sdkZip = "$workDir\dotnet-sdk.zip"
    Invoke-WebRequest -Uri $sdkUrl -OutFile $sdkZip -UseBasicParsing
    Expand-Archive -Path $sdkZip -DestinationPath $dotnetDir
    Write-Host "SDK downloaded!" -ForegroundColor Green
} else {
    Write-Host "Using cached .NET SDK" -ForegroundColor Green
}
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_CLI_HOME = "$workDir\dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
Write-Host "SDK version: $(& "$dotnetDir\dotnet.exe" --version)" -ForegroundColor Green

# 4. Download NagiBridge source (from your fork)
Write-Host "Downloading NagiBridge source..." -ForegroundColor Cyan
$srcUrl = "https://github.com/738148680a-png/NagiBridge/archive/refs/heads/main.zip"
$srcZip = "$workDir\nagi-src.zip"
Invoke-WebRequest -Uri $srcUrl -OutFile $srcZip -UseBasicParsing
Expand-Archive -Path $srcZip -DestinationPath $workDir
$srcDir = "$workDir\NagiBridge-main"

# 5. Build
Write-Host "Building NagiBridge..." -ForegroundColor Cyan
$env:GAME_PATH = $GamePath
& "$dotnetDir\dotnet.exe" build "$srcDir\NagiBridge.csproj" -c Release /p:GamePath="$GamePath" /p:EnableModDeploy=false /p:EnableModZip=false 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED!" -ForegroundColor Red
    exit 1
}

# 6. Find and copy DLL
$dll = Get-ChildItem -Path "$srcDir" -Recurse -Filter "NagiBridge.dll" | Where-Object { $_.FullName -like "*Release*" } | Select-Object -First 1
if (-not $dll) {
    Write-Host "ERROR: Cannot find built NagiBridge.dll" -ForegroundColor Red
    exit 1
}

$modDir = "$GamePath\Mods\NagiBridge"
if (-not (Test-Path $modDir)) {
    Write-Host "ERROR: NagiBridge mod folder not found at $modDir" -ForegroundColor Red
    Write-Host "Copy manually from: $($dll.FullName)" -ForegroundColor Yellow
    exit 1
}

# Backup original
$backup = "$modDir\NagiBridge.dll.bak"
if (Test-Path "$modDir\NagiBridge.dll") {
    Copy-Item "$modDir\NagiBridge.dll" $backup -Force
    Write-Host "Backed up original DLL to $backup" -ForegroundColor Gray
}

Copy-Item $dll.FullName "$modDir\NagiBridge.dll" -Force
Write-Host "`nDONE! NagiBridge.dll updated in $modDir" -ForegroundColor Green
Write-Host "Restart Stardew Valley to use the new version." -ForegroundColor Green
Write-Host "`nNew features:" -ForegroundColor Cyan
Write-Host "  - /cast endpoint (ranged staff attack)" -ForegroundColor White
Write-Host "  - Sleep fix (dynamic bed position for Cabin)" -ForegroundColor White
Write-Host ""
Read-Host "Press Enter to close"
