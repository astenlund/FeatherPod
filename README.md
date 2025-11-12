# FeatherPod

A cloud-native .NET podcast feed server for Azure with Blob Storage integration. Host your audio content (like NotebookLM audio overviews) with iTunes-compatible RSS feeds.

## Features

- **Azure Blob Storage** - Scalable cloud storage for audio files
- **RSS podcast feed** - iTunes spec compatible
- **REST API** - Manage episodes with API key authentication
- **Hash-based episode IDs** - Preserves play progress when re-adding files
- **Managed Identity** - Secure Azure authentication without secrets
- **Podcast icon support** - Custom branding for your feed

## Prerequisites

**For Azure Deployment:**
- .NET 9 SDK
- Azure Storage Account
- Azure App Service (optional, can run anywhere)

**For Local Development:**
- .NET 9 SDK
- Azurite (Azure Storage Emulator): `npm install -g azurite`

## Quick Start

### Local Development

**1. Start Azurite:**
```bash
azurite --silent --location $env:USERPROFILE\.azurite --debug $env:USERPROFILE\.azurite\debug.log
```

**2. Run FeatherPod:**
```bash
dotnet run --project FeatherPod
```

**3. Access the feed:**
```
http://localhost:5070/feed.xml
```

The development configuration (`appsettings.Development.json`) is already set up to use Azurite with `UseDevelopmentStorage=true`.

### Azure Deployment

**Option 1: Bicep (Recommended)**

Deploy complete infrastructure with one command:
```bash
az login
az group create --name featherpod-rg --location swedencentral
az deployment group create \
  --resource-group featherpod-rg \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/parameters.json
```

This creates: Storage Account, blob containers, App Service Plan, App Service, Managed Identity, and RBAC.

Then deploy the application:
```powershell
.\Deploy-FeatherPod.ps1
```

Or manually:
```bash
dotnet publish FeatherPod/FeatherPod.csproj -c Release -o publish
cd publish && powershell.exe -Command "Compress-Archive -Path * -DestinationPath ../deploy.zip -Force"
az webapp deploy --resource-group featherpod-rg --name featherpod --src-path ../deploy.zip --type zip
```

**Option 2: Manual Setup**

**1. Create Azure Resources:**
- Create an Azure Storage Account
- Create two blob containers: `audio` and `metadata`
- Create an Azure App Service (optional, can deploy anywhere)

**2. Configure Application Settings:**

In your Azure App Service configuration (or appsettings.json for other hosting):

```json
{
  "Azure": {
    "AccountName": "your-storage-account-name",
    "AudioContainerName": "audio",
    "MetadataContainerName": "metadata"
  },
  "ApiKey": "your-strong-random-api-key",
  "Podcast": {
    "Title": "My Podcast",
    "Description": "Podcast description",
    "Author": "Your Name",
    "Email": "your@email.com",
    "BaseUrl": "https://your-app-name.azurewebsites.net",
    "ImageUrl": "https://your-app-name.azurewebsites.net/icon.png"
  }
}
```

**3. Enable Managed Identity (Recommended):**
- Enable system-assigned managed identity on your App Service
- Grant "Storage Blob Data Contributor" role to the managed identity on your Storage Account
- This eliminates the need for connection strings in configuration

**Alternative: Use Connection String:**
```json
{
  "Azure": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    "AudioContainerName": "audio",
    "MetadataContainerName": "metadata"
  }
}
```

**4. Deploy:**
```bash
dotnet publish -c Release
# Deploy using your preferred method (Azure DevOps, GitHub Actions, FTP, etc.)
```

**Important for Azure App Service:** Program.cs is already configured to read the PORT environment variable. Do not hardcode URLs or ports in appsettings.json.

**5. Subscribe in your podcast app:**
```
https://your-app-name.azurewebsites.net/feed.xml
```

## Usage

### Adding Episodes

**Via REST API (requires API key):**
```bash
curl -X POST https://your-app-name.azurewebsites.net/api/episodes \
  -H "X-API-Key: your-api-key" \
  -F "file=@audio.mp3" \
  -F "title=My Episode" \
  -F "description=Episode description"
```

**With explicit published date:**
```bash
curl -X POST https://your-app-name.azurewebsites.net/api/episodes \
  -H "X-API-Key: your-api-key" \
  -F "file=@audio.mp3" \
  -F "title=My Episode" \
  -F "publishedDate=2025-01-15T10:30:00Z"
```

**Using file metadata for published date:**
```bash
curl -X POST https://your-app-name.azurewebsites.net/api/episodes \
  -H "X-API-Key: your-api-key" \
  -F "file=@audio.mp3" \
  -F "title=My Episode" \
  -F "useMetadataForPublishedDate=true"
```

**Directly to Azure Blob Storage:**
- Upload audio files to the `audio` container in your Storage Account
- Restart the app or call the sync endpoint to detect new files

