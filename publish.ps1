<#
.SYNOPSIS
    Builds TurboSuite and publishes all deployment files to a network share.

.PARAMETER ServerPath
    The UNC path to the server share (e.g., \\SERVER\TurboSuite).

.PARAMETER Version
    The version string to write to version.txt (e.g., 1.1.0). If omitted, you will be prompted.

.EXAMPLE
    .\publish.ps1 -ServerPath "\\SERVER\TurboSuite" -Version "1.1.0"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,

    [string]$Version
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$sln = Join-Path $projectRoot "TurboSuite.sln"
$mainCsproj = Join-Path $projectRoot "TurboSuite.csproj"
$installerCsproj = Join-Path $projectRoot "Installer\TurboSuiteInstaller.csproj"
$addinFile = Join-Path $projectRoot "TurboSuite.addin"

# Prompt for version if not provided
if (-not $Version) {
    $Version = Read-Host "Enter the version to publish (e.g., 1.1.0)"
    if (-not $Version) {
        Write-Error "Version is required."
        exit 1
    }
}

Write-Host ""
Write-Host "=== TurboSuite Publish ===" -ForegroundColor Cyan
Write-Host "  Version:     $Version"
Write-Host "  Destination: $ServerPath"
Write-Host ""

# Step 1: Build solution in Release
Write-Host "[1/5] Building solution in Release mode..." -ForegroundColor Yellow
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# Step 2: Publish installer as single-file
Write-Host "[2/5] Publishing installer..." -ForegroundColor Yellow
$installerPublishDir = Join-Path $projectRoot "Installer\publish"
dotnet publish $installerCsproj -c Release -o $installerPublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Installer publish failed."
    exit 1
}

# Step 3: Ensure server directory exists
Write-Host "[3/5] Preparing server directory..." -ForegroundColor Yellow
if (-not (Test-Path $ServerPath)) {
    New-Item -ItemType Directory -Path $ServerPath -Force | Out-Null
}

# Step 4: Copy files to server share
Write-Host "[4/5] Copying files to server..." -ForegroundColor Yellow

$mainBinDir = Join-Path $projectRoot "bin\Release\net8.0-windows"
$updaterBinDir = Join-Path $projectRoot "Updater\bin\Release\net8.0-windows"

# Copy main DLLs and PDBs (exclude Revit API DLLs)
$excludePatterns = @("RevitAPI.dll", "RevitAPIUI.dll", "Xceed.Wpf.AvalonDock.dll")
Get-ChildItem -Path $mainBinDir -Filter "*.dll" | Where-Object { $_.Name -notin $excludePatterns } | ForEach-Object {
    Copy-Item $_.FullName -Destination $ServerPath -Force
    Write-Host "  Copied $($_.Name)"
}
Get-ChildItem -Path $mainBinDir -Filter "*.pdb" | ForEach-Object {
    Copy-Item $_.FullName -Destination $ServerPath -Force
    Write-Host "  Copied $($_.Name)"
}

# Copy .addin manifest
Copy-Item $addinFile -Destination $ServerPath -Force
Write-Host "  Copied TurboSuite.addin"

# Copy updater (try RID-specific path first, then plain)
$updaterExe = Join-Path $updaterBinDir "win-x64\TurboSuiteUpdater.exe"
if (-not (Test-Path $updaterExe)) {
    $updaterExe = Join-Path $updaterBinDir "TurboSuiteUpdater.exe"
}
if (Test-Path $updaterExe) {
    Copy-Item $updaterExe -Destination $ServerPath -Force
    Write-Host "  Copied TurboSuiteUpdater.exe"
} else {
    Write-Warning "TurboSuiteUpdater.exe not found - skipping."
}

# Copy installer files
if (Test-Path $installerPublishDir) {
    Get-ChildItem -Path $installerPublishDir -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $ServerPath -Force
        Write-Host "  Copied $($_.Name)"
    }
} else {
    Write-Warning "Installer publish directory not found at: $installerPublishDir - skipping."
}

# Step 5: Write version.txt
Write-Host "[5/5] Writing version.txt..." -ForegroundColor Yellow
Set-Content -Path (Join-Path $ServerPath "version.txt") -Value $Version -NoNewline
Write-Host "  Version set to $Version"

# Summary
Write-Host ""
Write-Host "=== Publish Complete ===" -ForegroundColor Green
Write-Host "  Files deployed to: $ServerPath"
Write-Host "  Version: $Version"
Write-Host ""
Write-Host "Users can now run TurboSuiteInstaller.exe from the share to install." -ForegroundColor Cyan
