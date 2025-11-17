# FeatherPod

A cloud-native .NET podcast feed server for Azure with Blob Storage integration. Host your audio content (like NotebookLM audio overviews) with iTunes-compatible RSS feeds.

## Features

- **Multi-feed support** - Host multiple podcast feeds from a single instance
- **Azure Blob Storage** - Scalable cloud storage for audio files
- **RSS podcast feeds** - iTunes spec compatible with per-feed configuration
- **REST API** - Manage feeds and episodes with API key authentication
- **CLI tool** - Command-line interface for episode and icon management
- **Hash-based episode IDs** - Preserves play progress; re-uploading same file updates metadata
- **Cross-feed operations** - Move or copy episodes between feeds
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

**3. Access feeds:**
```
http://localhost:8080/api/feeds          # List all feeds
http://localhost:8080/{feedId}/feed.xml  # RSS feed
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
https://your-app-name.azurewebsites.net/{feedId}/feed.xml
```

## Development Workflow

Pull requests are automatically deployed to an isolated test environment (`featherpod-test.azurewebsites.net`) where you can validate changes before merging to production. The GitHub Actions workflow:

1. Builds and deploys PR changes to test environment
2. Comments on PR with test URLs
3. Allows testing with real Azure infrastructure

**Setup:** See [.github/DEPLOYMENT.md](.github/DEPLOYMENT.md) for configuring automated deployments and [.github/API-KEY-SETUP.md](.github/API-KEY-SETUP.md) for API key security.

## Usage

### Managing Feeds

```bash
# Create a feed
curl -X POST https://your-app.azurewebsites.net/api/feeds \
  -H "X-API-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{"id":"my-podcast","title":"My Podcast","author":"Your Name",...}'

# List all feeds
curl https://your-app.azurewebsites.net/api/feeds
```

### Adding Episodes

```bash
curl -X POST https://your-app.azurewebsites.net/api/{feedId}/episodes \
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
curl -X DELETE https://your-app.azurewebsites.net/api/{feedId}/episodes/{episode-id} \
  -H "X-API-Key: your-api-key"
```

### Listing Episodes

```bash
curl https://your-app.azurewebsites.net/api/{feedId}/episodes
```

## API Reference

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds` | GET | Public | List all feeds |
| `/api/feeds/{feedId}` | GET | Public | Get feed configuration |
| `/api/feeds` | POST | API Key | Create new feed |
| `/api/feeds/{feedId}` | DELETE | API Key | Delete feed and episodes |
| `/{feedId}/feed.xml` | GET | Public | RSS podcast feed |
| `/{feedId}/icon.png` | GET | Public | Get feed icon |
| `/{feedId}/api/icon` | POST | API Key | Upload/replace feed icon |
| `/{feedId}/api/icon` | DELETE | API Key | Remove feed icon |
| `/{feedId}/audio/{filename}` | GET | Public | Stream audio (range requests) |
| `/{feedId}/api/episodes` | GET | Public | List episodes |
| `/{feedId}/api/episodes` | POST | API Key | Upload episode |
| `/{feedId}/api/episodes/{id}` | DELETE | API Key | Delete episode |
| `/{sourceFeedId}/api/episodes/{id}/move` | POST | API Key | Move episode between feeds |
| `/{sourceFeedId}/api/episodes/{id}/copy` | POST | API Key | Copy episode between feeds |

**Authentication:**
- Protected endpoints require `X-API-Key` header
- Configure via Azure App Service settings or `appsettings.json`
- Read-only endpoints (feeds, audio) are public

## Configuration

**Minimal configuration (appsettings.json):**
```json
{
  "Azure": {
    "AccountName": "your-storage-account",
    "ContainerName": "featherpod"
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

**Podcast icon:** Upload via API (`POST /{feedId}/api/icon`) or CLI (`featherpod-cli icon set icon.png`)

**Additional options:** See configuration files for published date behavior, language, category, and more.

## CLI Tool

FeatherPod includes a command-line tool for managing episodes and icons:

```bash
# Episode management
featherpod-cli episode push *.mp3 -f my-podcast -x
featherpod-cli push episode.mp3 --title "Episode Title"  # Alias

# Icon management
featherpod-cli icon set icon.png -f my-podcast
featherpod-cli icon unset -f my-podcast

# Interactive mode (default)
featherpod-cli
```

Configure API endpoint and key in `appsettings.{Environment}.Local.json` (gitignored).

## Development

```bash
dotnet build          # Build solution
dotnet test           # Run tests (starts integration tests if Azurite is running)
```

## Architecture

- **.NET 9 Minimal API** - Lightweight HTTP endpoints
- **Multi-feed architecture** - Single instance hosts multiple isolated podcast feeds
- **Azure Blob Storage** - Cloud-native file storage with managed identity support
- **Hash-based episode IDs** - `SHA256(feedId:filename:filesize)` ensures stability
- **API Key Authentication** - Secures management endpoints
- **Range request support** - Enables seeking and resuming in podcast apps
- **Cross-feed operations** - Move or copy episodes between feeds via REST API

**Supported formats:** MP3, M4A, AAC, WAV, OGG, FLAC

## License

MIT

## Contributing

Pull requests welcome! The automated test environment will deploy your changes for validation before merge.
