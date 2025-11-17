<#
.SYNOPSIS
    Publishes and deploys FeatherPod to Azure App Service.

.DESCRIPTION
    Builds the project, creates a deployment package, deploys to Azure, and cleans up temporary files.

.PARAMETER Environment
    Target environment: Test, Prod, or All (defaults to Prod).

.EXAMPLE
    .\Deploy-FeatherPod.ps1
    Deploys to production (featherpod)

.EXAMPLE
    .\Deploy-FeatherPod.ps1 -Environment Test
    Deploys to test environment (featherpod-test)

.EXAMPLE
    .\Deploy-FeatherPod.ps1 -Environment All
    Deploys to both test and production environments
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Test", "Prod", "All")]
    [string]$Environment
)

$ErrorActionPreference = "Stop"

function Main {
    param(
        [string]$Environment
    )

    if ($Environment -eq "All") {
        $environments = @("Test", "Prod")
        foreach ($env in $environments) { Deploy-Environment -TargetEnvironment $env }
        Write-Host "`n======================================" -ForegroundColor Green
        Write-Host "  All deployments completed!" -ForegroundColor Green
        Write-Host "======================================`n" -ForegroundColor Green
    }
    else {
        Deploy-Environment -TargetEnvironment $Environment
    }
}

# Deployment function for a single environment
function Deploy-Environment {
    param(
        [string]$TargetEnvironment
    )

    $ResourceGroup = switch ($TargetEnvironment) {
        "Test" { "featherpod-test-rg" }
        "Prod" { "featherpod-rg" }
    }

    $AppName = switch ($TargetEnvironment) {
        "Test" { "featherpod-test" }
        "Prod" { "featherpod" }
    }

    $projectPath = Join-Path $PSScriptRoot "FeatherPod\FeatherPod.csproj"
    $publishPath = Join-Path $PSScriptRoot "publish"
    $zipPath = Join-Path $PSScriptRoot "deploy.zip"

    Write-Host "`n======================================" -ForegroundColor Magenta
    Write-Host "  Deploying to: $TargetEnvironment" -ForegroundColor Magenta
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
        Write-Error "Deployment to $TargetEnvironment failed: $_"
        throw
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
}

Main @PSBoundParameters
