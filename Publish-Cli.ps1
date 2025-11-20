# Publish-Cli.ps1
# Builds FeatherPod CLI as a single self-contained executable

param(
    [string]$Runtime = "win-x64",
    [string]$OutputPath,
    [switch]$Net10,
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
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

if ($Net10) {
    $publishArgs += "-p:TargetFramework=net10.0"
}

if ($ShowR2RWarnings) {
    $publishArgs += "-p:PublishReadyToRunShowWarnings=true"
}

$framework = if ($Net10) { "net10.0" } else { "net9.0" }
$defaultOutputPath = Join-Path "FeatherPod" "bin" "Release" $framework $Runtime "publish"

if ($OutputPath) {
    $publishArgs += "-o"
    $publishArgs += $OutputPath
    $finalOutputPath = $OutputPath
} else {
    $finalOutputPath = $defaultOutputPath
}

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`nPublished to: $finalOutputPath" -ForegroundColor Green
Get-ChildItem $finalOutputPath | Format-Table Name, @{N='Size (MB)';E={[math]::Round($_.Length/1MB, 2)}}
