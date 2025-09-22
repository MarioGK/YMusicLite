# YMusicLite

A modern YouTube playlist manager and downloader built with Blazor Server and MudBlazor.

## Features
 * Lightweight and fast

## Google / YouTube Authentication

YMusicLite now uses a desktop-style OAuth flow with only a public Google OAuth Client ID:

Client ID: `198027251119-c04chsbao214hcplsf697u2smo682vuq.apps.googleusercontent.com`

We request only the `youtube.readonly` scope to read your private playlists. Tokens (access & refresh) are securely stored in the local LiteDB database along with granted scopes. A minimal PKCE structure is in place; due to current library limitations the code verifier is reserved for future enhancement.

After authenticating via `/auth`, your private playlists are fetched and displayed. You can revoke access, and the application will handle token refresh automatically when near expiry.

### Multi-Account Support
You can add multiple YouTube accounts. The authentication page lists existing authenticated accounts and lets you switch between them. Each account is keyed by its YouTube Channel ID rather than a transient state value for stability.

### Resilience & Retries
The token exchange now includes exponential backoff (up to 4 attempts) for transient Google errors (5xx, 429, internal_failure). This improves reliability when Google's OAuth endpoints experience intermittent issues.

### PKCE Persistence
The PKCE code_verifier is now stored briefly (15 minutes) in the local database. This allows you to start authentication, restart the app (or reload the server), and still successfully complete the callback without losing the verifier. After a successful (or attempted) token exchange the verifier entry is deleted. Expired entries are cleaned up opportunistically.

If you encounter the error `client_secret is missing` during token exchange, verify that:
1. The OAuth client in Google Cloud Console is of type "Desktop app" (Installed application)
2. You're using the correct public client ID shown above
3. The redirect URI you supply matches exactly what you passed when building the authorization URL
4. You are not reusing an already consumed authorization code

- 🎵 **Modern UI**: Built with MudBlazor featuring a dark theme with configurable colors
- 📊 **Dashboard**: Statistics and activity overview
- ⚡ **Real-Time Metrics**: Live download speed, active download count, average progress, and sync status
- 📝 **Playlist Management**: Create, edit, and manage YouTube playlists
- 🎧 **Audio Download**: Convert YouTube videos to MP3 format (automatic MP3 conversion via YoutubeExplode + FFmpeg integration)
- 🔄 **Sync Management**: Manual, scheduled, and strict (removal) sync modes
- ⏱️ **Auto Sync Scheduling**: Multiple CRON expressions per playlist (e.g. "0 3 * * *" for daily 3 AM)
- ❤️ **Liked Songs Support (LM)**: Virtual "Liked Songs" playlist (myRating=like) for authenticated accounts
- 🧩 **Incremental & Strict Modes**: Keep all historical tracks or prune removed ones based on Sync Mode toggle
- 🗂️ **Playlist Detail View**: Per-track status (Pending, Downloading, Converting, Completed, Error) with retry (single or bulk)
- ♻️ **Retry Failed/Pending**: Re-attempt only failed/pending tracks with progress feedback
- ❌ **Cancellation**: Cancel active sync jobs safely (graceful token-based cancellation)
- 🏷️ **Tagging & Filtering**: Add arbitrary tags to playlists; real-time text + tag filtering
- 🔁 **Bulk Sync**: Trigger sync for all idle playlists with one click
- 🔐 **Multi-Account OAuth**: Multiple Google accounts with PKCE desktop-style OAuth and token persistence
- 🗄️ **Local Storage**: LiteDB for offline persistence
- ⚙️ **Configurable**: Flexible settings for downloads and audio quality
- 🧪 **Test Suite**: xUnit + bUnit tests for services and Blazor components
- 📈 **Metrics Aggregation**: Centralized runtime metrics (active sync %, download speed, progress averages)
- 🐳 **Docker Ready**: Containerized for easy deployment

## Technologies Used

- **.NET 10**: Latest .NET framework
- **Blazor Server**: Server-side rendering for dynamic UI
- **MudBlazor**: Material Design component library
- **LiteDB**: NoSQL database for local storage
- **YoutubeExplode**: YouTube API integration
- **Serilog**: Structured logging
- **FFMpeg**: Audio conversion
- **Docker**: Containerization

## Quick Start with Docker

1. Clone the repository:
   ```bash
   git clone https://github.com/MarioGK/YMusicLite.git
   cd YMusicLite
   ```

2. Run with Docker Compose:
   ```bash
   docker-compose up -d
   ```

3. Access the application at `http://localhost:8080`

## Development Setup

### Prerequisites

- .NET 10 SDK
- Docker (optional)

### Local Development

1. Clone the repository:
   ```bash
   git clone https://github.com/MarioGK/YMusicLite.git
   cd YMusicLite
   ```

2. Navigate to the source directory:
   ```bash
   cd src
   ```

