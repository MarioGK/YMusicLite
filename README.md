# YMusicLite

A modern YouTube playlist manager and downloader built with Blazor Server and MudBlazor.

## Features

- 🎵 **Modern UI**: Built with MudBlazor featuring a dark theme with configurable colors
- 📊 **Dashboard**: Statistics and activity overview
- ⚡ **Real-Time Metrics**: Live download speed, active download count, average progress, and sync status
- 📝 **Playlist Management**: Create, edit, and manage YouTube playlists
- 🎧 **Audio Download**: Convert YouTube videos to MP3 format
- 🔄 **Sync Management**: Automatic and scheduled playlist synchronization
- 🗄️ **Local Storage**: LiteDB for offline persistence
- ⚙️ **Configurable**: Flexible settings for downloads and audio quality
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
