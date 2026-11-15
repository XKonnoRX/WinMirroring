using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMirroring.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as settings.json inside the data directory.</summary>
public sealed class SettingsStore : IDisposable
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

    /// <summary>
    /// Held for as long as this store lives, so that another node can tell the directory is taken. Kept in a field
    /// for exactly that reason: a mutex no one holds a reference to is released by the finalizer.
    /// </summary>
    private readonly Mutex? _claim;

    public SettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        FilePath = Path.Combine(DataDirectory, "settings.json");
        SharedWithAnotherNode = !TryClaim(DataDirectory, out _claim);
    }

    /// <summary>
    /// Another node in another process already had this directory when this store opened it. Advisory: nothing
    /// here refuses to work because of it, because a device that will not start is worse than one that shares.
    /// </summary>
    public bool SharedWithAnotherNode { get; }

    public string DataDirectory { get; }

    public string FilePath { get; }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMirroring");

    /// <summary>
    /// Whether some other node in some other process already has this directory. Worth asking before building a
    /// node on it, because a data directory is a device: one identity, one certificate, one list of paired peers.
    /// Two nodes sharing one hold the same file in memory and write their whole view of it whenever anything
    /// changes, so the second one to save silently replaces the first one's identity and everything it was paired
    /// with — the device loses the pairings it had, and the devices on the other side are left trusting a
    /// certificate nobody presents any more. That really happened: this tool, pointed at the app's own directory,
    /// took the app's identity off the disk while the app went on running with it in memory.
    ///
    /// This is a question, not a claim: it is asked and let go of at once, so the answer is a moment old by the
    /// time it is read. That is enough for the mistake it is here to catch, which is a person or a script naming a
    /// directory that is plainly in use, not two nodes starting in the same millisecond.
    /// </summary>
    public static bool IsInUseByAnotherNode(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!TryClaim(Path.GetFullPath(dataDirectory), out var claim))
        {
            return true;
        }

        if (claim is not null)
        {
            claim.ReleaseMutex();
            claim.Dispose();
        }

        return false;
    }

    /// <summary>
    /// Takes the directory for this process. False only when another process clearly holds it; true otherwise,
    /// with <paramref name="claim"/> set when there is something to hold on to and null when the answer could not
    /// be had at all. Not being able to tell counts as free: this exists to catch a mistake, and must never be
    /// what keeps a device from starting.
    ///
    /// A named mutex rather than a lock file, because a file would have to be closed before its directory could be
    /// removed — which is what the tests do to their temporary directories all day — whereas this is let go of
    /// when the process ends, however it ends. Named in the session rather than across the machine: a global name
    /// needs a privilege that is not always there, and a restricted process that cannot even make the thing would
    /// be told the directory is free by the very code meant to tell it otherwise. Everything this guards against
    /// happens inside one signed-in session anyway — the app and this tool, side by side, as the same person.
    ///
    /// The claim belongs to the thread that took it, so a node is expected to be built on a thread that lives as
    /// long as it does — the one the program starts on, in both the app and the tool. A thread that ends while the
    /// node lives costs nothing worse than the claim: the next node to ask is told the directory is free, which is
    /// the direction this is meant to fail in.
    /// </summary>
    private static bool TryClaim(string fullPath, out Mutex? claim)
    {
        var name = "Local\\WinMirroring-data-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())))[..32];

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            if (mutex.WaitOne(TimeSpan.Zero))
            {
                claim = mutex;
                return true;
            }

            mutex.Dispose();
            claim = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // Whoever had it is gone without releasing it, so the directory is free and now ours.
            claim = mutex;
            return true;
        }
        catch (Exception)
        {
            // No such thing as a locked-out device: an answer that cannot be had is not an answer of "taken".
            mutex?.Dispose();
            claim = null;
            return true;
        }
    }

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

    /// <summary>Lets go of the claim on the data directory, so another node may take it.</summary>
    public void Dispose()
    {
        if (_claim is null)
        {
            return;
        }

        try
        {
            _claim.ReleaseMutex();
        }
        catch (Exception e) when (e is ApplicationException or ObjectDisposedException)
        {
            // Not held any more (released already, or this is a second call); there is nothing to let go of.
        }

        _claim.Dispose();
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
