<#
.SYNOPSIS
    Builds one TurboSuite Revit-version channel and publishes its deployment files
    to that version's subfolder on a network share, or rolls that subfolder back to
    a previously published version.

.DESCRIPTION
    TurboSuite ships a separate DLL per Revit version (net10 for 2026, net8 for 2025,
    net48 for 2024). The share is laid out with one subfolder per version:

        <ServerPath>\2026\   net10 TurboSuite.dll + updater + version.txt + Archive\
        <ServerPath>\2025\   net8 TurboSuite.dll + updater + version.txt + Archive\
        <ServerPath>\2024\   net48 TurboSuite.dll + updater + version.txt + Archive\
        <ServerPath>\        the (version-agnostic) combined TurboSuiteInstaller

    Run this script once per version you want to publish.

.PARAMETER ServerPath
    The UNC path to the share root (e.g., \\SERVER\TurboSuite). Version subfolders live under it.

.PARAMETER RevitVersion
    Which Revit channel to publish: "2024", "2025", or "2026".

.PARAMETER Version
    The version string to write to that channel's version.txt (e.g., 1.1.0). Prompted if omitted.
    Ignored when -Rollback is used.

.PARAMETER Rollback
    If specified, skips build/publish and restores <ServerPath>\<RevitVersion>\ from an archived
    version (must exist under <ServerPath>\<RevitVersion>\Archive\).

.EXAMPLE
    .\publish.ps1 -ServerPath "\\SERVER\TurboSuite" -RevitVersion 2025 -Version "1.1.0"
    Publish the Revit 2025 channel 1.1.0 to <ServerPath>\2025\.

.EXAMPLE
    .\publish.ps1 -ServerPath "\\SERVER\TurboSuite" -RevitVersion 2024 -Rollback "1.0.0"
    Restore the Revit 2024 channel to the archived 1.0.0 build.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitVersion,

    [string]$Version,

    [string]$Rollback
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$installerCsproj = Join-Path $projectRoot "Installer\TurboSuiteInstaller.csproj"

# Per-version layout: each version gets its own share subfolder + its own Archive\.
$versionShare = Join-Path $ServerPath $RevitVersion
$archiveRoot = Join-Path $versionShare "Archive"

# Per-version build outputs. The shim project + target framework differ by version:
# 2024 = net48 (Revit 2024), 2025 = net8 (Revit 2025), 2026 = net10 (Revit 2026).
$tfm = switch ($RevitVersion) {
    "2024" { "net48" }
    "2025" { "net8.0-windows" }
    "2026" { "net10.0-windows" }
}
$shimProjDir = Join-Path $projectRoot "Revit$RevitVersion"
$shimProj = Join-Path $shimProjDir "TurboSuite.Revit$RevitVersion.csproj"
$addinFile = Join-Path $shimProjDir "TurboSuite.addin"
$mainBinDir = Join-Path $shimProjDir "bin\Release\$tfm"
$updaterBinDir = Join-Path $projectRoot "Updater\bin\Release\$tfm"
# net48 emits a self-contained managed exe; net8 emits exe + dll + runtimeconfig.
$updaterFiles = if ($RevitVersion -eq "2024") {
    @("TurboSuiteUpdater.exe")
} else {
    @("TurboSuiteUpdater.exe", "TurboSuiteUpdater.dll", "TurboSuiteUpdater.runtimeconfig.json")
}

function Get-DeployedVersion {
    $versionFile = Join-Path $versionShare "version.txt"
    if (Test-Path $versionFile) {
        return (Get-Content $versionFile -Raw).Trim()
    }
    return $null
}

function Copy-ShareToArchive {
    param([string]$ArchiveName)
    $dest = Join-Path $archiveRoot $ArchiveName
    if (Test-Path $dest) {
        Write-Host "  Archive already exists at $dest - skipping."
        return
    }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Get-ChildItem -Path $versionShare -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $dest -Force
    }
    Write-Host "  Archived prior deployment to $dest"
}

# === Rollback path ===
if ($Rollback) {
    Write-Host ""
    Write-Host "=== TurboSuite Rollback (Revit $RevitVersion) ===" -ForegroundColor Cyan
    Write-Host "  Target version: $Rollback"
    Write-Host "  Channel share:  $versionShare"
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
        Write-Host "[1/2] No version.txt present - skipping snapshot." -ForegroundColor Yellow
    }

    Write-Host "[2/2] Restoring $Rollback from archive..." -ForegroundColor Yellow
    Get-ChildItem -Path $versionShare -File | ForEach-Object { Remove-Item $_.FullName -Force }
    Get-ChildItem -Path $archiveDir -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $versionShare -Force
        Write-Host "  Restored $($_.Name)"
    }

    Write-Host ""
    Write-Host "=== Rollback Complete ===" -ForegroundColor Green
    Write-Host "  Revit $RevitVersion channel restored to $Rollback. Users see it on next Revit launch." -ForegroundColor Cyan
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
Write-Host "=== TurboSuite Publish (Revit $RevitVersion) ===" -ForegroundColor Cyan
Write-Host "  Version:     $Version"
Write-Host "  Destination: $versionShare"
Write-Host ""

