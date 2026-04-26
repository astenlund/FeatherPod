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

$script:publishPath = Join-Path $PSScriptRoot "publish"
$script:funcPublishPath = Join-Path $PSScriptRoot "publish-func"
$script:zipPath = Join-Path $PSScriptRoot "deploy.zip"

function Main {
    param(
        [string]$Environment,
        [switch]$Infrastructure
    )

    # Check for uncommitted changes - version number includes commit hash
    $gitStatus = git status --porcelain
    if ($gitStatus) {
        Write-Host "`nError: Git workspace has uncommitted changes.`n" -ForegroundColor Red
        Write-Host "The deployed version number includes the commit hash, which would not" -ForegroundColor Yellow
        Write-Host "reflect the actual content being deployed. Please commit or stash your" -ForegroundColor Yellow
        Write-Host "changes before deploying.`n" -ForegroundColor Yellow
        Write-Host "Changed files:" -ForegroundColor Gray
        $gitStatus | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        Write-Host ""
        throw "Deployment aborted: uncommitted changes detected."
    }

    $environments = if ($Environment -eq "All") { @("Test", "Prod") } else { @($Environment) }

    $needsBuild = -not $Infrastructure

    # Resolve the release branch remote up-front so any interactive prompt happens
    # before the long-running build and deploy steps. Skip on infrastructure-only
    # deploys -- no code is shipped, so the release branch must not move.
    $branchRemote = if ($needsBuild) { Get-ReleaseBranchRemote } else { $null }

    try {
        if ($needsBuild) {
            Build-Artifacts
        }

        foreach ($env in $environments) {
            Deploy-Environment -TargetEnvironment $env -Infrastructure:$Infrastructure -BranchRemote $branchRemote
        }

        if ($environments.Count -gt 1) {
            Write-Host "`n======================================" -ForegroundColor Green
            Write-Host "  All deployments completed!" -ForegroundColor Green
            Write-Host "======================================`n" -ForegroundColor Green
        }
    }
    finally {
        if ($needsBuild) {
            Remove-Artifacts
        }
    }
}

# Build solution, publish projects, and create deployment packages
function Build-Artifacts {
    Write-Host "`n======================================" -ForegroundColor Cyan
    Write-Host "  Building deployment artifacts" -ForegroundColor Cyan
    Write-Host "======================================`n" -ForegroundColor Cyan

    # Show commit hash so the user can verify before the build finishes
    $commitHash = git rev-parse --short HEAD
    $commitMsg = git log -1 --format=%s
    Write-Host "Deploying commit: $commitHash ($commitMsg)`n" -ForegroundColor Yellow

    # 1. Build solution
    Write-Host "Building solution...`n" -ForegroundColor Cyan
    dotnet build -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    # 2. Publish NativeAOT bridge (side effect for local CLI use)
    $bridgeProjectPath = Join-Path $PSScriptRoot "FeatherPod.Bridge\FeatherPod.Bridge.csproj"
    Write-Host "`nPublishing FeatherPod.Bridge (NativeAOT)..." -ForegroundColor Cyan
    dotnet publish $bridgeProjectPath -c Release -p:DebugType=None --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Bridge publish failed (non-fatal) - context menu may not work" -ForegroundColor Yellow
    }

    # 3. Publish App Service
    $serverProjectPath = Join-Path $PSScriptRoot "FeatherPod.Server\FeatherPod.Server.csproj"
    Write-Host "`nPublishing App Service...`n" -ForegroundColor Cyan
    dotnet publish $serverProjectPath -c Release -o $script:publishPath --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish (Server) failed with exit code $LASTEXITCODE"
    }

    # 4. Create App Service zip package
    Write-Host "`nCreating deployment package..." -ForegroundColor Cyan
    if (Test-Path $script:zipPath) {
        Remove-Item $script:zipPath -Force
    }

    Push-Location $script:publishPath
    try {
        Compress-Archive -Path * -DestinationPath $script:zipPath -Force
    }
    finally {
        Pop-Location
    }

    # 5. Publish Function App
    $functionsProjectPath = Join-Path $PSScriptRoot "FeatherPod.Functions\FeatherPod.Functions.csproj"
    Write-Host "`nPublishing Function App...`n" -ForegroundColor Cyan
    dotnet publish $functionsProjectPath -c Release -o $script:funcPublishPath --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish (Functions) failed with exit code $LASTEXITCODE"
    }

    Write-Host "`n======================================" -ForegroundColor Green
    Write-Host "  Build complete" -ForegroundColor Green
    Write-Host "======================================" -ForegroundColor Green
}

