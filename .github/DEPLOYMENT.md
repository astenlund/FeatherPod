# GitHub Actions Deployment Setup

This guide explains how to set up automated PR deployments to Azure using GitHub Actions.

## ⚠️ Security Prerequisites

**Before starting deployment setup, please read [API-KEY-SETUP.md](API-KEY-SETUP.md)** to:
1. Generate secure API keys for test and production environments
2. Set up GitHub Secrets properly
3. Rotate the exposed production API key

This is critical for security - API keys have been removed from the repository.

---

## Overview

The PR deployment workflow automatically:
1. Builds and deploys PRs to a test environment (`featherpod-test`)
2. Comments on the PR with test environment URLs
3. Allows testing bug fixes before merging to production

## Architecture

- **Production**: `featherpod` App Service with `audio` and `metadata` containers
- **Test**: `featherpod-test` App Service with `audio-test` and `metadata-test` containers
- **Shared**: Both environments use the same storage account (`featherpod`)
- **Cost**: Both use F1 (Free) tier, no additional costs

## One-Time Setup

### Step 1: Deploy Test Environment Infrastructure

The test environment needs to be created once before the workflow can deploy to it.

```bash
# Login to Azure
az login

# Create test environment resource group (or use existing)
az group create --name featherpod-test-rg --location swedencentral

# Deploy test infrastructure
az deployment group create \
  --resource-group featherpod-test-rg \
  --template-file infrastructure/test-environment.bicep \
  --parameters infrastructure/test-environment.parameters.json
```

**Important**: If you use a different storage account or resource group, update the parameters in `test-environment.parameters.json`.

### Step 2: Create Azure Service Principal for GitHub Actions

GitHub Actions needs credentials to deploy to Azure. Create a service principal with contributor access:

```bash
# Create service principal and get credentials
az ad sp create-for-rbac \
  --name "featherpod-github-actions" \
  --role contributor \
  --scopes /subscriptions/{subscription-id}/resourceGroups/featherpod-test-rg \
  --sdk-auth
```

**Replace** `{subscription-id}` with your Azure subscription ID. Get it with:
```bash
az account show --query id -o tsv
```

This command outputs JSON like:
```json
{
  "clientId": "...",
  "clientSecret": "...",
  "subscriptionId": "...",
  "tenantId": "...",
  "activeDirectoryEndpointUrl": "...",
  "resourceManagerEndpointUrl": "...",
  "activeDirectoryGraphResourceId": "...",
  "sqlManagementEndpointUrl": "...",
  "galleryEndpointUrl": "...",
  "managementEndpointUrl": "..."
}
```

**Save this entire JSON output** - you'll need it in the next step.

### Step 3: Configure GitHub Secrets

You need to add TWO secrets to your GitHub repository:

#### Secret 1: AZURE_CREDENTIALS

1. Go to your GitHub repository
2. Navigate to **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**
4. Name: `AZURE_CREDENTIALS`
5. Value: Paste the entire JSON output from Step 2
6. Click **Add secret**

#### Secret 2: TEST_API_KEY

**See [API-KEY-SETUP.md](API-KEY-SETUP.md) for detailed instructions on generating and setting the test API key.**

Quick version:
1. Generate a secure API key: `openssl rand -base64 32`
2. Go to **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**
4. Name: `TEST_API_KEY`
5. Value: Paste the generated API key
6. Click **Add secret**

### Step 4: Verify Setup

Push a test PR to verify the workflow:

```bash
# Create a test branch
git checkout -b test-deployment

# Make a small change (e.g., add a comment)
echo "// Test deployment" >> FeatherPod/Program.cs

# Commit and push
git add .
git commit -m "Test: Verify GitHub Actions deployment"
git push -u origin test-deployment

# Create PR via gh CLI or GitHub UI
gh pr create --title "Test: Verify deployment workflow" --body "Testing automated PR deployments"
```

The workflow should:
1. Trigger automatically when the PR is opened
2. Build and deploy to `featherpod-test`
3. Comment on the PR with test URLs

## Workflow Details

### Trigger Events

The workflow runs on:
- `opened` - When a new PR is created
- `synchronize` - When commits are pushed to an existing PR
- `reopened` - When a closed PR is reopened
- `closed` - When a PR is closed or merged

### Deployment Process