3. Restore dependencies:
   ```bash
   dotnet restore
   ```

4. Run the application:
   ```bash
   dotnet run --project YMusicLite
   ```

5. Open your browser and navigate to `http://localhost:5051`

## Configuration

### Environment Variables

- `ASPNETCORE_ENVIRONMENT`: Development/Production
- `DataPath`: Path for database and downloads (default: `/app/data`)
- `MaxParallelDownloads`: Maximum concurrent downloads (default: 3)
- `DownloadPath`: Directory for downloaded files

### Configuration Files

- `appsettings.json`: Primary configuration
- `config.yaml`: YAML configuration (optional)

Example `config.yaml`:
```yaml
DataPath: "/app/data"
DownloadPath: "/app/data/downloads"
MaxParallelDownloads: 3
AudioQuality: "high"
MinDurationSeconds: 30
MaxDurationSeconds: 7200
```

## Docker

### Building the Image

```bash
docker build -t ymusic-lite .
```

### Running the Container

```bash
docker run -d \
  --name ymusic-lite \
  -p 8080:80 \
  -v ./data:/app/data \
  -e DataPath=/app/data \
  ymusic-lite
```

### Docker Compose

Use the provided `docker-compose.yml` for easy deployment:

```bash
docker-compose up -d
```

## Usage

1. **Dashboard**: View statistics and real-time metrics (speed, active downloads, sync progress)
2. **Add Playlists**: Navigate to Playlists and add YouTube playlist URLs
3. **Configure Settings**: Adjust download preferences and quality settings
4. **Monitor Downloads**: Track download progress and sync history
5. **Open Playlist Detail**: Click a playlist name to view per-track statuses and retry failures
6. **Enable Auto Sync**: Edit a playlist, toggle Auto Sync, and specify one or more CRON expressions (one per line)
7. **Use Liked Songs (LM)**: Enter "LM" as the playlist identifier after authenticating to sync liked videos
8. **Tag & Organize**: Add tags (space or comma separated) in the playlist dialog; filter via the top bar
9. **Bulk Sync**: Use the Bulk Sync button to start sync for all non-busy playlists

### Auto Sync & Scheduling

Playlists support multiple CRON expressions (standard 5-field format). Example expressions:
- `0 3 * * *` → Daily at 03:00
- `*/30 * * * *` → Every 30 minutes
- `0 */6 * * *` → Every 6 hours

When Auto Sync is enabled:
- Each expression is scheduled independently
- Schedules are reloaded on startup
- Disabling Auto Sync unschedules all associated timers

### Liked Songs Virtual Playlist (LM)

Specify `LM` (or a URL containing `list=LM`) to create a virtual playlist of your liked YouTube / YouTube Music videos:
- Requires an authenticated Google/YouTube account
- Tracks enumerated via `videos.list` `myRating=like`
- Duration filtering still applies
- Video count is dynamic and may appear as 0 until sync enumerates tracks

### Playlist Detail & Track Retry

The playlist details page provides:
- Real-time auto-refresh (5s interval)
- Track progress bars (download, convert, complete, error)
- Bulk retry for failed/pending tracks
- Single-track retry action
- Error tooltips per track

### Strict Sync Mode

If enabled (Sync Mode = Strict), tracks removed from the YouTube playlist are also deleted locally (and their files removed).
If disabled, removed tracks remain archived locally.

### Cancellation

While a sync is running or downloading:
- Cancel from the playlist card (Cancel button replaces Sync)
- State reverts to Idle (or Error if failure occurred)
- Active sync metrics update and job logs persisted

### Testing

Run tests after restoring dependencies:
```bash
dotnet test
```

Test coverage includes:
- SyncService behavior (idempotent start, cancellation)
- Playlists page UI logic (rendering, sync trigger, validation)
- Metrics basic calculations
- Database CRUD (integration sample)

### Development Notes

- All timestamps stored in UTC
- `UpdatedAt` maintained for playlists & tracks on state transitions
- Retry & incremental updates are safe across restarts due to persisted state in LiteDB

## Architecture

### Data Models
- **User**: User preferences and authentication
- **Playlist**: YouTube playlist metadata and settings
- **Track**: Individual song information and download status
- **SyncJob**: Synchronization job history and status

### Services
- **DatabaseService**: LiteDB data access layer
- **YouTubeService**: YouTube API integration
- **DownloadService**: Audio download and conversion
- **SyncService**: Orchestrates playlist synchronization jobs
- **MetricsService**: Aggregates runtime metrics for UI components

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- [MudBlazor](https://mudblazor.com/) for the amazing component library
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) for YouTube integration
- [LiteDB](https://www.litedb.org/) for the embedded database

## Support

For issues and feature requests, please use the [GitHub Issues](https://github.com/MarioGK/YMusicLite/issues) page.
This is a tool that you can selfhost and download an sync a youtube playlist to your computer
