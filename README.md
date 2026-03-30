# FeatherPod

A cloud-native .NET podcast feed server for Azure with Blob Storage integration. Host your audio content (like NotebookLM audio overviews) with iTunes-compatible RSS feeds.

## Features

- **Multi-feed support** - Host multiple podcast feeds from a single instance
- **Role-based access control** - Admin and FeedOwner roles with per-user API keys
- **User management** - Create users, manage permissions, and assign feed ownership via API and CLI
- **Audio normalization** - Loudness normalization (-16 LUFS) via FFmpeg, locally or server-side (async via Azure Functions)
- **AI-assisted episode titles** - Azure OpenAI auto-generates titles on upload; on-demand suggestions in CLI and push page
- **Azure Blob Storage** - Scalable cloud storage for audio files with Managed Identity
- **RSS podcast feeds** - iTunes spec compatible with per-feed configuration
- **CLI tool** - Command-line interface for episode, icon, feed, and user management
- **REST API** - REST API for management (consumed by CLI tool)
- **Browser push page** - PWA at `/{feedId}/push#API_KEY` with multi-file upload, server-side normalization, real-time progress, upload history, episode context menus (rename, delete), cross-device sync via SSE, push notifications via Web Push API, and screen wake lock
- **YouTube import** - Paste or share a YouTube link on the push page to import audio or video episodes via yt-dlp, with cookie-based authentication for bot detection bypass
- **Android Share Target** - Install the push page as a PWA to share audio files directly from Android
- **Windows context menu** - Right-click audio files in Explorer to push to a feed
- **Real-time progress** - SSE and SignalR-based progress streaming for normalization jobs
- **Episode rename** - Rename episodes via CLI (with `--suggest` for AI titles) or push page context menu
- **Delete after upload** - `--delete-after` flag with cross-platform trash support
- **Hash-based episode IDs** - Preserves play progress; re-uploading same file updates metadata
- **Cross-feed operations** - Move or copy episodes between feeds
- **Version tracking** - Git SHA embedded in binaries and available via `/api/version`
- **Automated PR testing** - GitHub Actions deploys PRs to isolated test environment
- **CI/CD pipeline** - Test-before-merge workflow with automated deployments

## Prerequisites

- .NET 10 SDK
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
az group create --name <your-resource-group> --location swedencentral

az deployment group create \
  --resource-group <your-resource-group> \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/parameters.json
```

> **Note:** You'll need to choose unique names for your resources (resource group, storage account, app service) in `parameters.json`. Azure resource names must be globally unique.

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
https://<your-app-name>.azurewebsites.net/{feedId}/feed.xml
```

## Development Workflow

PRs auto-deploy to test environment via GitHub Actions. See [.github/DEPLOYMENT.md](.github/DEPLOYMENT.md) for setup.

## Usage

### Managing Feeds

```bash
# Create a feed
curl -X POST https://<your-app>.azurewebsites.net/api/feeds \
  -H "X-API-Key: <your-api-key>" \
  -H "Content-Type: application/json" \
  -d '{"id":"my-podcast","title":"My Podcast","author":"Your Name",...}'

# List all feeds
curl https://<your-app>.azurewebsites.net/api/feeds
```

### Adding Episodes

```bash
curl -X POST https://<your-app>.azurewebsites.net/api/feeds/{feedId}/episodes \
  -H "X-API-Key: <your-api-key>" \
  -F "file=@audio.mp3" \
  -F "title=Episode Title" \
  -F "description=Full episode description for RSS" \
  -F "summary=Short summary for iTunes (optional)"
```

**Optional parameters:**
- `description` - Full description for RSS feed
- `summary` - Short summary for iTunes (defaults to description if not provided)
- `publishedDate` - Set explicit date (ISO 8601 format)
- `normalize=true` (query param) - Async server-side audio normalization to -16 LUFS (returns 202 with job ID)

### Removing Episodes

```bash
curl -X DELETE https://<your-app>.azurewebsites.net/api/feeds/{feedId}/episodes/{episode-id} \
  -H "X-API-Key: <your-api-key>"
```

### Listing Episodes

```bash
curl https://<your-app>.azurewebsites.net/api/feeds/{feedId}/episodes \
  -H "X-API-Key: <your-api-key>"
```

### Managing Users (Admin only)