1. **Checkout code** - Gets the PR branch code
2. **Setup .NET** - Installs .NET 9 SDK
3. **Build and publish** - Compiles release build
4. **Create deployment package** - Zips the output
5. **Azure Login** - Authenticates using `AZURE_CREDENTIALS` secret
6. **Deploy** - Uploads to test App Service
7. **Comment** - Posts deployment info to PR

### Test Environment Details

- **URL**: https://featherpod-test.azurewebsites.net
- **Resource Group**: `featherpod-test-rg`
- **App Service**: `featherpod-test`
- **Storage Containers**: `audio-test`, `metadata-test`
- **Tier**: F1 (Free)

## Testing a PR

When a PR is deployed, the bot comments with test endpoints:

```
📡 RSS Feed: https://featherpod-test.azurewebsites.net/feed.xml
📋 Episodes API: https://featherpod-test.azurewebsites.net/api/episodes
🎵 Upload: POST https://featherpod-test.azurewebsites.net/api/episodes
```

### Upload Test Files

Use the test environment to verify bug fixes:

```bash
# Upload an episode to test environment
curl -X POST https://featherpod-test.azurewebsites.net/api/episodes \
  -H "X-API-Key: test-api-key-change-me-in-production" \
  -F "file=@your-test-file.mp3" \
  -F "title=Test Episode"

# Check episodes list
curl https://featherpod-test.azurewebsites.net/api/episodes

# Check RSS feed
curl https://featherpod-test.azurewebsites.net/feed.xml
```

### Testing 502 Gateway Error Fix

For the specific 502 gateway error bug:

1. Upload files that previously caused 502 errors to the test environment
2. Verify they upload successfully without errors
3. Check the RSS feed includes the new episodes
4. Try downloading the audio files via `/audio/{filename}` endpoint
5. If all works, approve and merge the PR

## Cleanup

The test environment persists after PRs are closed to allow manual testing. To clean up:

### Restart App (keeps data)
```bash
az webapp restart --name featherpod-test --resource-group featherpod-test-rg
```

### Clear test data
```bash
# Delete all test episodes via API
curl -X DELETE https://featherpod-test.azurewebsites.net/api/episodes/{episode-id} \
  -H "X-API-Key: test-api-key-change-me-in-production"
```

### Full cleanup (removes infrastructure)
```bash
# WARNING: This deletes the test environment completely
az group delete --name featherpod-test-rg --yes --no-wait
```

After full cleanup, you'll need to re-run Step 1 to redeploy the test infrastructure.

## Troubleshooting

### Workflow fails with "Resource not found"

The test environment hasn't been deployed. Run Step 1 to create infrastructure.

### Workflow fails with "Authentication failed"

Check that:
1. `AZURE_CREDENTIALS` secret is correctly set in GitHub
2. Service principal has contributor role on `featherpod-test-rg`
3. Service principal hasn't expired (they expire after 1 year by default)

### Deployment succeeds but app returns 502

Check App Service logs:
```bash
az webapp log tail --name featherpod-test --resource-group featherpod-test-rg
```

Common issues:
- PORT environment variable not set (should be auto-configured)
- .NET runtime version mismatch
- Storage account permissions not configured

### PR comment not posted

Check that GitHub Actions has write permissions:
1. Go to **Settings** → **Actions** → **General**
2. Under "Workflow permissions", select "Read and write permissions"
3. Click **Save**

## Cost Considerations

- **F1 Tier**: Free, but has 60 CPU minutes/day quota
- **Storage**: Shared with production, minimal additional cost
- **Data Transfer**: Minimal for testing purposes

If you hit quota limits during heavy testing, temporarily upgrade to B1:
```bash
az appservice plan update --name featherpod-test-plan --resource-group featherpod-test-rg --sku B1
```

Remember to downgrade back to F1 after testing:
```bash
az appservice plan update --name featherpod-test-plan --resource-group featherpod-test-rg --sku F1
```

## Security Notes

1. **Separate API Keys**: Test environment uses different API key than production
2. **Isolated Data**: Separate blob containers prevent test data mixing with production
3. **Service Principal**: Limited to test resource group only
4. **GitHub Secrets**: Azure credentials are encrypted and never exposed in logs

## Production Deployment

This workflow only deploys to the test environment. Production deployments are manual:

```bash
# Build and deploy to production
dotnet publish FeatherPod/FeatherPod.csproj -c Release -o publish
cd publish
zip -r ../deploy.zip .
cd ..
az webapp deploy --resource-group featherpod-rg --name featherpod --src-path deploy.zip --type zip
```

Consider creating a separate production deployment workflow that triggers on merges to `main`.
