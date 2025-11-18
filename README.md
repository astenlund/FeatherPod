# FeatherPod

A cloud-native .NET podcast feed server for Azure with Blob Storage integration. Host your audio content (like NotebookLM audio overviews) with iTunes-compatible RSS feeds.

## Features

- **Multi-feed support** - Host multiple podcast feeds from a single instance
- **Role-based access control** - Admin and FeedOwner roles with per-user API keys
- **User management** - Create users, manage permissions, and assign feed ownership via API and CLI
- **Audio normalization** - Automatic loudness normalization (-16 LUFS) via FFmpeg
- **Azure Blob Storage** - Scalable cloud storage for audio files
- **RSS podcast feeds** - iTunes spec compatible with per-feed configuration
- **REST API** - Comprehensive API with `/api` prefix for consistency
- **Version tracking** - Git SHA embedded in binaries and available via `/api/version`
- **CLI tool** - Command-line interface for episode, icon, and user management
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
dotnet run --project FeatherPod.Server
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
curl -X POST https://your-app.azurewebsites.net/api/feeds/{feedId}/episodes \
  -H "X-API-Key: your-api-key" \
  -F "file=@audio.mp3" \
  -F "title=Episode Title" \
  -F "description=Full episode description for RSS" \
  -F "summary=Short summary for iTunes (optional)"
```

**Optional parameters:**
- `description` - Full description for RSS feed
- `summary` - Short summary for iTunes (defaults to description if not provided)
- `publishedDate` - Set explicit date (ISO 8601 format)

### Removing Episodes

```bash
curl -X DELETE https://your-app.azurewebsites.net/api/feeds/{feedId}/episodes/{episode-id} \
  -H "X-API-Key: your-api-key"
```

### Listing Episodes

```bash
curl https://your-app.azurewebsites.net/api/feeds/{feedId}/episodes \
  -H "X-API-Key: your-api-key"
```

### Managing Users (Admin only)

```bash
# Create user
curl -X POST https://your-app.azurewebsites.net/api/users \
  -H "X-API-Key: admin-api-key" \
  -H "Content-Type: application/json" \
  -d '{"id":"user123","name":"John Doe","email":"john@example.com","role":"FeedOwner","ownedFeeds":["my-podcast"]}'

# List users
curl https://your-app.azurewebsites.net/api/users \
  -H "X-API-Key: admin-api-key"

# Grant feed ownership
curl -X POST https://your-app.azurewebsites.net/api/users/{userId}/feeds \
  -H "X-API-Key: admin-api-key" \
  -H "Content-Type: application/json" \
  -d '{"feedId":"my-podcast"}'
```

## API Reference

### Feed Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/version` | GET | Public | Version info (with git SHA) |
| `/api/feeds` | GET | Public | List all feeds |
| `/api/feeds/{feedId}` | GET | Public | Get feed configuration |
| `/api/feeds` | POST | Admin | Create new feed |
| `/api/feeds/{feedId}` | PUT | Admin/Owner | Update feed metadata |
| `/api/feeds/{feedId}/rename?newId=...` | POST | Admin | Rename feed ID |
| `/api/feeds/{feedId}` | DELETE | Admin | Delete feed and all episodes |

### Episode Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds/{feedId}/episodes` | GET | Admin/Owner | List episodes for feed |
| `/api/feeds/{feedId}/episodes` | POST | Admin/Owner | Upload episode |
| `/api/feeds/{feedId}/episodes/{id}` | DELETE | Admin/Owner | Delete episode |
| `/api/feeds/{feedId}/episodes/{id}/move` | POST | Admin/Owner | Move episode between feeds |
| `/api/feeds/{feedId}/episodes/{id}/copy` | POST | Admin/Owner | Copy episode between feeds |

### Icon Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds/{feedId}/icon` | POST | Admin/Owner | Upload/replace feed icon |
| `/api/feeds/{feedId}/icon` | DELETE | Admin/Owner | Remove feed icon |

### User Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/users` | GET | Admin | List all users |
| `/api/users/{userId}` | GET | Admin | Get user by ID |
| `/api/users` | POST | Admin | Create user (returns API key once) |
| `/api/users/{userId}` | DELETE | Admin | Delete user (soft delete) |
| `/api/users/{userId}/key/regenerate` | POST | Admin/Self | Regenerate user API key |
| `/api/users/{userId}/feeds` | POST | Admin | Grant feed ownership |
| `/api/users/{userId}/feeds/{feedId}` | DELETE | Admin | Revoke feed ownership |

### Public Endpoints

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/{feedId}/feed.xml` | GET | Public | RSS podcast feed |
| `/{feedId}/icon.png` | GET | Public | Feed icon |
| `/{feedId}/audio/{filename}` | GET | Public | Stream audio (range requests) |

**Authentication & Authorization:**
- Protected endpoints require `X-API-Key` header
- **Admin** role has full access to all feeds and user management
- **FeedOwner** role has access only to owned feeds
- Legacy API key automatically migrated to admin user on first use
- Each user has their own API key (32-byte random, SHA256 hashed)

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

**Podcast icon:** Upload via API (`POST /api/feeds/{feedId}/icon`) or CLI (`featherpod-cli icon set icon.png`)

**Additional options:** See configuration files for published date behavior, language, category, and more.

## CLI Tool

FeatherPod includes a command-line tool for managing episodes, icons, and users:

```bash
# Episode management
featherpod-cli episode push *.mp3 -f my-podcast -x  # -x extracts date from file before normalization
featherpod-cli push episode.mp3 --title "Episode Title" --description "Full description" --summary "Short summary"  # Alias

# Icon management
featherpod-cli icon set icon.png -f my-podcast
featherpod-cli icon unset -f my-podcast

# User management (Admin only)
featherpod-cli user create
featherpod-cli user list
featherpod-cli user delete
featherpod-cli user regenerate-key
featherpod-cli user grant-feed
featherpod-cli user revoke-feed

# Environment selection
featherpod-cli -e Dev user list  # Target dev environment
featherpod-cli -e Test user list # Target test environment
# (Defaults to Prod if not specified)

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
- **Role-based access control** - Admin and FeedOwner roles with per-user API keys and feed ownership
- **Azure Blob Storage** - Cloud-native file storage with managed identity support
- **Hash-based episode IDs** - `SHA256(feedId:filename:filesize)` ensures stability
- **User management** - User accounts stored in `users.json` with SHA256 hashed API keys
- **Range request support** - Enables seeking and resuming in podcast apps
- **Cross-feed operations** - Move or copy episodes between feeds via REST API

**Supported formats:** MP3, M4A, AAC, WAV, OGG, FLAC

## License

MIT

## Contributing

Pull requests welcome! The automated test environment will deploy your changes for validation before merge.
