# FeatherPod

A cloud-native .NET podcast feed server for Azure with Blob Storage integration. Host your audio content (like NotebookLM audio overviews) with iTunes-compatible RSS feeds.

## Features

- **Azure Blob Storage** - Scalable cloud storage for audio files
- **RSS podcast feed** - iTunes spec compatible
- **REST API** - Manage episodes with API key authentication
- **Hash-based episode IDs** - Preserves play progress when re-adding files
- **Managed Identity** - Secure Azure authentication without secrets
- **Automated PR testing** - GitHub Actions deploys PRs to isolated test environment
- **CI/CD pipeline** - Test-before-merge workflow with automated deployments

## Prerequisites

- .NET 9 SDK
- Azure Storage Account (or Azurite for local development)
- Azure App Service (optional for deployment)

## Quick Start

### Local Development

**1. Install and start Azurite:**
```bash
npm install -g azurite
azurite --silent --location $env:USERPROFILE\.azurite
```

**2. Run FeatherPod:**
```bash
dotnet run --project FeatherPod
```

**3. Access the feed:**
```
http://localhost:5070/feed.xml
```

The development configuration is already set up to use Azurite.

### Azure Deployment

**Deploy infrastructure with Bicep:**
```bash
az login
az group create --name featherpod-rg --location swedencentral

az deployment group create \
  --resource-group featherpod-rg \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/parameters.json
```

This creates: Storage Account, blob containers, App Service, Managed Identity, and RBAC.

**Deploy application:**
```powershell
# Deploy to production
.\Deploy-FeatherPod.ps1 -Environment Prod

# Deploy to test environment
.\Deploy-FeatherPod.ps1 -Environment Test
```

**Subscribe in your podcast app:**
```
https://your-app-name.azurewebsites.net/feed.xml
```

## Development Workflow

Pull requests are automatically deployed to an isolated test environment (`featherpod-test.azurewebsites.net`) where you can validate changes before merging to production. The GitHub Actions workflow:

1. Builds and deploys PR changes to test environment
2. Comments on PR with test URLs
3. Allows testing with real Azure infrastructure

**Setup:** See [.github/DEPLOYMENT.md](.github/DEPLOYMENT.md) for configuring automated deployments and [.github/API-KEY-SETUP.md](.github/API-KEY-SETUP.md) for API key security.

## Usage

### Adding Episodes

```bash
curl -X POST https://your-app.azurewebsites.net/api/episodes \
  -H "X-API-Key: your-api-key" \
  -F "file=@audio.mp3" \
  -F "title=Episode Title" \
  -F "description=Episode description"
```

**Optional parameters:**
- `publishedDate` - Set explicit date (ISO 8601 format)
- `useMetadataForPublishedDate=true` - Extract date from file metadata

### Removing Episodes

```bash
curl -X DELETE https://your-app.azurewebsites.net/api/episodes/{episode-id} \
  -H "X-API-Key: your-api-key"
```

### Listing Episodes

```bash
curl https://your-app.azurewebsites.net/api/episodes
```

## API Reference

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/feed.xml` | GET | Public | RSS podcast feed |
| `/audio/{filename}` | GET | Public | Stream audio file (supports range requests) |
| `/api/episodes` | GET | Public | List all episodes (JSON) |
| `/api/episodes` | POST | API Key | Upload new episode |
| `/api/episodes/{id}` | DELETE | API Key | Delete episode |

**Authentication:**
- Protected endpoints require `X-API-Key` header
- Configure via Azure App Service settings or `appsettings.json`
- Read-only endpoints (feed, audio) are public

## Configuration

**Minimal configuration (appsettings.json):**
```json
{
  "Azure": {
    "AccountName": "your-storage-account",
    "AudioContainerName": "audio",
    "MetadataContainerName": "metadata"
  },
  "Podcast": {
    "Title": "My Podcast",
    "Author": "Your Name",
    "Email": "your@email.com",
    "BaseUrl": "https://your-app.azurewebsites.net",
    "ImageUrl": "https://your-app.azurewebsites.net/icon.png"
  }
}
```

**Podcast icon:** Place a 1024x1024 PNG at `FeatherPod/wwwroot/icon.png`

**Additional options:** See configuration files for published date behavior, language, category, and more.

## Development

```bash
dotnet build          # Build solution
dotnet test           # Run tests (starts integration tests if Azurite is running)
```

## Architecture

- **.NET 9 Minimal API** - Lightweight HTTP endpoints
- **Azure Blob Storage** - Cloud-native file storage with managed identity support
- **Hash-based episode IDs** - `SHA256(filename:filesize)` ensures stability
- **API Key Authentication** - Secures management endpoints
- **Range request support** - Enables seeking and resuming in podcast apps

**Supported formats:** MP3, M4A, AAC, WAV, OGG, FLAC

## License

MIT

## Contributing

Pull requests welcome! The automated test environment will deploy your changes for validation before merge.
