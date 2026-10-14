using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jsr4Z8.Core.Settings;

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON at
/// <c>%LOCALAPPDATA%\JSR4Z8\settings.json</c> (Task 14; <c>baseDir</c> injectable for
/// tests). Robustness contract: a missing file is plain defaults (nothing written); a
/// corrupt file is replaced with the defaults (best effort — a transient IO failure
/// must never clobber the file); legacy files still carrying a removed field (e.g.
/// VramGb) load fine — System.Text.Json ignores unknown members; Save is atomic
/// (write *.tmp, then rename over) so a crash mid-write can never destroy the previous
/// settings. The app composes ONE instance and adapts it to the UI's ISettingsProvider:
/// <see cref="Current"/> is the in-memory last Load/Save, so every Save (settings
/// dialog, dark-picker memory) is immediately visible to consumers that read
/// CurrentSettings.
/// </summary>
public sealed class SettingsService
{
    public const string AppDirName = "JSR4Z8";
    public const string FileName = "settings.json";

    // Indented + raw CJK + string enums: the file is meant to be human-readable/hand-editable.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;
    private readonly object _writeLock = new();
    private volatile AppSettings _current = new();

    public SettingsService(string? baseDir = null)
    {
        string root = baseDir ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(root, AppDirName, FileName);
        Load();
    }

    /// <summary>In-memory current settings — set by every Load/Save; the basis of the
    /// UI-side ISettingsProvider adapter.</summary>
    public AppSettings Current => _current;

    /// <summary>Reads the file (defaults when missing or corrupt) and updates
    /// <see cref="Current"/>. A corrupt file is rewritten with the defaults.</summary>
    public AppSettings Load()
    {
        AppSettings loaded = new AppSettings();
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Corrupt file → defaults, and rewrite it so the next start is clean.
            loaded = new AppSettings();
            try { WriteAtomic(loaded); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { /* transient read failure: keep defaults, do NOT rewrite */ }
        catch (UnauthorizedAccessException) { }
        _current = loaded;
        return loaded;
    }

    /// <summary>Atomically writes the file and updates <see cref="Current"/>.
    /// Returns the record that was persisted.</summary>
    public AppSettings Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writeLock)
        {
            WriteAtomic(settings);
        }
        _current = settings;
        return settings;
    }

    /// <summary>tmp + rename: the destination is only ever replaced by a fully written
    /// file; the tmp file is removed even when the move fails.</summary>
    private void WriteAtomic(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        string tmp = _settingsPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, _settingsPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
