using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMirroring.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as settings.json inside the data directory.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();

    public SettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        FilePath = Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }

    public string FilePath { get; }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMirroring");

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch (JsonException)
            {
                // Keep the unreadable file for inspection and start fresh.
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}
