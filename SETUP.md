# YMusicLite - Setup and Configuration

## Prerequisites

1. .NET 10.0 SDK
2. Google Cloud Console project with YouTube Data API v3 enabled
3. OAuth 2.0 credentials configured

## Google OAuth Setup

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project or select existing one
3. Enable YouTube Data API v3
4. Create OAuth 2.0 credentials:
   - Application type: Web application  
   - Authorized redirect URIs: `http://localhost:5000/auth` (for development)
5. Copy Client ID and Client Secret

## Configuration

Update `appsettings.json` with your Google OAuth credentials:

```json
{
  "Google": {
    "ClientId": "your-google-client-id.apps.googleusercontent.com",
    "ClientSecret": "your-google-client-secret"
  }
}
```

## Features Implemented

### 1. YouTube OAuth Authentication
- Complete OAuth 2.0 flow for Google/YouTube account access
- Access to private and auto-generated playlists (like "Your Likes")
- Token refresh and management
- Access path: `/auth`

### 2. Download/Conversion System
- Downloads YouTube videos as audio
- Converts to MP3 format using YoutubeExplode.Converter
- Progress tracking and status updates
- Configurable audio quality and parallel downloads
- Safe filename generation and duplicate handling

### 3. Cron-based Scheduling
- Background service for automated playlist synchronization
- Configurable cron expressions for scheduling
- Supports multiple schedules per playlist
- Auto-sync enable/disable functionality

### 4. Advanced Sync Management
- **Sync Mode**: Keeps local directory in sync with YouTube playlist
- **Download new/missing tracks**: Automatically adds new songs
- **Delete removed tracks**: Removes local files no longer in playlist
- Real-time status tracking and progress monitoring
- Comprehensive sync job history

## Usage

### Running the Application

```bash
cd src/YMusicLite
dotnet run
```

Navigate to `http://localhost:5000`

### Basic Workflow

1. **Authenticate**: Go to `/auth` and sign in with your Google account
2. **Add Playlists**: Use the playlists page to add YouTube playlist URLs
3. **Configure Sync**: Go to `/sync` to manage synchronization settings
4. **Start Sync**: Manually trigger sync or enable auto-sync with cron scheduling

### Configuration Options

- `MaxParallelDownloads`: Number of concurrent downloads (default: 3)
- `MinDurationSeconds`: Filter out tracks shorter than this (default: 30s)
- `MaxDurationSeconds`: Filter out tracks longer than this (default: 7200s/2h)
- `AudioQuality`: Download quality - "high", "medium", "low" (default: "high")
- `DownloadPath`: Directory for downloaded files (default: "./data/downloads")

### Cron Scheduling Examples

- `0 3 * * *`: Daily at 3:00 AM
- `0 3 * * 1`: Weekly on Monday at 3:00 AM  
- `0 */6 * * *`: Every 6 hours
- `30 2 1 * *`: Monthly on 1st at 2:30 AM

## Database

Uses LiteDB for local storage:
- User authentication tokens
- Playlist metadata and configuration
- Track information and download status
- Sync job history and logs

Database location: `./data/ymusic.db`

## File Organization

Downloads are organized by playlist:
```
/data/downloads/
  ├── Playlist Name/
  │   ├── Artist - Song Title.mp3
  │   ├── Another Artist - Another Song.mp3
  │   └── ...
  └── Another Playlist/
      └── ...
```

## Troubleshooting

### Authentication Issues
- Verify Google OAuth credentials are correct
- Check that redirect URI matches exactly
- Ensure YouTube Data API v3 is enabled

### Download Issues  
- Check internet connectivity
- Verify playlist is accessible (not private without auth)
- Check available disk space
- Review logs for specific error messages

### Performance
- Adjust `MaxParallelDownloads` based on your connection
- Monitor CPU/memory usage during large playlist syncs
- Consider scheduling large syncs during off-peak hours