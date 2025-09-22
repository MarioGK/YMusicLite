using LiteDB;
using YMusicLite.Models;
 
namespace YMusicLite.Services;
 
public interface IDatabaseService
{
    IRepository<User> Users { get; }
    IRepository<Playlist> Playlists { get; }
    IRepository<Track> Tracks { get; }
    IRepository<SyncJob> SyncJobs { get; }
    IRepository<PkceSession> PkceSessions { get; }
    IRepository<AppConfiguration> AppConfigurations { get; }
    void Initialize();
}
 
public class DatabaseService : IDatabaseService, IDisposable
{
    private readonly ILiteDatabase _database;
    private readonly ILogger<DatabaseService> _logger;
 
    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _logger = logger;
        var dataPath = configuration.GetValue<string>("DataPath");
 
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            var baseDir = AppContext.BaseDirectory;
            dataPath = Path.Combine(baseDir, "data");
            _logger.LogInformation("DataPath not configured; using base directory {BaseDir}", baseDir);
        }
 
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.GetFullPath(Path.Combine(dataPath, "ymusic.db"));
 
        _logger.LogInformation("LiteDB path resolved to {Path}", dbPath);
 
        _database = new LiteDatabase($"Filename={dbPath};Connection=shared");
 
        Users = new LiteDbRepository<User>(_database, _logger);
        Playlists = new LiteDbRepository<Playlist>(_database, _logger);
        Tracks = new LiteDbRepository<Track>(_database, _logger);
        SyncJobs = new LiteDbRepository<SyncJob>(_database, _logger);
        PkceSessions = new LiteDbRepository<PkceSession>(_database, _logger);
        AppConfigurations = new LiteDbRepository<AppConfiguration>(_database, _logger);
 
        Initialize();
    }
 
    public IRepository<User> Users { get; }
    public IRepository<Playlist> Playlists { get; }
    public IRepository<Track> Tracks { get; }
    public IRepository<SyncJob> SyncJobs { get; }
    public IRepository<PkceSession> PkceSessions { get; }
    public IRepository<AppConfiguration> AppConfigurations { get; }
 
    public void Initialize()
    {
        try
        {
            // Create indexes for better performance
            var users = _database.GetCollection<User>();
            users.EnsureIndex(x => x.GoogleId);
            users.EnsureIndex(x => x.Email);
 
            var playlists = _database.GetCollection<Playlist>();
            playlists.EnsureIndex(x => x.YouTubeId);
            playlists.EnsureIndex(x => x.UserId);
 
            var tracks = _database.GetCollection<Track>();
            tracks.EnsureIndex(x => x.YouTubeId);
            tracks.EnsureIndex(x => x.PlaylistId);
 
            var syncJobs = _database.GetCollection<SyncJob>();
            syncJobs.EnsureIndex(x => x.PlaylistId);
            syncJobs.EnsureIndex(x => x.UserId);
 
            var pkce = _database.GetCollection<PkceSession>();
            pkce.EnsureIndex(x => x.State, unique: true);
            pkce.EnsureIndex(x => x.ExpiresAt);
 
            var configs = _database.GetCollection<AppConfiguration>();
            configs.EnsureIndex(x => x.Key, unique: true);
            
            _logger.LogInformation("Database initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }
 
    public void Dispose()
    {
        _database?.Dispose();
    }
}