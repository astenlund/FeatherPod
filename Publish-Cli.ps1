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
    "-p:DebugType=None"
)

if ($Net10) {
    # Temporarily rename global.json to allow .NET 10 SDK
    $globalJsonPath = Join-Path $PSScriptRoot "global.json"
    $globalJsonBackup = Join-Path $PSScriptRoot "global.json.bak"
    if (Test-Path $globalJsonPath) {
        Rename-Item $globalJsonPath $globalJsonBackup
    }

    # Temporarily modify csproj to target net10.0 (MSBuild property override doesn't work for restore)
    $csprojPath = Join-Path $PSScriptRoot "FeatherPod" "FeatherPod.csproj"
    $csprojBackup = Get-Content $csprojPath -Raw
    $csprojContent = $csprojBackup -replace '<TargetFramework>net9\.0</TargetFramework>', '<TargetFramework>net10.0</TargetFramework>'
    Set-Content $csprojPath $csprojContent -NoNewline
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

try {
    dotnet @publishArgs
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Write-Host "Publish failed with exit code $exitCode" -ForegroundColor Red
        exit $exitCode
    }

    Write-Host "`nPublished to: $finalOutputPath" -ForegroundColor Green
    Get-ChildItem $finalOutputPath | Format-Table Name, @{N='Size (kB)';E={[math]::Round($_.Length/1KB, 2)}}
} finally {
    if ($Net10) {
        # Restore csproj
        if ($csprojBackup) {
            Set-Content $csprojPath $csprojBackup -NoNewline
        }
        # Restore global.json
        if (Test-Path $globalJsonBackup) {
            Rename-Item $globalJsonBackup $globalJsonPath
        }
    }
}
