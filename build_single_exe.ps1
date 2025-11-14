#Requires -Version 5.0
<#
.SYNOPSIS
    VPM Single-File Build Script
.DESCRIPTION
    Builds VPM as a single-file executable with full control and better terminal management
#>

$ErrorActionPreference = "Continue"

$colors = @{
    Success = "Green"
    Error = "Red"
    Warning = "Yellow"
    Info = "Cyan"
}

function Write-Status {
    param([string]$Message, [string]$Type = "Info")
    $color = $colors[$Type]
    Write-Host $Message -ForegroundColor $color
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
}

Write-Section "VPM - Single-File Publish"

$buildStartTime = Get-Date
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptPath

$version = "unknown"
if (Test-Path "version.txt") {
    $version = Get-Content "version.txt" -Raw | ForEach-Object { $_.Trim() }
    Write-Status "Building version: $version" "Info"
} else {
    Write-Status "WARNING: version.txt not found" "Warning"
}

Write-Host "Checking for running instances of VPM..." -ForegroundColor Yellow
$runningProcesses = Get-Process -Name "VPM" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    Write-Status "WARNING: Found running instance(s) of VPM.exe" "Warning"
    Write-Host "Closing running instances..."
    try {
        Stop-Process -Name "VPM" -Force -ErrorAction Stop
        Write-Status "SUCCESS: Closed running instances" "Success"
        Start-Sleep -Seconds 2
    } catch {
        Write-Status "ERROR: Failed to close running instances. Please close manually." "Error"
        Read-Host "Press Enter to continue"
        exit 1
    }
} else {
    Write-Status "SUCCESS: No running instances found" "Success"
}