### Removing Episodes

**Via REST API (requires API key):**
```bash
curl -X DELETE https://your-app-name.azurewebsites.net/api/episodes/{episode-id} \
  -H "X-API-Key: your-api-key"
```

**Directly from Azure Blob Storage:**
- Delete the file from the `audio` container
- Metadata will be cleaned up on next sync

### Listing Episodes

**Via REST API (public):**
```bash
curl https://your-app-name.azurewebsites.net/api/episodes
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
- Set API key in configuration: `"ApiKey": "your-strong-random-key"`
- Read-only endpoints (feed, audio streaming) are public

## Configuration

### Podcast Metadata

```json
{
  "Podcast": {
    "Title": "My Podcast",
    "Description": "Podcast description",
    "Author": "Your Name",
    "Email": "your@email.com",
    "Language": "en",
    "Category": "Technology",
    "BaseUrl": "https://your-app-name.azurewebsites.net",
    "ImageUrl": "https://your-app-name.azurewebsites.net/icon.png",
    "ImageVersion": "1",
    "UseFileMetadataForPublishDate": false
  }
}
```

### Published Date Behavior

FeatherPod supports flexible published date configuration with the following priority order:

1. **Explicit API parameter** (highest priority) - Set `publishedDate` when uploading
2. **Per-request metadata flag** - Set `useMetadataForPublishedDate=true` in API call
3. **Global metadata config** - Set `"UseFileMetadataForPublishDate": true` in appsettings.json
4. **Current time** (default fallback) - Uses upload time

When using file metadata (options 2 or 3), FeatherPod reads from:
- TagLib DateTagged field (audio file metadata tags)
- MP4 container creation time (for M4A/MP4 files)
- File modified/creation timestamps

### Azure Storage

**Managed Identity (Production - Recommended):**
```json
{
  "Azure": {
    "AccountName": "yourstorageaccount",
    "AudioContainerName": "audio",
    "MetadataContainerName": "metadata"
  }
}
```

**Connection String (Alternative):**
```json
{
  "Azure": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...",
    "AudioContainerName": "audio",
    "MetadataContainerName": "metadata"
  }
}
```

**Local Development (Azurite):**
```json
{
  "Azure": {
    "ConnectionString": "UseDevelopmentStorage=true",
    "AudioContainerName": "audio",
    "MetadataContainerName": "metadata"
  }
}
```

## Podcast Icon

Place a 1024x1024 PNG or JPG icon at `FeatherPod/wwwroot/icon.png` and configure the URL:

```json
{
  "Podcast": {
    "ImageUrl": "https://your-app-name.azurewebsites.net/icon.png",
    "ImageVersion": "1"
  }
}
```

The icon is served as a static file and must be included in your deployment.

### Forcing Icon Refresh

Podcast apps like Pocket Casts aggressively cache artwork. To force apps to refresh the icon after you change it:

1. Replace `wwwroot/icon.png` with your new icon
2. Increment the `ImageVersion` in your configuration (e.g., from "1" to "2")
3. Restart/redeploy your app

The RSS feed will include a cache-busting query parameter (e.g., `icon.png?v=2`), which podcast apps will treat as a new URL and fetch the updated icon. This works without changing the actual filename.

## Details

**Supported formats:** MP3, M4A, AAC, WAV, OGG, FLAC

**Episode IDs:** Hash-based (`SHA256(filename:filesize)`) - Re-adding the same file preserves podcast app metadata and play progress

**Audio streaming:** Supports HTTP range requests for seeking and resuming playback

**Storage:** All audio files and metadata stored in Azure Blob Storage for scalability

**Development:**
```bash
dotnet build          # Build solution
dotnet test           # Run tests (integration tests skip if Azurite not running)
```

## Testing

FeatherPod includes both unit tests and integration tests using xUnit.

**Run all tests (requires Azurite for integration tests):**
```bash
# Terminal 1: Start Azurite~~~~
azurite --silent --location $env:USERPROFILE\.azurite --debug $env:USERPROFILE\.azurite\debug.log

# Terminal 2: Run tests
dotnet test
```

**Run without Azurite:**
```bash
# Unit tests run, integration tests automatically skip
dotnet test
```

Integration tests use a custom `[AzuriteFact]` attribute that automatically skips tests with a helpful message if Azurite is not running. This allows for fast unit test execution without external dependencies.

## Architecture

- **.NET 9 Minimal API** - Lightweight HTTP endpoints
- **Azure Blob Storage** - Cloud-native file storage
- **Managed Identity** - Secure credential-less authentication
- **API Key Authentication** - Protects management endpoints
- **TestBlobStorageService** - Local file-based testing without Azure dependencies

## License

MIT

## Contributing

Issues and pull requests welcome at [your-repo-url]
