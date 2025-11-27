# Publish-Cli.ps1
# Builds FeatherPod CLI as a single self-contained executable

param(
    [string]$Runtime = "win-x64",
    [string]$OutputPath,
    [switch]$ShowR2RWarnings
)

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
