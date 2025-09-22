using System;
using YMusicLite.Models;
 
namespace YMusicLite.Services;

public interface IConfigService
{
    Task<string> GetDownloadPathAsync();
    Task SetDownloadPathAsync(string path);
    Task<string?> GetValueAsync(string key);
    Task SetValueAsync(string key, string value);

    // Event fired when a config value is changed via this service.
    event Action<string, string>? ConfigValueChanged;
}

public class ConfigService : IConfigService
{
    private const string DownloadPathKey = "DownloadPath";
    private readonly IDatabaseService _database;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigService> _logger;

    // Raised when a configuration value is changed via this service.
    public event Action<string, string>? ConfigValueChanged;

    public ConfigService(IDatabaseService database, IConfiguration configuration, ILogger<ConfigService> logger)
    {
        _database = database;
        _configuration = configuration;
        _logger = logger;

        // Ensure there is an initial value for DownloadPath in DB using environment/appsettings value.
        Task.Run(async () =>
        {
            try
            {
                var existing = await _database.AppConfigurations.FindOneAsync(c => c.Key == DownloadPathKey);
                if (existing == null)
                {
                    var envValue = _configuration.GetValue<string>(DownloadPathKey, "/app/data/downloads");
                    var cfg = new AppConfiguration { Key = DownloadPathKey, Value = envValue, UpdatedAt = DateTime.UtcNow };
                    await _database.AppConfigurations.CreateAsync(cfg);
                    _logger.LogInformation("Seeded initial configuration {Key} with value {Value}", DownloadPathKey, envValue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed initial configuration");
            }
        });
    }

    public async Task<string> GetDownloadPathAsync()
    {
        var cfg = await _database.AppConfigurations.FindOneAsync(c => c.Key == DownloadPathKey);
        if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Value))
        {
            return cfg.Value;
        }

        // Fallback to IConfiguration (environment / appsettings)
        return _configuration.GetValue<string>(DownloadPathKey, "/app/data/downloads");
    }

    public async Task SetDownloadPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path cannot be empty", nameof(path));

        var cfg = await _database.AppConfigurations.FindOneAsync(c => c.Key == DownloadPathKey);
        if (cfg == null)
        {
            cfg = new AppConfiguration { Key = DownloadPathKey, Value = path, UpdatedAt = DateTime.UtcNow };
            await _database.AppConfigurations.CreateAsync(cfg);
        }
        else
        {
            cfg.Value = path;
            cfg.UpdatedAt = DateTime.UtcNow;
            await _database.AppConfigurations.UpdateAsync(cfg);
        }

        _logger.LogInformation("DownloadPath updated to {Path}", path);
    }

    public async Task<string?> GetValueAsync(string key)
    {
        var cfg = await _database.AppConfigurations.FindOneAsync(c => c.Key == key);
        return cfg?.Value;
    }

    public async Task SetValueAsync(string key, string value)
    {
        var cfg = await _database.AppConfigurations.FindOneAsync(c => c.Key == key);
        if (cfg == null)
        {
            cfg = new AppConfiguration { Key = key, Value = value, UpdatedAt = DateTime.UtcNow };
            await _database.AppConfigurations.CreateAsync(cfg);
        }
        else
        {
            cfg.Value = value;
            cfg.UpdatedAt = DateTime.UtcNow;
            await _database.AppConfigurations.UpdateAsync(cfg);
        }

        try
        {
            ConfigValueChanged?.Invoke(key, value);
            _logger.LogInformation("Configuration key '{Key}' changed to '{Value}'", key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error raising ConfigValueChanged for key {Key}", key);
        }
    }
}