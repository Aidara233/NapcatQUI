using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NapcatQUI.Core.Configuration;

public class ConfigManager
{
    private readonly string _configPath;
    private readonly ILogger<ConfigManager> _logger;
    private AppConfig? _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ConfigManager(string configDir, ILogger<ConfigManager> logger)
    {
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        _configPath = Path.Combine(configDir, "config.json");
        _logger = logger;
    }

    public AppConfig Load()
    {
        if (_cache != null) return _cache;

        if (!File.Exists(_configPath))
        {
            _cache = new AppConfig();
            Save(_cache);
            _logger.LogInformation("Created default config at {Path}", _configPath);
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            _cache = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            _logger.LogInformation("Loaded config from {Path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config, using defaults");
            _cache = new AppConfig();
        }

        return _cache;
    }

    public void Save(AppConfig? config = null)
    {
        if (config != null) _cache = config;
        if (_cache == null) return;

        try
        {
            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save config");
        }
    }

    public string GetEffectiveDbPath(string appDataDir)
    {
        var config = Load();
        if (!string.IsNullOrEmpty(config.Settings.DbPath))
            return config.Settings.DbPath;
        return Path.Combine(appDataDir, "napcatqui.db");
    }
}
