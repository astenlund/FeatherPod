# API Key Security Setup

This guide explains how to securely generate and configure API keys for both production and test environments.

## ⚠️ Important Security Note

**API keys should NEVER be committed to git.** They have been removed from the parameters files and must be set securely.

## Generating Secure API Keys

Use one of these methods to generate strong, random API keys:

### Option 1: Using OpenSSL (Recommended)
```bash
# Generate a 32-byte random key, base64 encoded
openssl rand -base64 32
```

### Option 2: Using PowerShell
```powershell
# Generate a random key
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
```

### Option 3: Using Python
```python
import secrets
import base64
print(base64.b64encode(secrets.token_bytes(32)).decode())
```

### Option 4: Online Generator
Visit https://www.random.org/strings/ and generate a random string (set length to 32+ characters, use alphanumeric + special characters)

**Save these keys securely** - you'll need them for the setup steps below.

---

## Production Environment Setup

### Step 1: Generate New Production API Key

Since the old production key was in git (and potentially exposed), generate a new one:

```bash
# Generate new key
PROD_API_KEY=$(openssl rand -base64 32)
echo "New production API key: $PROD_API_KEY"
```

**IMPORTANT:** Save this key somewhere secure (password manager, etc.)

### Step 2: Update Production App Service

Set the new API key in your production Azure App Service:

```bash
# Set API key in production
az webapp config appsettings set \
  --name featherpod \
  --resource-group featherpod-rg \
  --settings ApiKey="YOUR_NEW_PRODUCTION_KEY_HERE"
```

Or via Azure Portal:
1. Go to your App Service (`featherpod`)
2. Navigate to **Settings** → **Configuration**
3. Click **+ New application setting**
4. Name: `ApiKey`
5. Value: Your new production key
6. Click **OK** then **Save**

### Step 3: Update Your Upload Scripts/Tools

If you have any scripts or tools that upload episodes to production, update them with the new API key:

```bash
# Example upload command with new key
curl -X POST https://featherpod.azurewebsites.net/api/episodes \
  -H "X-API-Key: YOUR_NEW_PRODUCTION_KEY_HERE" \
  -F "file=@episode.mp3" \
  -F "title=My Episode"
```

### Step 4: Deploy Infrastructure Updates

The updated Bicep templates make API key optional. Redeploy to ensure everything is in sync:

```bash
# This will NOT overwrite your manually set API key
az deployment group create \
  --resource-group featherpod-rg \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/parameters.json
```

---

## Test Environment Setup

The test environment uses GitHub Secrets to manage the API key securely.

### Step 1: Generate Test API Key

```bash
# Generate test environment key
TEST_API_KEY=$(openssl rand -base64 32)
echo "Test API key: $TEST_API_KEY"
```

### Step 2: Add GitHub Secrets

You need to add TWO secrets to GitHub (the second one is for Azure authentication):

#### Secret 1: TEST_API_KEY

1. Go to your GitHub repository
2. Navigate to **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**
4. Name: `TEST_API_KEY`
5. Value: The test API key you generated in Step 1
6. Click **Add secret**

#### Secret 2: AZURE_CREDENTIALS (if not already added)

Create an Azure Service Principal:

```bash
# Get your subscription ID
SUBSCRIPTION_ID=$(az account show --query id -o tsv)

# Create service principal for GitHub Actions
az ad sp create-for-rbac \
  --name "featherpod-github-actions" \
  --role contributor \
  --scopes /subscriptions/$SUBSCRIPTION_ID/resourceGroups/featherpod-test-rg \
  --sdk-auth
```

This outputs JSON credentials. Add them as a GitHub secret:

1. Copy the entire JSON output
2. Go to **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**
4. Name: `AZURE_CREDENTIALS`
5. Value: Paste the entire JSON
6. Click **Add secret**

### Step 3: Deploy Test Infrastructure

Deploy the test environment (API key will be set by GitHub Actions on each PR):

```bash
# Create resource group if it doesn't exist
az group create --name featherpod-test-rg --location swedencentral

# Deploy test infrastructure (without API key - it's set by workflow)
az deployment group create \
  --resource-group featherpod-test-rg \
  --template-file infrastructure/test-environment.bicep \
  --parameters infrastructure/test-environment.parameters.json
```

### Step 4: Test the Workflow

The GitHub Actions workflow will automatically:
1. Deploy PR code to test environment
2. Set the API key from `TEST_API_KEY` secret
3. Comment on PR with test URLs

Test it by creating a PR:

```bash
git checkout -b test-api-key-setup
git add .
git commit -m "Security: Remove API keys from parameters files"
git push -u origin test-api-key-setup
gh pr create --title "Security: Remove API keys from parameters" --body "Removes hardcoded API keys and sets up secure key management"
```