```bash
# Create user
curl -X POST https://<your-app>.azurewebsites.net/api/users \
  -H "X-API-Key: <admin-api-key>" \
  -H "Content-Type: application/json" \
  -d '{"id":"user123","name":"John Doe","email":"john@example.com","role":"FeedOwner","ownedFeeds":["my-podcast"]}'

# List users
curl https://<your-app>.azurewebsites.net/api/users \
  -H "X-API-Key: <admin-api-key>"

# Grant feed ownership
curl -X POST https://<your-app>.azurewebsites.net/api/users/{userId}/feeds \
  -H "X-API-Key: <admin-api-key>" \
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
| `/api/feeds/check-integrity` | GET | Admin/Owner | Verify episode metadata and audio blobs exist |

### Episode Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds/{feedId}/episodes` | GET | Public | List episodes for feed |
| `/api/feeds/{feedId}/episodes` | POST | Admin/Owner | Upload episode (201), or with `?normalize=true` for async normalization (202) |
| `/api/feeds/{feedId}/episodes/recent-uploads` | GET | Public | Recent uploads with optional `?source=Browser&limit=5` |
| `/api/feeds/{feedId}/episodes/{id}` | DELETE | Admin/Owner | Delete episode |
| `/api/feeds/{feedId}/episodes/{id}` | PATCH | Admin/Owner | Update episode metadata (title, note) |
| `/api/feeds/{feedId}/episodes/{id}/suggest-title` | POST | Admin/Owner | AI-suggested title for episode |
| `/api/feeds/{feedId}/episodes/{id}/move` | POST | Admin/Owner | Move episode between feeds |
| `/api/feeds/{feedId}/episodes/{id}/copy` | POST | Admin/Owner | Copy episode between feeds |

### Jobs & Events

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds/{feedId}/jobs` | GET | Admin/Owner | Active jobs, or all jobs within `?since=1h` window |
| `/api/feeds/{feedId}/events` | GET | Public | SSE stream for feed events (job/episode changes, cross-device sync) |
| `/api/jobs/{jobId}` | GET | Public | Get normalization job status |
| `/api/jobs/{jobId}/cancel` | POST | Admin/Owner | Cancel a normalization job |
| `/api/jobs/{jobId}/progress` | GET | Public | SSE stream for real-time job progress |

### YouTube Import

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds/{feedId}/youtube` | POST | Admin/Owner | Import YouTube video (returns 202 with job ID) |
| `/api/youtube/cookies` | POST | Admin | Upload cookies.txt for yt-dlp authentication |
| `/api/youtube/cookies/status` | GET | Any | Check if YouTube cookies are uploaded |
| `/api/youtube/cookies` | DELETE | Admin | Remove stored YouTube cookies |

### Icon Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/feeds/{feedId}/icon` | POST | Admin/Owner | Upload/replace feed icon |
| `/api/feeds/{feedId}/icon` | DELETE | Admin/Owner | Remove feed icon |

### User Management

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/users/me` | GET | Any | Get current authenticated user |
| `/api/users` | GET | Admin | List all users |
| `/api/users/{userId}` | GET | Admin | Get user by ID |
| `/api/users` | POST | Admin | Create user (returns API key once) |
| `/api/users/{userId}` | DELETE | Admin | Delete user |
| `/api/users/{userId}/key/regenerate` | POST | Admin/Self | Regenerate user API key |
| `/api/users/{userId}/feeds` | POST | Admin | Grant feed ownership |
| `/api/users/{userId}/feeds/{feedId}` | DELETE | Admin | Revoke feed ownership |

### Public Endpoints

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/{feedId}/feed.xml` | GET | Public | RSS podcast feed (conditional GET: ETag, 304) |
| `/{feedId}/icon.png` | GET | Public | Feed icon (immutable cache, 1 year) |
| `/{feedId}/icon-{size}.png` | GET | Public | Resized icon (192 or 512px, for PWA manifest) |
| `/{feedId}/audio/{filename}` | GET | Public | Stream audio (RFC 7233 range requests) |
| `/{feedId}/push` | GET | Public | Browser upload page (PWA, API key via URL fragment) |
| `/{feedId}/push/manifest.json` | GET | Public | PWA manifest for Android Share Target |
| `/api/feeds/{feedId}/push-subscriptions` | POST | FeedOwner | Subscribe to push notifications (VAPID/Web Push) |
| `/api/feeds/{feedId}/push-subscriptions` | DELETE | FeedOwner | Unsubscribe from push notifications |
| `/api/feeds/{feedId}/push-sessions` | POST | FeedOwner | Track jobIds for batched push notification (one notification when all done) |
| `/health` | GET | Public | Health check (returns blob storage status) |

**Authentication:** `X-API-Key` header required for protected endpoints. Admin has full access; FeedOwner limited to owned feeds.

## Configuration

**Minimal configuration (appsettings.json):**
```json
{
  "Azure": {
    "AccountName": "<your-storage-account>",
    "ContainerName": "<your-container>"
  },
  "Podcast": {
    "Title": "My Podcast",
    "Author": "Your Name",
    "Email": "your@email.com",
    "BaseUrl": "https://<your-app>.azurewebsites.net",
    "ImageUrl": "https://<your-app>.azurewebsites.net/icon.png"
  }
}
```

