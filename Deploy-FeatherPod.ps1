<#
.SYNOPSIS
    Publishes and deploys FeatherPod to Azure App Service.

.DESCRIPTION
    Builds the project, creates a deployment package, deploys to Azure, and cleans up temporary files.

.PARAMETER Environment
    Target environment: Test or Prod (defaults to Prod).

.EXAMPLE
    .\Deploy-FeatherPod.ps1
    Deploys to production (featherpod)

.EXAMPLE
    .\Deploy-FeatherPod.ps1 -Environment Test
    Deploys to test environment (featherpod-test)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Test", "Prod")]
    [string]$Environment
)

# Set resource group and app name based on environment
$ResourceGroup = switch ($Environment) {
    "Test" { "featherpod-test-rg" }
    "Prod" { "featherpod-rg" }
}

$AppName = switch ($Environment) {
    "Test" { "featherpod-test" }
    "Prod" { "featherpod" }
}

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "FeatherPod\FeatherPod.csproj"
$publishPath = Join-Path $PSScriptRoot "publish"
$zipPath = Join-Path $PSScriptRoot "deploy.zip"

Write-Host "`n======================================" -ForegroundColor Magenta
Write-Host "  Deploying to: $Environment" -ForegroundColor Magenta
Write-Host "  Resource Group: $ResourceGroup" -ForegroundColor Magenta
Write-Host "  App Name: $AppName" -ForegroundColor Magenta
Write-Host "======================================`n" -ForegroundColor Magenta

try {
    Write-Host "Publishing FeatherPod..." -ForegroundColor Cyan
    dotnet publish $projectPath -c Release -o $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host "`nCreating deployment package..." -ForegroundColor Cyan
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Push-Location $publishPath
    try {
        Compress-Archive -Path * -DestinationPath $zipPath -Force
    }
    finally {
        Pop-Location
    }

    Write-Host "`nDeploying to Azure App Service..." -ForegroundColor Cyan
    az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zipPath --type zip
    if ($LASTEXITCODE -ne 0) {
        throw "az webapp deploy failed with exit code $LASTEXITCODE"
    }

    Write-Host "`nDeployment successful!" -ForegroundColor Green
    Write-Host "App URL: https://$AppName.azurewebsites.net" -ForegroundColor Yellow
    Write-Host "Feed URL: https://$AppName.azurewebsites.net/feed.xml" -ForegroundColor Yellow
}
catch {
    Write-Error "Deployment failed: $_"
    exit 1
}
finally {
    # Clean up
    Write-Host "`nCleaning up..." -ForegroundColor Cyan

    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
        Write-Host "Removed $zipPath" -ForegroundColor Gray
    }

    if (Test-Path $publishPath) {
        Remove-Item $publishPath -Recurse -Force
        Write-Host "Removed $publishPath" -ForegroundColor Gray
    }

    Write-Host "Cleanup complete." -ForegroundColor Green
}