Write-Host "Verifying .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version 2>$null
    Write-Status ("SUCCESS: .NET SDK found: " + $dotnetVersion) "Success"
} catch {
    Write-Status "ERROR: .NET SDK not found! Please install .NET 10 SDK." "Error"
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "Cleaning previous artifacts..." -ForegroundColor Yellow
@("bin", "obj", ".build") | ForEach-Object {
    if (Test-Path $_) {
        Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Status "SUCCESS: Clean complete" "Success"

Write-Section "Publishing Single-File Executable"

$runtime = "win-x64"
$outputDir = ".build"
$singleFile = $true
$trimmed = $false
$debugType = "none"
$readyToRun = $false

Write-Host ("Publishing (" + $runtime + ") SINGLE_FILE=" + $singleFile + " TRIMMED=" + $trimmed + " DEBUG_TYPE=" + $debugType + " R2R=" + $readyToRun + " ...") -ForegroundColor Yellow

$publishArgs = @(
    "publish", "VPM.csproj",
    "--configuration", "Release",
    "-r", $runtime,
    "--self-contained", "false",
    "-p:PublishSingleFile=$singleFile",
    "-p:PublishTrimmed=$trimmed",
    "-p:PublishReadyToRun=$readyToRun",
    "-p:DebugType=$debugType",
    "-o", $outputDir,
    "--nologo", "--verbosity", "normal"
)

$publishOutput = @()
$publishResult = & dotnet $publishArgs 2>&1 | Tee-Object -Variable publishOutput
$buildSuccess = $LASTEXITCODE -eq 0

if (-not $buildSuccess) {
    Write-Status "ERROR: Publish failed." "Error"
    Write-Host ""
    Write-Host "Build Output:" -ForegroundColor Yellow
    Write-Host "=============" -ForegroundColor Yellow
    $publishOutput | ForEach-Object { Write-Host $_ }
    Write-Host ""
    Write-Host "Press C to copy errors to clipboard, or Enter to continue..." -ForegroundColor Cyan
    
    # Check for C key press
    $key = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    if ($key.Character -eq 'c' -or $key.Character -eq 'C') {
        Write-Host "Copying build errors to clipboard..." -ForegroundColor Green
        $publishOutput | Set-Clipboard
        Write-Status "SUCCESS: Build errors copied to clipboard!" "Success"
        Write-Host ""
        Read-Host "Press Enter to continue"
    }
}

# Count warnings and errors in output
$warningCount = 0
$errorCount = 0
foreach ($line in $publishOutput) {
    if ($line -match "warning CS\d+") { $warningCount++ }
    if ($line -match "error CS\d+") { $errorCount++ }
}

if ($buildSuccess) {
    $statusMsg = "SUCCESS: Publish completed successfully"
    if ($warningCount -gt 0 -or $errorCount -gt 0) {
        $statusMsg += " - Warnings: $warningCount, Errors: $errorCount"
    }
    Write-Status $statusMsg "Success"
} else {
    $statusMsg = "FAILED: Publish failed - Warnings: $warningCount, Errors: $errorCount"
    Write-Status $statusMsg "Error"
}

Write-Section "Archiving Source Code"

$archiveDir = "..\archives"
if (-not (Test-Path $archiveDir)) {
    New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
}

$archiveName = "VPM-v" + $version + "-source.zip"
$archivePath = Join-Path $archiveDir $archiveName
$tempArchiveDir = Join-Path $env:TEMP ("VPM_Archive_" + (Get-Random))

Write-Host ("Creating archive: " + $archiveName) -ForegroundColor Yellow
New-Item -ItemType Directory -Path $tempArchiveDir -Force | Out-Null

try {
    $robocopyArgs = @(
        ".", $tempArchiveDir,
        "/E", "/XD", "bin", "obj", ".build", ".git", ".vs", "_links", "_scripts",
        "/XF", "*.user", "*.suo",
        "/NFL", "/NDL", "/NJH", "/NJS"
    )
    & robocopy @robocopyArgs | Out-Null
    
    Compress-Archive -Path "$tempArchiveDir\*" -DestinationPath $archivePath -Force
    Write-Status ("SUCCESS: Source archived to: " + $archivePath) "Success"
} catch {
    Write-Status "WARNING: Archive creation failed, but build was successful." "Warning"
} finally {
    Remove-Item $tempArchiveDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Section "Copying Configuration Files"

$configFiles = @{
    "urls.csv" = "$outputDir\urls.csv"
    "urls.json" = "$outputDir\urls.json"
    "VPM.json" = "$outputDir\VPM.json"
}

foreach ($file in $configFiles.GetEnumerator()) {
    if (Test-Path $file.Key) {
        Copy-Item $file.Key $file.Value -Force
        Write-Status ("SUCCESS: Copied " + $file.Key) "Success"
    }
}

if (Test-Path "_links\VPM.bin") {
    Copy-Item "_links\VPM.bin" "$outputDir\VPM.bin" -Force
    Write-Status "SUCCESS: Copied VPM.bin (offline database)" "Success"
} else {
    Write-Status "INFO: VPM.bin not found in _links folder (offline mode disabled)" "Info"
}

Write-Host "Cleaning up extracted native DLLs from output folder..." -ForegroundColor Yellow
@("$outputDir\*.dll") | ForEach-Object {
    Get-Item $_ -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
Write-Status "SUCCESS: Cleaned up extracted DLLs" "Success"

@("bin", "obj") | ForEach-Object {
    if (Test-Path $_) {
        Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$buildEndTime = Get-Date
$duration = ($buildEndTime - $buildStartTime).TotalSeconds
$duration = [Math]::Round($duration, 1)

if (Test-Path "code_metrics.txt") {
    try {
        $content = Get-Content "code_metrics.txt"
        if ($content[-1] -match '^[0-9]') {
            $durationStr = " | " + [string]$duration + "s"
            $content[-1] = $content[-1] -replace ' \| 0s$', $durationStr
            Set-Content "code_metrics.txt" $content
            Write-Status ("Build time: " + [string]$duration + "s") "Success"
        }
    } catch {
        # Silently continue if metrics update fails
    }
}

Write-Section "Build Results"

$exePath = $null
$fileSize = 0
$fileSizeMB = 0

if ($buildSuccess) {
    $exePath = Join-Path $outputDir "VPM.exe"
    if (Test-Path $exePath) {
        $fileSize = (Get-Item $exePath).Length
        $fileSizeMB = [Math]::Round($fileSize / 1MB, 2)
        Write-Status "Single-file executable created: $exePath" "Success"
        $sizeStr = [string]$fileSizeMB + " MB - " + [string]$fileSize + " bytes"
        Write-Host "File size: $sizeStr" -ForegroundColor Cyan
    } else {
        Write-Status "Could not locate the executable in $outputDir" "Error"
        $buildSuccess = $false
    }
}

$menuActive = $true
while ($menuActive) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    if ($buildSuccess) {
        Write-Host "Build completed successfully!" -ForegroundColor Cyan
    } else {
        Write-Host "Build FAILED!" -ForegroundColor Red
    }
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Options:" -ForegroundColor Yellow
    if ($buildSuccess) {
        Write-Host "  [L] Launch the application" -ForegroundColor White
    }
    Write-Host "  [X] Exit" -ForegroundColor White
    Write-Host "  [C] Copy build info to clipboard" -ForegroundColor White
    Write-Host ""
    
    if ($buildSuccess) {
        $choice = Read-Host "Your choice (L/X/C)"
    } else {
        $choice = Read-Host "Your choice (X/C)"
    }
    
    switch ($choice.ToUpper()) {
        "L" {
            if ($buildSuccess) {
                Write-Host ""
                Write-Status "Launching VPM..." "Info"
                & $exePath
                Write-Status "SUCCESS: Application launched!" "Success"
                Start-Sleep -Seconds 2
                $menuActive = $false
            } else {
                Write-Status "Cannot launch - build failed" "Error"
            }
        }
        "X" {
            Write-Host ""
            Write-Status "Exiting without launching." "Info"
            Start-Sleep -Seconds 1
            $menuActive = $false
        }
        "C" {
            Write-Host ""
            Write-Status "Copying build information to clipboard..." "Info"
            
            $buildInfo = @(
                "VPM Build Summary"
                "=================="
                ""
                ("Build Version: " + $version)
                ("Build Date: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
                ("Build Duration: " + [string]$duration + "s")
                ("Output Directory: " + (Resolve-Path $outputDir))
                ("Runtime: " + $runtime)
                ""
                "Configuration:"
                ("  Single File: " + $singleFile)
                ("  Trimmed: " + $trimmed)
                ("  Debug Type: " + $debugType)
                ("  Ready to Run: " + $readyToRun)
                ""
                "Executable: VPM.exe"
                ("Size: " + [string]$fileSizeMB + " MB - " + [string]$fileSize + " bytes")
                ""
                ("Build completed at: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
            )
            
            if ($publishOutput.Count -gt 0) {
                $buildInfo += ""
                $buildInfo += "Build Statistics:"
                $buildInfo += "================="
                $buildInfo += ("Warnings: " + [string]$warningCount)
                $buildInfo += ("Errors: " + [string]$errorCount)
                $buildInfo += ""
                $buildInfo += "Build Output:"
                $buildInfo += "============="
                $buildInfo += $publishOutput
            }
            
            $finalBuildInfo = $buildInfo -join [Environment]::NewLine
            $finalBuildInfo | Set-Clipboard
            Write-Status "SUCCESS: Build information copied to clipboard!" "Success"
            Write-Host ""
            Write-Host "Press Enter to continue..." -ForegroundColor Yellow
            Read-Host
        }
        default {
            Write-Status "ERROR: Invalid choice. Please enter L, X, or C." "Error"
        }
    }
}

Write-Host ""
Write-Status "Build script completed." "Success"