**Podcast icon:** Upload via API (`POST /api/feeds/{feedId}/icon`) or CLI (`FeatherPod feed set-icon icon.png my-podcast`)

**Additional options:** See configuration files for published date behavior, language, category, and more.

## CLI Tool

FeatherPod includes a command-line tool for managing feeds, episodes, icons, and users:

```bash
# Episodes
FeatherPod episode push *.mp3 -f my-podcast       # Upload with local normalization
FeatherPod episode push *.mp3 -f my-podcast -n     # -n for server-side normalization
FeatherPod episode push *.mp3 -f my-podcast -x     # -x extracts date from file metadata
FeatherPod episode push *.mp3 --delete-after       # Delete source files after upload (--dry-run to preview)
FeatherPod episode list -f my-podcast
FeatherPod episode delete -f my-podcast            # Interactive multi-select
FeatherPod episode rename -f my-podcast --suggest  # AI-suggested title with inline editing
FeatherPod episode move --from feed1 --to feed2 --episode "Episode*"
FeatherPod episode copy --from feed1 --to feed2

# Feeds
FeatherPod feed list
FeatherPod feed create --id my-podcast --title "My Podcast" --author "John Doe"
FeatherPod feed update my-podcast --title "New Title"
FeatherPod feed rename old-id new-id
FeatherPod feed delete my-podcast --force
FeatherPod feed set-icon icon.png my-podcast
FeatherPod feed unset-icon my-podcast
FeatherPod feed push-url -f my-podcast --copy      # Push page URL to clipboard
FeatherPod feed config set -f my-podcast -x true   # Enable date extraction from file metadata
FeatherPod feed check-integrity -f my-podcast

# Users (requires: preferences admin-features enable)
FeatherPod user create / list / delete / rotate-key / grant / revoke

# YouTube (requires: preferences admin-features enable)
FeatherPod youtube set-cookies cookies.txt          # Upload cookies.txt for yt-dlp auth
FeatherPod youtube cookie-status                    # Check cookie upload status

# Preferences (alias: prefs)
FeatherPod preferences key show / set <key> / rotate
FeatherPod preferences normalization enable / disable
FeatherPod preferences auto-connect enable / disable
FeatherPod preferences admin-features enable / disable

# Windows context menu
FeatherPod config context-menu install -f my-podcast                 # Right-click audio files to push
FeatherPod config context-menu install -f my-podcast --delete-after  # Same, but trash source after upload
FeatherPod config context-menu list / remove

# Other
FeatherPod config generate [--select]   # Generate appsettings from defaults
FeatherPod version                      # CLI and server versions
FeatherPod -e Test feed list            # Environment selection (defaults to Prod)
FeatherPod                              # Interactive mode
```

**Interactive mode** provides full feature parity with CLI commands - all operations (push, move, copy, delete, icon management, user management, etc.) are available through menus with arrow key navigation. When pushing episodes, choose between Local (client-side), Server (server-side), or no normalization.

**User preferences** are stored in `%APPDATA%\FeatherPod\preferences.json`:
- API keys (per environment)
- Audio normalization enabled/disabled (per environment, defaults to enabled)
- Auto-connect on startup enabled/disabled (per environment, defaults to enabled)
- Delete-after-upload trash behavior (global)
- Admin features visibility (global)

The CLI prompts for the API key on first use and saves it automatically. If auto-connect is disabled, interactive mode starts in disconnected mode and you can connect manually via Preferences.

**Single-file distribution:** The CLI embeds default configuration as resources. Run `FeatherPod config generate` to create local config files for customization.

## Development

```bash
dotnet build          # Build solution
dotnet test           # Run tests (starts integration tests if Azurite is running)
```

## Architecture

- **.NET 10 ASP.NET Core** - Controllers-based REST API
- **Azure Functions** - Queue-triggered async audio normalization with timer-triggered cleanup
- **Azure OpenAI** - GPT-4o-mini for episode title suggestions via Managed Identity
- **Azure SignalR Service** - Real-time progress push from Functions to Server
- **Multi-feed** - Single instance, multiple isolated feeds
- **Role-based access** - Admin and FeedOwner roles with salted, hashed API keys
- **Azure Blob Storage** - Managed Identity support, hash-based episode IDs
- **Azure Queue/Table Storage** - Job queuing and status tracking for async operations
- **Background services** - Hourly blob sync and stale temp file cleanup
- **PWA/Service Worker** - Offline-capable push page with Android Share Target and Web Push notifications
- **Range requests** - Seeking/resuming in podcast apps

**Supported formats:** MP3, M4A, AAC, WAV, OGG, FLAC

## License

MIT

## Contributing

Pull requests welcome! The automated test environment will deploy your changes for validation before merge.
