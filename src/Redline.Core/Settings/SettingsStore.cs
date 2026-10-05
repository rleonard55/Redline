using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Redline.Core.Settings;

/// <summary>
/// Loads, validates and saves <see cref="RedlineSettings"/>. Writes are atomic (temp file + swap).
/// A file that can't be parsed is moved aside to settings.json.bad and defaults are used, so a
/// broken file never stops Redline from starting. Thread-safe.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private RedlineSettings _current;

    /// <param name="path">Backing file; null keeps settings in memory only (tests).</param>
    public SettingsStore(string? path, ILogger<SettingsStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<SettingsStore>.Instance;
        IsNew = path is not null && !File.Exists(path);
        _current = Load();
    }

    /// <summary>No settings file existed before this run: a fresh install (not an upgrade).</summary>
    public bool IsNew { get; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redline", "settings.json");

    public RedlineSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised after settings change, with (old, new). Raised on the caller's thread.</summary>
    public event Action<RedlineSettings, RedlineSettings>? Changed;

    /// <summary>Applies <paramref name="change"/> to the current settings, validates, saves and notifies.</summary>
    public RedlineSettings Update(Func<RedlineSettings, RedlineSettings> change)
    {
        RedlineSettings old, updated;
        lock (_gate)
        {
            old = _current;
            updated = change(old).Validated();
            if (Equivalent(old, updated)) return old;
            _current = updated;
            Save(updated);
        }
        Changed?.Invoke(old, updated);
        return updated;
    }

    private RedlineSettings Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            var defaults = new RedlineSettings().Validated();
            if (_path is not null) Save(defaults); // give users a file to look at
            return defaults;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<RedlineSettings>(File.ReadAllText(_path), Json) ?? new RedlineSettings();
            return loaded.Validated();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var aside = _path + ".bad";
            _logger.LogWarning("settings.json is not valid ({Reason}); moved to {Aside} and using defaults", ex.Message, Path.GetFileName(aside));
            try { File.Move(_path, aside, overwrite: true); } catch (IOException) { }
            var defaults = new RedlineSettings().Validated();
            Save(defaults);
            return defaults;
        }
    }

    private void Save(RedlineSettings settings)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Couldn't save settings: {Reason}", ex.Message);
        }
    }

    private static bool Equivalent(RedlineSettings a, RedlineSettings b) =>
        JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json);
}
