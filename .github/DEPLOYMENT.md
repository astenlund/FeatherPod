# GitHub Actions Deployment Setup

This guide explains how to set up automated PR deployments to Azure using GitHub Actions.

## Overview

The PR deployment workflow automatically:
1. Builds and deploys PRs to a test environment (`featherpod-test` App Service + `featherpod-test-func` Function App)
2. Comments on the PR with deployment status and test environment URLs
3. Allows testing changes before merging to production

## Architecture

- **Production**: `featherpod` App Service + `featherpod-func` Function App
- **Test**: `featherpod-test` App Service + `featherpod-test-func` Function App
- **Storage**: Separate accounts — production uses `featherpod`, test uses `featherpodtest`
- **Container Structure**: Single container with hierarchical paths (`feeds.json`, `{feedId}/episodes.json`, `{feedId}/{filename}`)
- **Cost**: App Service uses F1 (Free) tier; Function App uses FC1 (Flex Consumption, pay-per-use)

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
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/parameters-test.json
```

**Important**: If you use a different storage account or resource group, update the parameters in `parameters-test.json`.

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

#### Secret 2: TEST_API_KEY (optional)

The test environment uses user-based API keys (format: `fp_{userId}_{secret}`). This secret is not used by the PR deploy workflow itself, but can be referenced by other workflows or scripts that need to configure the test environment's app settings.

1. Go to **Settings** → **Secrets and variables** → **Actions**
2. Click **New repository secret**
3. Name: `TEST_API_KEY`
4. Value: Paste the admin user's API key
5. Click **Add secret**

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
2. **Setup .NET** - Installs .NET 10 SDK
3. **Publish** - Compiles release builds for Server and Functions
4. **Upload artifacts** - Uploads server zip and functions output
5. **Deploy App Service** - Azure login + deploy to `featherpod-test` (parallel)
6. **Deploy Function App** - Azure login + deploy to `featherpod-test-func` (parallel)
7. **Notify** - Posts deployment status to PR (updates existing comment)

### Test Environment Details

- **URL**: https://featherpod-test.azurewebsites.net
- **Resource Group**: `featherpod-test-rg`
- **App Service**: `featherpod-test`
- **Function App**: `featherpod-test-func` (Flex Consumption)
- **Storage**: `featherpodtest` account, `featherpod` container (hierarchical structure)
- **Tier**: F1 (Free) for App Service, FC1 for Function App

## Testing a PR

When a PR is deployed, the bot comments with deployment status and a link to the push page:

```
Push Page: https://featherpod-test.azurewebsites.net/test-feed/push
```

### Upload Test Files

Use the test environment to verify bug fixes:

```bash
# Upload an episode to test environment (replace {feedId} with your feed ID)
curl -X POST https://featherpod-test.azurewebsites.net/api/{feedId}/episodes \
  -H "X-API-Key: fp_{userId}_{secret}" \
  -F "file=@your-test-file.mp3" \
  -F "title=Test Episode"

# Check episodes list
curl https://featherpod-test.azurewebsites.net/api/{feedId}/episodes

# Check RSS feed
curl https://featherpod-test.azurewebsites.net/{feedId}/feed.xml
```

## Cleanup

The test environment persists after PRs are closed to allow manual testing. To clean up:

### Restart App (keeps data)
```bash
az webapp restart --name featherpod-test --resource-group featherpod-test-rg
```

### Clear test data
```bash
# Delete all test episodes via API
curl -X DELETE https://featherpod-test.azurewebsites.net/api/{feedId}/episodes/{episode-id} \
  -H "X-API-Key: fp_{userId}_{secret}"
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
- **Storage**: Separate `featherpodtest` account, minimal additional cost
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
2. **Isolated Data**: Separate storage accounts (`featherpod` vs `featherpodtest`) prevent test data mixing with production
3. **Service Principal**: Limited to test resource group only
4. **GitHub Secrets**: Azure credentials are encrypted and never exposed in logs

## Production Deployment

This workflow only deploys to the test environment. Production deployments are manual:

```bash
# Build and deploy to production
dotnet publish FeatherPod.Server/FeatherPod.Server.csproj -c Release -o publish
cd publish
zip -r ../deploy.zip .
cd ..
az webapp deploy --resource-group featherpod-rg --name featherpod --src-path deploy.zip --type zip
```

Consider creating a separate production deployment workflow that triggers on merges to `main`.