---

## How It Works

### Production
- API key is stored in Azure App Service settings only
- Set once manually, persists across deployments
- Not in git, not in CI/CD

### Test Environment
- API key stored in GitHub Secrets
- GitHub Actions sets it on every PR deployment
- Isolated from production key
- Can be rotated without touching production

---

## API Key Rotation

### When to Rotate

Rotate API keys if:
- They were exposed in git history (✅ production key was exposed)
- Suspected compromise
- Regular security rotation (recommended: every 90 days)
- Team member with access leaves

### Production Rotation

```bash
# 1. Generate new key
NEW_KEY=$(openssl rand -base64 32)

# 2. Update Azure
az webapp config appsettings set \
  --name featherpod \
  --resource-group featherpod-rg \
  --settings ApiKey="$NEW_KEY"

# 3. Update your upload scripts/tools with new key
```

### Test Environment Rotation

```bash
# 1. Generate new key
NEW_TEST_KEY=$(openssl rand -base64 32)

# 2. Update GitHub Secret
# Go to Settings → Secrets → Actions
# Click on TEST_API_KEY → Update
# Paste new key → Update secret

# 3. Next PR deployment will use new key automatically
```

---

## Verification

### Verify Production API Key

```bash
# This should succeed with correct key
curl -X POST https://featherpod.azurewebsites.net/api/episodes \
  -H "X-API-Key: YOUR_PRODUCTION_KEY" \
  -F "file=@test.mp3" \
  -F "title=Test"

# This should fail (401 Unauthorized) with wrong key
curl -X POST https://featherpod.azurewebsites.net/api/episodes \
  -H "X-API-Key: wrong-key" \
  -F "file=@test.mp3" \
  -F "title=Test"
```

### Verify Test Environment

The GitHub Actions workflow will fail if `TEST_API_KEY` secret is not set correctly.

You can also verify manually:

```bash
# Get the current test API key from GitHub Secrets (you need to know it)
curl -X POST https://featherpod-test.azurewebsites.net/api/episodes \
  -H "X-API-Key: YOUR_TEST_KEY" \
  -F "file=@test.mp3" \
  -F "title=Test"
```

---

## Security Best Practices

1. ✅ **Never commit API keys to git** - Use secrets management
2. ✅ **Use different keys for different environments** - Test vs. Production
3. ✅ **Use strong, random keys** - At least 32 bytes of entropy
4. ✅ **Rotate keys regularly** - Every 90 days or on suspected compromise
5. ✅ **Store keys securely** - Password manager, Azure Key Vault, GitHub Secrets
6. ✅ **Limit key scope** - Separate keys for different apps/environments
7. ✅ **Monitor for unauthorized use** - Check App Service logs for 401 errors

---

## Troubleshooting

### "Unauthorized" errors after key rotation

**Problem:** Getting 401 errors with the new key

**Solutions:**
- Verify you copied the entire key (no trailing spaces)
- Check the App Service setting was saved (go to Portal → Configuration)
- Restart the App Service: `az webapp restart --name featherpod --resource-group featherpod-rg`

### GitHub Actions workflow fails with 401

**Problem:** PR deployments fail when uploading test data

**Solutions:**
- Verify `TEST_API_KEY` secret is set in GitHub
- Check the secret value matches what's configured in Azure
- Re-run the workflow after fixing the secret

### API key not taking effect

**Problem:** Changed API key but old key still works

**Solutions:**
- Restart the App Service to pick up new configuration
- Clear any CDN/proxy caches if you're using one
- Check you updated the correct environment (prod vs. test)

---

## Old Production Key Was Exposed - What Now?

✅ **Action taken:** Generated and set new production key (see Step 1 above)

The old production key (`s1u1yJ67sIKQ/Gcn/v/HBuumzAkK0c3c1upt7RC/d8w=`) was in git history and is now invalid.

**Additional recommendations:**
1. ✅ Remove old key from git history (optional, but recommended)
2. ✅ Monitor production logs for unauthorized access attempts
3. ✅ Consider enabling Azure App Service authentication for additional security
4. ✅ Rotate production key again in 90 days

**To scrub git history (optional, advanced):**
```bash
# WARNING: This rewrites git history - coordinate with team
git filter-branch --force --index-filter \
  "git rm --cached --ignore-unmatch infrastructure/parameters.json" \
  --prune-empty --tag-name-filter cat -- --all

# Force push (dangerous - backup first!)
git push origin --force --all
```

**Note:** Since this is likely a personal project, simply rotating the key is sufficient. Git history scrubbing is only critical for team projects with wide access.