# Clean up build artifacts
function Remove-Artifacts {
    Write-Host "`nCleaning up...`n" -ForegroundColor Cyan

    if (Test-Path $script:zipPath) {
        Remove-Item $script:zipPath -Force
        Write-Host "Removed $script:zipPath" -ForegroundColor Gray
    }

    if (Test-Path $script:publishPath) {
        Remove-Item $script:publishPath -Recurse -Force
        Write-Host "Removed $script:publishPath" -ForegroundColor Gray
    }

    if (Test-Path $script:funcPublishPath) {
        Remove-Item $script:funcPublishPath -Recurse -Force
        Write-Host "Removed $script:funcPublishPath" -ForegroundColor Gray
    }

    Write-Host "`nCleanup complete.`n" -ForegroundColor Green
}

# Deploy pre-built artifacts to a single environment
function Deploy-Environment {
    param(
        [string]$TargetEnvironment,
        [switch]$Infrastructure,
        [string]$BranchRemote
    )

    $suffix = if ($TargetEnvironment -eq "Test") { "-test" } else { "" }
    $AppName          = "featherpod$suffix"
    $ResourceGroup    = "featherpod$suffix-rg"
    $FunctionAppName  = "featherpod$suffix-func"
    $ParametersFile   = "parameters$suffix.json"

    Write-Host "`n======================================" -ForegroundColor Magenta
    Write-Host "  Deploying to: $TargetEnvironment" -ForegroundColor Magenta
    Write-Host "  Resource Group: $ResourceGroup" -ForegroundColor Magenta
    Write-Host "  App Service: $AppName" -ForegroundColor Magenta
    Write-Host "  Function App: $FunctionAppName" -ForegroundColor Magenta
    if ($Infrastructure) {
        Write-Host "  Infrastructure: Yes (Bicep)" -ForegroundColor Magenta
    }
    Write-Host "======================================`n" -ForegroundColor Magenta

    if ($Infrastructure) {
        # Deploy infrastructure via Bicep
        Write-Host "Deploying infrastructure via Bicep...`n" -ForegroundColor Cyan

        $secretsFile = Join-Path $PSScriptRoot "infrastructure\parameters.secrets.json"
        if (-not (Test-Path $secretsFile)) {
            throw "Secrets file not found: $secretsFile. Create it with internalApiKey parameter."
        }

        Ensure-VapidKeys -SecretsFile $secretsFile

        az deployment group create `
            --resource-group $ResourceGroup `
            --template-file (Join-Path $PSScriptRoot "infrastructure\main.bicep") `
            --parameters (Join-Path $PSScriptRoot "infrastructure\$ParametersFile") `
            --parameters $secretsFile `
            --output none
        if ($LASTEXITCODE -ne 0) {
            throw "Bicep deployment failed with exit code $LASTEXITCODE"
        }
        Write-Host "`nInfrastructure deployment complete.`n" -ForegroundColor Green
    }
    else {
        # Deploy App Service
        Write-Host "Deploying to Azure App Service...`n" -ForegroundColor Cyan
        az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $script:zipPath --type zip --clean true --async true
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

        # Deploy Function App
        Write-Host "`nDeploying to Azure Function App (Flex Consumption)...`n" -ForegroundColor Cyan
        Push-Location $script:funcPublishPath
        try {
            # Flex Consumption: runtime is configured via functionAppConfig in Bicep, not func CLI
            # --no-build: we publish pre-built binaries, skip remote build
            func azure functionapp publish $FunctionAppName --dotnet-isolated --no-build
            if ($LASTEXITCODE -ne 0) {
                throw "func azure functionapp publish failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Pop-Location
        }

        Write-Host "`nDeployment to $TargetEnvironment successful!`n" -ForegroundColor Green
        Write-Host "App Service URL: https://$AppName.azurewebsites.net" -ForegroundColor Yellow
        Write-Host "Function App URL: https://$FunctionAppName.azurewebsites.net" -ForegroundColor Yellow

        # Move the release-{env} branch only when code shipped. Infrastructure-only
        # deploys (-Infrastructure) leave the release branch where it is so it keeps
        # tracking the last commit whose code is actually running.
        Update-ReleaseBranch -TargetEnvironment $TargetEnvironment -BranchRemote $BranchRemote
    }
}

# Prompt the user to pick from a list of choices using PowerShell's host UI.
function AskUser {
    param (
        [Parameter(Position=0)]            [string]$Caption,
        [Parameter(Position=1, Mandatory)] [string]$Message,
        [Parameter(Position=2, Mandatory)] [string[]]$Choices,
        [Parameter(Position=3)]            [string]$DefaultChoice,
        [Parameter(Position=4)]            [hashtable]$HelpMessages = @{}
    )

    [System.Management.Automation.Host.ChoiceDescription[]]$ChoiceDescriptions = @()

    foreach ($Choice in $Choices) {
        if (-not $HelpMessages.ContainsKey($Choice)) {
            $HelpMessages[$Choice] = $Choice.Trim("&")
        }

        $ChoiceDescriptions += New-Object System.Management.Automation.Host.ChoiceDescription $Choice, $HelpMessages[$Choice]
    }

    $DefaultChoiceIndex = [array]::IndexOf(($ChoiceDescriptions | Select-Object -ExpandProperty Label), "$DefaultChoice")
    $Result = $Host.Ui.PromptForChoice($Caption, $Message, $ChoiceDescriptions, $DefaultChoiceIndex)

    return @($ChoiceDescriptions | Select-Object -ExpandProperty Label)[$Result]
}

# Resolve the remote that should receive release branches.
# First run (multiple remotes): prompts the user and remembers the chosen URL in
# git config (deploy.publicRemoteUrl). Subsequent runs look up the current remote
# name by URL, so renaming the remote locally doesn't break anything.
function Get-ReleaseBranchRemote {
    $remotes = @(git remote)
    if ($remotes.Count -eq 0) {
        return $null
    }

    if ($remotes.Count -eq 1) {
        return $remotes[0]
    }

    $savedUrl = git config --get deploy.publicRemoteUrl 2>$null
    if ($LASTEXITCODE -eq 0 -and $savedUrl) {
        foreach ($remote in $remotes) {
            $url = git remote get-url $remote 2>$null
            if ($LASTEXITCODE -eq 0 -and $url -eq $savedUrl) {
                return $remote
            }
        }

        Write-Host "Saved deploy remote URL '$savedUrl' no longer matches any configured remote; re-prompting." -ForegroundColor Yellow
    }

    # Numeric accelerators (&1, &2, ...) avoid collisions when remote names share a first character.
    $choices = @()
    $helpMessages = @{}
    $labelToRemote = @{}
    for ($i = 0; $i -lt $remotes.Count; $i++) {
        $remote = $remotes[$i]
        $url = git remote get-url $remote 2>$null
        $label = "&$($i + 1) $remote"
        $choices += $label
        $helpMessages[$label] = "$remote ($url)"
        $labelToRemote[$label] = $remote
    }

    try {
        $chosenLabel = AskUser -Caption "Select release branch remote" `
            -Message "Which remote should receive release branches? This will be remembered for future deploys." `
            -Choices $choices `
            -HelpMessages $helpMessages
    }
    catch {
        Write-Host "Warning: could not prompt for release branch remote ($($_.Exception.Message)); skipping branch push." -ForegroundColor Yellow
        return $null
    }

    $chosenRemote = $labelToRemote[$chosenLabel]
    $chosenUrl = git remote get-url $chosenRemote 2>$null
    if ($LASTEXITCODE -eq 0 -and $chosenUrl) {
        git config --local deploy.publicRemoteUrl $chosenUrl | Out-Null
        Write-Host "Remembered '$chosenRemote' ($chosenUrl) as the release branch remote." -ForegroundColor Gray
    }

    return $chosenRemote
}

# Move the release-{env} branch on the remote to HEAD, marking the commit as released.
# Failures are non-fatal: the deploy already succeeded, the branch is just a marker.
# $BranchRemote is resolved up-front in Main via Get-ReleaseBranchRemote.
function Update-ReleaseBranch {
    param(
        [string]$TargetEnvironment,
        [string]$BranchRemote
    )

    $branchName = if ($TargetEnvironment -eq "Test") { "release-test" } else { "release-prod" }

    Write-Host "`nUpdating release branch '$branchName' to HEAD..." -ForegroundColor Cyan

    if (-not $BranchRemote) {
        Write-Host "Warning: no git remote available; skipping branch push." -ForegroundColor Yellow
        return
    }

    git push --force $BranchRemote "HEAD:refs/heads/$branchName"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Warning: failed to push branch '$branchName' to '$BranchRemote' (exit $LASTEXITCODE)" -ForegroundColor Yellow
        return
    }

    Write-Host "Release branch '$branchName' updated on '$BranchRemote'." -ForegroundColor Green
}

# Generate VAPID keys for Web Push if not already present in the secrets file.
# Uses .NET ECDsa P-256 to produce URL-safe base64 keys compatible with the Web Push spec.
function Ensure-VapidKeys {
    param([string]$SecretsFile)

    $secrets = Get-Content $SecretsFile -Raw | ConvertFrom-Json
    $params = $secrets.parameters
    $dirty = $false

    $hasPublic = $params.PSObject.Properties['vapidPublicKey'] -and $params.vapidPublicKey.value
    $hasPrivate = $params.PSObject.Properties['vapidPrivateKey'] -and $params.vapidPrivateKey.value
    $hasSubject = $params.PSObject.Properties['vapidSubject'] -and $params.vapidSubject.value

    if (-not $hasSubject) {
        $email = Read-Host "VAPID subject email (e.g. you@example.com)"
        if (-not $email) {
            throw "VAPID subject is required for Web Push notifications."
        }
        $params | Add-Member -NotePropertyName 'vapidSubject' -NotePropertyValue @{ value = "mailto:$email" } -Force
        $dirty = $true
    }

    if (-not $hasPublic -or -not $hasPrivate) {
        Write-Host "Generating VAPID keys for Web Push notifications..." -ForegroundColor Yellow

        # OID 1.2.840.10045.3.1.7 = NIST P-256. Named curves don't resolve on all Windows builds.
        $ecdsa = [System.Security.Cryptography.ECDsa]::Create(
            [System.Security.Cryptography.ECCurve]::CreateFromValue("1.2.840.10045.3.1.7"))
        try {
            # .NET CNG returns D/X/Y left-padded to key size (32 bytes for P-256).
            # No manual padding needed -- unlike Java's BigInteger, .NET does not strip leading zeros.
            $ecParams = $ecdsa.ExportParameters($true)

            # Public key: uncompressed point (0x04 || X || Y) = 65 bytes
            $publicBytes = [byte[]]::new(65)
            $publicBytes[0] = 0x04
            [Array]::Copy($ecParams.Q.X, 0, $publicBytes, 1, 32)
            [Array]::Copy($ecParams.Q.Y, 0, $publicBytes, 33, 32)

            # URL-safe base64 (no padding)
            $publicKey = [Convert]::ToBase64String($publicBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            $privateKey = [Convert]::ToBase64String($ecParams.D).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        }
        finally {
            $ecdsa.Dispose()
        }

        $params | Add-Member -NotePropertyName 'vapidPublicKey' -NotePropertyValue @{ value = $publicKey } -Force
        $params | Add-Member -NotePropertyName 'vapidPrivateKey' -NotePropertyValue @{ value = $privateKey } -Force
        $dirty = $true
    }

    if ($dirty) {
        $secrets | ConvertTo-Json -Depth 10 | Set-Content $SecretsFile -Encoding utf8NoBOM
        Write-Host "VAPID configuration saved to secrets file." -ForegroundColor Green
    }
}

Main @PSBoundParameters
