# Publish-Cli.ps1
# Builds FeatherPod CLI as a single self-contained executable

param(
    [string]$Runtime = "win-x64",
    [string]$OutputPath,
    [switch]$ShowR2RWarnings
)

# 1. Publish the NativeAOT bridge first (it gets embedded as a resource)
Write-Host "Publishing FeatherPod.Bridge (NativeAOT)..." -ForegroundColor Cyan
$bridgePublishArgs = @(
    "publish", "FeatherPod.Bridge",
    "-c", "Release",
    "-p:DebugType=None"
)

dotnet @bridgePublishArgs
$bridgeExitCode = $LASTEXITCODE

if ($bridgeExitCode -ne 0) {
    Write-Host "Bridge publish failed with exit code $bridgeExitCode" -ForegroundColor Red
    exit $bridgeExitCode
}

$bridgeOutputPath = Join-Path "FeatherPod.Bridge" "bin" "Release" "net10.0-windows" "win-x64" "publish"
$bridgeExe = Join-Path $bridgeOutputPath "featherpod-bridge.exe"

if (Test-Path $bridgeExe) {
    $resourceDir = Join-Path "FeatherPod" "Resources"
    if (-not (Test-Path $resourceDir)) {
        New-Item -ItemType Directory -Path $resourceDir -Force | Out-Null
    }
    Copy-Item $bridgeExe -Destination $resourceDir -Force
    Write-Host "Copied featherpod-bridge.exe to FeatherPod/Resources/ (will be embedded)" -ForegroundColor Green
} else {
    Write-Host "Warning: featherpod-bridge.exe not found at $bridgeExe" -ForegroundColor Yellow
}

# 2. Publish the CLI (bridge is embedded as a resource)
Write-Host "`nPublishing FeatherPod CLI..." -ForegroundColor Cyan
$publishArgs = @(
    "publish", "FeatherPod",
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None"
)

if ($ShowR2RWarnings) {
    $publishArgs += "-p:PublishReadyToRunShowWarnings=true"
}

$defaultOutputPath = Join-Path "FeatherPod" "bin" "Release" "net10.0" $Runtime "publish"

if ($OutputPath) {
    $publishArgs += "-o"
    $publishArgs += $OutputPath
    $finalOutputPath = $OutputPath
} else {
    $finalOutputPath = $defaultOutputPath
}

dotnet @publishArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host "Publish failed with exit code $exitCode" -ForegroundColor Red
    exit $exitCode
}

Write-Host "`nPublished to: $finalOutputPath" -ForegroundColor Green
Get-ChildItem $finalOutputPath | Format-Table Name, @{N='Size (kB)';E={[math]::Round($_.Length/1KB, 2)}}
