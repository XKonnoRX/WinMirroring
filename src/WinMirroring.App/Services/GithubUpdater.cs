using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinMirroring.App.Services;

/// <summary>A release newer than the running app, with the download URL of its Windows build.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, string ZipUrl);

/// <summary>
/// Checks the project's GitHub Releases for a newer build and installs it through the bundled update script. The
/// release asset is the self-contained zip produced by the release workflow; <see cref="Apply"/> hands it to
/// update.ps1, which closes the app, swaps the files and starts the new version.
/// </summary>
public sealed class GithubUpdater
{
    private const string Owner = "XKonnoRX";
    private const string Repo = "WinMirroring";
    private const string AssetName = "WinMirroring-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger<GithubUpdater> _logger;

    public GithubUpdater(ILogger<GithubUpdater> logger) => _logger = logger;

    public static Version CurrentVersion => Normalize(Assembly.GetEntryAssembly()?.GetName().Version);

    public string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    /// <summary>Returns the newest release when it is newer than the running app, otherwise null.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Update check: GitHub returned {Status}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                return null;
            }

            var version = ParseTag(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
            if (version is null || version <= CurrentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets))
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName
                    && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { Length: > 0 } zip)
                {
                    return new AvailableUpdate(version, tag.GetString() ?? version.ToString(), zip);
                }
            }

            _logger.LogInformation("Update {Tag} has no {Asset} asset", version, AssetName);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logger.LogInformation("Update check failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the update's zip to a temporary file; null when it could not be fetched. GitHub's asset CDN
    /// sometimes answers a 504 Gateway Timeout for a short while after a release, so transient failures are retried.
    /// </summary>
    public async Task<string?> DownloadAsync(AvailableUpdate update, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"WinMirroring-{update.Tag}.zip");
        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Update download got {Status} (attempt {Attempt}/{Total})", (int)response.StatusCode, attempt, attempts);
                    if (attempt < attempts && IsTransient((int)response.StatusCode))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
                        continue;
                    }

                    return null;
                }

                await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < attempts)
            {
                _logger.LogInformation("Update download attempt {Attempt}/{Total} failed: {Message}", attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.LogWarning("Downloading the update failed: {Message}", ex.Message);
                return null;
            }
        }

        return null;
    }

    private static bool IsTransient(int status) => status is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    /// <summary>Hands the downloaded zip to update.ps1, which closes the app, swaps the build and restarts it.</summary>
    public bool Apply(string zipPath)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "update.ps1");
        if (!File.Exists(script))
        {
            _logger.LogWarning("update.ps1 is not next to the app; cannot self-update");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Source \"{zipPath}\"")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("Could not start the updater: {Message}", ex.Message);
            return false;
        }
    }

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var version) ? Normalize(version) : null;
    }

    private static Version Normalize(Version? version) =>
        version is null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(0, version.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinMirroring-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
