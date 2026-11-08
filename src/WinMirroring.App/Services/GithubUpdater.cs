using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinMirroring.App.Services;

/// <summary>A release newer than the running app, with the download URL of its Windows build.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, string ZipUrl);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,

    /// <summary>GitHub could not be asked (it answered an error, or nothing): it is not known whether one exists.</summary>
    Failed,
}

/// <summary>What a check found; <see cref="Error"/> says why it failed, in Russian, for the user.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, AvailableUpdate? Update = null, string? Error = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate);

    public static UpdateCheckResult Fail(string error) => new(UpdateCheckStatus.Failed, Error: error);
}

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

    private const int CheckAttempts = 3;

    private static readonly HttpClient Http = CreateClient(followRedirects: true);
    private static readonly HttpClient NoRedirect = CreateClient(followRedirects: false);
    private readonly ILogger<GithubUpdater> _logger;

    public GithubUpdater(ILogger<GithubUpdater> logger) => _logger = logger;

    public static Version CurrentVersion => Normalize(Assembly.GetEntryAssembly()?.GetName().Version);

    public string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    /// <summary>
    /// Asks GitHub for the newest release. A failure is reported as such, never as "up to date": GitHub sometimes
    /// answers anonymous requests with 502/503/504 (for instance from a VPN's shared address), so each way is retried,
    /// and when the API keeps failing the release page's redirect to its tag is tried instead.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        var viaApi = await CheckViaApiAsync(ct).ConfigureAwait(false);
        if (viaApi.Status != UpdateCheckStatus.Failed)
        {
            return viaApi;
        }

        var viaWeb = await CheckViaReleasePageAsync(ct).ConfigureAwait(false);
        return viaWeb.Status != UpdateCheckStatus.Failed ? viaWeb : viaApi;
    }

    private async Task<UpdateCheckResult> CheckViaApiAsync(CancellationToken ct)
    {
        var error = "GitHub не ответил";
        for (var attempt = 1; attempt <= CheckAttempts; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", attemptCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    error = $"GitHub ответил {(int)response.StatusCode}";
                    _logger.LogInformation("Update check: GitHub API returned {Status} (attempt {Attempt}/{Total})", (int)response.StatusCode, attempt, CheckAttempts);
                    if (attempt < CheckAttempts && IsTransient((int)response.StatusCode))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                        continue;
                    }

                    return UpdateCheckResult.Fail(error);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: attemptCts.Token).ConfigureAwait(false);
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
                var version = ParseTag(tag);
                if (version is null)
                {
                    return UpdateCheckResult.Fail("в ответе GitHub нет номера версии");
                }

                if (version <= CurrentVersion)
                {
                    return UpdateCheckResult.UpToDate;
                }

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName
                            && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { Length: > 0 } zip)
                        {
                            return new UpdateCheckResult(UpdateCheckStatus.Available, new AvailableUpdate(version, tag!, zip));
                        }
                    }
                }

                _logger.LogInformation("Update {Tag} has no {Asset} asset", tag, AssetName);
                return UpdateCheckResult.Fail($"в релизе {tag} нет файла {AssetName}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                error = ex is TaskCanceledException ? "GitHub не ответил вовремя" : ex.Message;
                _logger.LogInformation("Update check attempt {Attempt}/{Total} failed: {Message}", attempt, CheckAttempts, ex.Message);
                if (attempt < CheckAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                return UpdateCheckResult.Fail("GitHub прислал непонятный ответ: " + ex.Message);
            }
        }

        return UpdateCheckResult.Fail(error);
    }

    /// <summary>
    /// The release page "…/releases/latest" redirects to "…/releases/tag/vX.Y.Z", which gives the newest tag without
    /// the API; the build is then at the usual download address for that tag.
    /// </summary>
    private async Task<UpdateCheckResult> CheckViaReleasePageAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var response = await NoRedirect.GetAsync($"https://github.com/{Owner}/{Repo}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var location = response.Headers.Location?.ToString();
                var tag = location is null ? null : location[(location.LastIndexOf('/') + 1)..];
                if ((int)response.StatusCode is >= 300 and < 400 && ParseTag(tag) is { } version)
                {
                    return version <= CurrentVersion
                        ? UpdateCheckResult.UpToDate
                        : new UpdateCheckResult(UpdateCheckStatus.Available, new AvailableUpdate(version, tag!, $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{AssetName}"));
                }

                _logger.LogInformation("Update check: release page returned {Status}", (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("Update check via the release page failed: {Message}", ex.Message);
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        return UpdateCheckResult.Fail("страница релизов GitHub не ответила");
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

    private static HttpClient CreateClient(bool followRedirects)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects })
        {
            Timeout = followRedirects ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinMirroring-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
