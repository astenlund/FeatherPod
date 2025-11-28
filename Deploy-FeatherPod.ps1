<#
.SYNOPSIS
    Publishes and deploys FeatherPod to Azure App Service and Function App.

.DESCRIPTION
    Builds the project, creates deployment packages, deploys App Service and Function App to Azure,
    and optionally deploys infrastructure via Bicep.

.PARAMETER Environment
    Target environment: Test, Prod, or All (defaults to Prod).

.PARAMETER Infrastructure
    Include Bicep infrastructure deployment. Use this for initial setup or infrastructure changes.

.EXAMPLE
    .\Deploy-FeatherPod.ps1
    Deploys code to production (App Service + Function App)

.EXAMPLE
    .\Deploy-FeatherPod.ps1 -Environment Test
    Deploys code to test environment

.EXAMPLE
    .\Deploy-FeatherPod.ps1 -Environment Test -Infrastructure
    Deploys infrastructure and code to test environment

.EXAMPLE
    .\Deploy-FeatherPod.ps1 -Environment All
    Deploys code to both test and production environments
#>

param(
    [Parameter(Mandatory)]
    [ValidateSet("Test", "Prod", "All")]
    [string]$Environment,

    [switch]$Infrastructure
)

$ErrorActionPreference = "Stop"

function Main {
    param(
        [string]$Environment,
        [switch]$Infrastructure
    )

    if ($Environment -eq "All") {
        $environments = @("Test", "Prod")
        foreach ($env in $environments) { Deploy-Environment -TargetEnvironment $env -Infrastructure:$Infrastructure }
        Write-Host "`n======================================" -ForegroundColor Green
        Write-Host "  All deployments completed!" -ForegroundColor Green
        Write-Host "======================================`n" -ForegroundColor Green
    }
    else {
        Deploy-Environment -TargetEnvironment $Environment -Infrastructure:$Infrastructure
    }
}