# Pre-flight: Verify CHANGELOG.md has an entry for this version
$changelogPath = Join-Path $projectRoot "CHANGELOG.md"
if (Test-Path $changelogPath) {
    $changelogContent = Get-Content $changelogPath -Raw
    if ($changelogContent -notmatch "\[$([regex]::Escape($Version))\]") {
        Write-Host "WARNING: CHANGELOG.md has no entry for version $Version." -ForegroundColor Red
        Write-Host "  Add a '## [$Version]' section before publishing." -ForegroundColor Red
        $proceed = Read-Host "  Continue anyway? (y/N)"
        if ($proceed -ne "y") {
            Write-Host "Aborted. Update CHANGELOG.md and try again."
            exit 1
        }
    } else {
        Write-Host "  CHANGELOG.md entry found for $Version." -ForegroundColor DarkGray
    }
} else {
    Write-Warning "CHANGELOG.md not found at $changelogPath - skipping changelog check."
}

# Step 1: Build only this channel's shim project in Release. Building the shim csproj
# directly (rather than the whole solution) pulls its full dependency closure via
# ProjectReferences into bin\Release\$tfm\ while avoiding the shared-project (.shproj)
# and the other channel. SkipRevitDeploy=true suppresses the dev inner-loop post-build
# copy into the Revit addins folder, so publishing never depends on Revit being closed.
Write-Host "[1/6] Building the Revit $RevitVersion shim ($tfm) in Release mode..." -ForegroundColor Yellow
dotnet build $shimProj -c Release -p:SkipRevitDeploy=true
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# Verify the build actually produced this channel's output before we try to copy it.
$mainDll = Join-Path $mainBinDir "TurboSuite.dll"
if (-not (Test-Path $mainDll)) {
    Write-Error "Build reported success but $mainDll was not produced (expected output dir: $mainBinDir). Aborting."
    exit 1
}

# Step 2: Publish the combined installer as single-file (version-agnostic; lives at share root)
Write-Host "[2/6] Publishing installer..." -ForegroundColor Yellow
$installerPublishDir = Join-Path $projectRoot "Installer\publish"
dotnet publish $installerCsproj -c Release -o $installerPublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Installer publish failed."
    exit 1
}

# Step 3: Ensure server directories exist (share root + this version's subfolder + its Archive)
Write-Host "[3/6] Preparing server directory..." -ForegroundColor Yellow
foreach ($dir in @($ServerPath, $versionShare, $archiveRoot)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# Step 4: Archive currently-deployed version (if any) before overwriting
Write-Host "[4/6] Archiving prior deployment..." -ForegroundColor Yellow
$priorVersion = Get-DeployedVersion
if ($priorVersion) {
    if ($priorVersion -eq $Version) {
        Write-Error "Prior $RevitVersion deployment is already version $Version. Bump the version before republishing."
        exit 1
    }
    Copy-ShareToArchive -ArchiveName $priorVersion
} else {
    Write-Host "  No prior version.txt found - nothing to archive (first publish of this channel)."
}

# Step 5: Copy files to the version subfolder
Write-Host "[5/6] Copying files to $versionShare..." -ForegroundColor Yellow

# Copy main DLLs and PDBs (exclude Revit API DLLs)
$excludePatterns = @("RevitAPI.dll", "RevitAPIUI.dll", "Xceed.Wpf.AvalonDock.dll")
Get-ChildItem -Path $mainBinDir -Filter "*.dll" | Where-Object { $_.Name -notin $excludePatterns } | ForEach-Object {
    Copy-Item $_.FullName -Destination $versionShare -Force
    Write-Host "  Copied $($_.Name)"
}
Get-ChildItem -Path $mainBinDir -Filter "*.pdb" | ForEach-Object {
    Copy-Item $_.FullName -Destination $versionShare -Force
    Write-Host "  Copied $($_.Name)"
}

# Copy .addin manifest
Copy-Item $addinFile -Destination $versionShare -Force
Write-Host "  Copied TurboSuite.addin"

# Copy the version-matched updater
foreach ($updaterFile in $updaterFiles) {
    $updaterPath = Join-Path $updaterBinDir $updaterFile
    if (Test-Path $updaterPath) {
        Copy-Item $updaterPath -Destination $versionShare -Force
        Write-Host "  Copied $updaterFile"
    } else {
        Write-Error "$updaterFile not found at: $updaterPath"
        exit 1
    }
}

# Copy the retire manifest (cumulative "delete these stale files on update" list). The client
# stages it with everything else; TurboSuiteUpdater processes and removes it. Same file for all
# three channels — the managed dependency set is identical across them.
$retireFile = Join-Path $projectRoot "retire.txt"
if (Test-Path $retireFile) {
    Copy-Item $retireFile -Destination $versionShare -Force
    Write-Host "  Copied retire.txt"
}

# Copy the combined installer to the share ROOT (shared across versions)
if (Test-Path $installerPublishDir) {
    Get-ChildItem -Path $installerPublishDir -File | ForEach-Object {
        Copy-Item $_.FullName -Destination $ServerPath -Force
        Write-Host "  Copied (root) $($_.Name)"
    }
} else {
    Write-Error "Installer publish directory not found at: $installerPublishDir"
    exit 1
}

# Step 6: Write this channel's version.txt
Write-Host "[6/6] Writing version.txt..." -ForegroundColor Yellow
Set-Content -Path (Join-Path $versionShare "version.txt") -Value $Version -NoNewline
Write-Host "  Revit $RevitVersion version set to $Version"

# Summary
Write-Host ""
Write-Host "=== Publish Complete ===" -ForegroundColor Green
Write-Host "  Files deployed to: $versionShare"
Write-Host "  Version: $Version"
if ($priorVersion) {
    Write-Host "  Prior version $priorVersion archived to: $archiveRoot\$priorVersion"
    Write-Host "  To roll back: .\publish.ps1 -ServerPath `"$ServerPath`" -RevitVersion $RevitVersion -Rollback `"$priorVersion`"" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Users can now run TurboSuiteInstaller.exe from the share to install." -ForegroundColor Cyan
