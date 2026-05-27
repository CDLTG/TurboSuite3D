<#
.SYNOPSIS
    Builds TurboSuite and publishes all deployment files to a network share,
    or rolls back the share to a previously published version.

.PARAMETER ServerPath
    The UNC path to the server share (e.g., \\SERVER\TurboSuite).

.PARAMETER Version
    The version string to write to version.txt (e.g., 1.1.0). If omitted, you will be prompted.
    Ignored when -Rollback is used.

.PARAMETER Rollback
    If specified, skips the build/publish and restores the share from an archived version.
    The value is the version string to restore (must exist under <ServerPath>\Archive\).

.EXAMPLE
    .\publish.ps1 -ServerPath "\\SERVER\TurboSuite" -Version "1.1.0"
    Publish 1.1.0. The currently-deployed version is archived to <ServerPath>\Archive\<prior-version>\.

.EXAMPLE
    .\publish.ps1 -ServerPath "\\SERVER\TurboSuite" -Rollback "1.0.0"
    Restore the share to the archived 1.0.0 build. Current files are first archived under
    <ServerPath>\Archive\<current-version>-rolledback-<timestamp>\ so the rollback is itself reversible.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,

    [string]$Version,

    [string]$Rollback
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$sln = Join-Path $projectRoot "TurboSuite.sln"
$mainCsproj = Join-Path $projectRoot "TurboSuite.csproj"
$installerCsproj = Join-Path $projectRoot "Installer\TurboSuiteInstaller.csproj"
$addinFile = Join-Path $projectRoot "TurboSuite.addin"
$archiveRoot = Join-Path $ServerPath "Archive"

function Get-DeployedVersion {
    $versionFile = Join-Path $ServerPath "version.txt"
    if (Test-Path $versionFile) {
        return (Get-Content $versionFile -Raw).Trim()
    }
    return $null
}

function Copy-ShareToArchive {
    param([string]$ArchiveName)
    $dest = Join-Path $archiveRoot $ArchiveName
    if (Test-Path $dest) {
        Write-Host "  Archive already exists at $dest — skipping."
        return
    }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Get-ChildItem -Path $ServerPath -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $dest -Force
    }
    Write-Host "  Archived prior deployment to $dest"
}

# === Rollback path ===
if ($Rollback) {
    Write-Host ""
    Write-Host "=== TurboSuite Rollback ===" -ForegroundColor Cyan
    Write-Host "  Target version: $Rollback"
    Write-Host "  Share:          $ServerPath"
    Write-Host ""

    $archiveDir = Join-Path $archiveRoot $Rollback
    if (-not (Test-Path $archiveDir)) {
        Write-Error "No archive found at $archiveDir. Available archives:"
        if (Test-Path $archiveRoot) {
            Get-ChildItem -Path $archiveRoot -Directory | ForEach-Object { Write-Host "  $($_.Name)" }
        } else {
            Write-Host "  (none)"
        }
        exit 1
    }

    # Snapshot current state before rollback so the rollback is reversible.
    $current = Get-DeployedVersion
    if ($current) {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $snapshotName = "$current-rolledback-$timestamp"
        Write-Host "[1/2] Snapshotting current $current deployment..." -ForegroundColor Yellow
        Copy-ShareToArchive -ArchiveName $snapshotName
    } else {
        Write-Host "[1/2] No version.txt present — skipping snapshot." -ForegroundColor Yellow
    }

    Write-Host "[2/2] Restoring $Rollback from archive..." -ForegroundColor Yellow
    Get-ChildItem -Path $ServerPath -File | ForEach-Object { Remove-Item $_.FullName -Force }
    Get-ChildItem -Path $archiveDir -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $ServerPath -Force
        Write-Host "  Restored $($_.Name)"
    }

    Write-Host ""
    Write-Host "=== Rollback Complete ===" -ForegroundColor Green
    Write-Host "  Share restored to $Rollback. Users will see this on their next Revit launch." -ForegroundColor Cyan
    exit 0
}