# Deployment function for a single environment
function Deploy-Environment {
    param(
        [string]$TargetEnvironment,
        [switch]$Infrastructure
    )

    $ResourceGroup = switch ($TargetEnvironment) {
        "Test" { "featherpod-test-rg" }
        "Prod" { "featherpod-rg" }
    }

    $AppName = switch ($TargetEnvironment) {
        "Test" { "featherpod-test" }
        "Prod" { "featherpod" }
    }

    $FunctionAppName = switch ($TargetEnvironment) {
        "Test" { "featherpod-test-func" }
        "Prod" { "featherpod-func" }
    }

    $ParametersFile = switch ($TargetEnvironment) {
        "Test" { "parameters-test.json" }
        "Prod" { "parameters.json" }
    }

    $serverProjectPath = Join-Path $PSScriptRoot "FeatherPod.Server\FeatherPod.Server.csproj"
    $functionsProjectPath = Join-Path $PSScriptRoot "FeatherPod.Functions\FeatherPod.Functions.csproj"
    $publishPath = Join-Path $PSScriptRoot "publish"
    $funcPublishPath = Join-Path $PSScriptRoot "publish-func"
    $zipPath = Join-Path $PSScriptRoot "deploy.zip"

    Write-Host "`n======================================" -ForegroundColor Magenta
    Write-Host "  Deploying to: $TargetEnvironment" -ForegroundColor Magenta
    Write-Host "  Resource Group: $ResourceGroup" -ForegroundColor Magenta
    Write-Host "  App Service: $AppName" -ForegroundColor Magenta
    Write-Host "  Function App: $FunctionAppName" -ForegroundColor Magenta
    if ($Infrastructure) {
        Write-Host "  Infrastructure: Yes (Bicep)" -ForegroundColor Magenta
    }
    Write-Host "======================================`n" -ForegroundColor Magenta

    try {
        # 1. Deploy infrastructure (if requested)
        if ($Infrastructure) {
            Write-Host "Deploying infrastructure via Bicep...`n" -ForegroundColor Cyan

            $secretsFile = Join-Path $PSScriptRoot "infrastructure\parameters.secrets.json"
            if (-not (Test-Path $secretsFile)) {
                throw "Secrets file not found: $secretsFile. Create it with internalApiKey parameter."
            }

            az deployment group create `
                --resource-group $ResourceGroup `
                --template-file (Join-Path $PSScriptRoot "infrastructure\main.bicep") `
                --parameters (Join-Path $PSScriptRoot "infrastructure\$ParametersFile") `
                --parameters $secretsFile
            if ($LASTEXITCODE -ne 0) {
                throw "Bicep deployment failed with exit code $LASTEXITCODE"
            }
            Write-Host "`nInfrastructure deployment complete.`n" -ForegroundColor Green
        }

        # 2. Build solution
        Write-Host "Building solution...`n" -ForegroundColor Cyan
        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }

        # 3. Deploy App Service
        Write-Host "`nPublishing App Service...`n" -ForegroundColor Cyan
        dotnet publish $serverProjectPath -c Release -o $publishPath --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish (Server) failed with exit code $LASTEXITCODE"
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

        Write-Host "Deploying to Azure App Service...`n" -ForegroundColor Cyan
        az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zipPath --type zip --clean true --async true
        if ($LASTEXITCODE -ne 0) {
            throw "az webapp deploy failed with exit code $LASTEXITCODE"
        }

        # Give the app a moment to start, then verify it's running
        Write-Host "Waiting for App Service to start..." -ForegroundColor Yellow
        Start-Sleep -Seconds 30
        $versionUrl = "https://$AppName.azurewebsites.net/api/version"
        $maxAttempts = 10
        $attempt = 0
        while ($attempt -lt $maxAttempts) {
            $attempt++
            try {
                $response = Invoke-RestMethod -Uri $versionUrl -TimeoutSec 10 -ErrorAction Stop
                Write-Host "App Service is running: v$($response.version)" -ForegroundColor Green
                break
            }
            catch {
                if ($attempt -eq $maxAttempts) {
                    Write-Host "Warning: App Service health check timed out. It may still be starting." -ForegroundColor Yellow
                }
                else {
                    Write-Host "  Attempt $attempt/$maxAttempts - waiting..." -ForegroundColor Gray
                    Start-Sleep -Seconds 15
                }
            }
        }

        # 4. Deploy Function App
        Write-Host "`nPublishing Function App...`n" -ForegroundColor Cyan
        dotnet publish $functionsProjectPath -c Release -o $funcPublishPath --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish (Functions) failed with exit code $LASTEXITCODE"
        }

        Write-Host "Deploying to Azure Function App...`n" -ForegroundColor Cyan
        Push-Location $funcPublishPath
        try {
            func azure functionapp publish $FunctionAppName --dotnet-isolated
            if ($LASTEXITCODE -ne 0) {
                throw "func azure functionapp publish failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Pop-Location
        }

        # Ensure .NET 10 runtime (func CLI defaults to 8.0)
        Write-Host "Setting Function App runtime to .NET 10..." -ForegroundColor Yellow
        az functionapp config set --name $FunctionAppName --resource-group $ResourceGroup --net-framework-version v10.0 --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Warning: Failed to set .NET version. Function may use .NET 8." -ForegroundColor Yellow
        }

        Write-Host "`nDeployment successful!`n" -ForegroundColor Green
        Write-Host "App Service URL: https://$AppName.azurewebsites.net" -ForegroundColor Yellow
        Write-Host "Function App URL: https://$FunctionAppName.azurewebsites.net" -ForegroundColor Yellow
    }
    catch {
        Write-Error "Deployment to $TargetEnvironment failed: $_"
        throw
    }
    finally {
        # Clean up
        Write-Host "`nCleaning up...`n" -ForegroundColor Cyan

        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
            Write-Host "Removed $zipPath" -ForegroundColor Gray
        }

        if (Test-Path $publishPath) {
            Remove-Item $publishPath -Recurse -Force
            Write-Host "Removed $publishPath" -ForegroundColor Gray
        }

        if (Test-Path $funcPublishPath) {
            Remove-Item $funcPublishPath -Recurse -Force
            Write-Host "Removed $funcPublishPath" -ForegroundColor Gray
        }

        Write-Host "`nCleanup complete.`n" -ForegroundColor Green
    }
}

Main @PSBoundParameters
