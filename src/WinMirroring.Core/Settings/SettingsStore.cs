using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMirroring.Core.Settings;

/// <summary>
/// A settings file that could not be read: where it was put so that nothing writes over it (null if even that
/// failed, and it is still where it was), and what was wrong with it.
/// </summary>
public sealed record UnreadableSettings(string? KeptAs, string Reason);

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

    /// <summary>
    /// What the last <see cref="Load"/> found in place of settings it could read, or null when it read them or
    /// there was no file at all. Here so that whoever asked for the settings can say so out loud: this class has
    /// no log of its own, and a device that quietly comes up as a new one is the thing to be avoided.
    /// </summary>
    public UnreadableSettings? Unreadable { get; private set; }

    /// <summary>
    /// Reads the settings, or starts with fresh ones when there is no file — or when the file cannot be read as
    /// settings, which is the case that needs care. That file is the device: its id, the certificate it is known
    /// by, everything it is paired with. Fresh settings in its place are a new device, and the first save writes
    /// them over the old one for good.
    ///
    /// So the unreadable file is moved out of the way first, under a name with the time in it, and
    /// <see cref="Unreadable"/> says where it went. It used to be copied to one fixed name instead, which the next
    /// bad file replaced, and nothing anywhere said that it had happened. A file cut short by a power failure can
    /// usually be mended by hand; one that has been written over cannot.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            Unreadable = null;
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            string why;
            try
            {
                var json = File.ReadAllText(FilePath);
                if (JsonSerializer.Deserialize<AppSettings>(json, Options) is { } settings)
                {
                    return settings;
                }

                // The one word "null" is good JSON and no settings at all.
                why = "the file holds no settings";
            }
            catch (JsonException ex)
            {
                why = ex.Message;
            }

            Unreadable = new UnreadableSettings(SetAside(DateTime.Now), why);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Moves the settings file out of the way of the next save and returns where it is now, or null when it could
    /// be neither moved nor copied — which leaves nothing more to try, and is said rather than hidden.
    /// </summary>
    private string? SetAside(DateTime now)
    {
        var name = $"{FilePath}.corrupt-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var kept = name;
        for (var again = 2; File.Exists(kept); again++)
        {
            // Twice within a second: the earlier one is not to be written over either.
            kept = $"{name}-{again}";
        }

        try
        {
            File.Move(FilePath, kept);
            return kept;
        }
        catch (Exception moving) when (moving is IOException or UnauthorizedAccessException)
        {
            // Something else has the file open in a way that will not let it be renamed. It was read a moment
            // ago, so it can very likely be copied.
            try
            {
                File.Copy(FilePath, kept);
                return kept;
            }
            catch (Exception copying) when (copying is IOException or UnauthorizedAccessException)
            {
                return null;
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