# === Publish path ===

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
Write-Host "[1/7] Building solution in Release mode..." -ForegroundColor Yellow
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# Step 2: Publish installer as single-file
Write-Host "[2/7] Publishing installer..." -ForegroundColor Yellow
$installerPublishDir = Join-Path $projectRoot "Installer\publish"
dotnet publish $installerCsproj -c Release -o $installerPublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Installer publish failed."
    exit 1
}

# Step 3: Ensure server directory exists
Write-Host "[3/7] Preparing server directory..." -ForegroundColor Yellow
if (-not (Test-Path $ServerPath)) {
    New-Item -ItemType Directory -Path $ServerPath -Force | Out-Null
}
if (-not (Test-Path $archiveRoot)) {
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
}

# Step 4: Archive currently-deployed version (if any) before overwriting
Write-Host "[4/7] Archiving prior deployment..." -ForegroundColor Yellow
$priorVersion = Get-DeployedVersion
if ($priorVersion) {
    if ($priorVersion -eq $Version) {
        Write-Error "Prior deployment is already version $Version. Bump the version before republishing."
        exit 1
    }
    Copy-ShareToArchive -ArchiveName $priorVersion
} else {
    Write-Host "  No prior version.txt found — nothing to archive (first publish)."
}

# Step 5: Copy files to server share
Write-Host "[5/7] Copying files to server..." -ForegroundColor Yellow

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

# Copy updater and its runtime files (try RID-specific path first, then plain)
$updaterDir = Join-Path $updaterBinDir "win-x64"
if (-not (Test-Path (Join-Path $updaterDir "TurboSuiteUpdater.exe"))) {
    $updaterDir = $updaterBinDir
}
$updaterFiles = @("TurboSuiteUpdater.exe", "TurboSuiteUpdater.dll", "TurboSuiteUpdater.runtimeconfig.json")
foreach ($updaterFile in $updaterFiles) {
    $updaterPath = Join-Path $updaterDir $updaterFile
    if (Test-Path $updaterPath) {
        Copy-Item $updaterPath -Destination $ServerPath -Force
        Write-Host "  Copied $updaterFile"
    } else {
        Write-Error "$updaterFile not found at: $updaterPath"
        exit 1
    }
}

# Copy installer files
if (Test-Path $installerPublishDir) {
    Get-ChildItem -Path $installerPublishDir -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $ServerPath -Force
        Write-Host "  Copied $($_.Name)"
    }
} else {
    Write-Error "Installer publish directory not found at: $installerPublishDir"
    exit 1
}

# Step 6: Tag the git commit
Write-Host "[6/7] Tagging git commit with v$Version..." -ForegroundColor Yellow
$gitExe = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitExe) {
    $gitExe = Get-Command "C:\Program Files\Git\bin\git.exe" -ErrorAction SilentlyContinue
}
if (-not $gitExe) {
    Write-Warning "git not found on PATH. Tag manually: git tag v$Version && git push origin v$Version"
} else {
    & $gitExe.Source tag "v$Version"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Git tag failed (tag may already exist). Skipping tag push."
    } else {
        & $gitExe.Source push origin "v$Version"
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to push tag to remote. You can push manually: git push origin v$Version"
        } else {
            Write-Host "  Tagged and pushed v$Version"
        }
    }
}

# Step 7: Write version.txt
Write-Host "[7/7] Writing version.txt..." -ForegroundColor Yellow
Set-Content -Path (Join-Path $ServerPath "version.txt") -Value $Version -NoNewline
Write-Host "  Version set to $Version"

# Summary
Write-Host ""
Write-Host "=== Publish Complete ===" -ForegroundColor Green
Write-Host "  Files deployed to: $ServerPath"
Write-Host "  Version: $Version"
if ($priorVersion) {
    Write-Host "  Prior version $priorVersion archived to: $archiveRoot\$priorVersion"
    Write-Host "  To roll back: .\publish.ps1 -ServerPath `"$ServerPath`" -Rollback `"$priorVersion`"" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Users can now run TurboSuiteInstaller.exe from the share to install." -ForegroundColor Cyan
