using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Redline.Core.Updates;

namespace Redline.App.Updates;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Checks GitHub Releases for a newer Redline, downloads its MSI into %LOCALAPPDATA%\Redline\updates
/// and verifies it against the SHA-256 digest GitHub publishes for the asset. Installing is always the
/// user's click: msiexec upgrades in place, closing this instance ("Redline.exe --exit") and starting
/// the new one. Copies not installed by the MSI (build folders) only report that an update exists.
/// </summary>
/// <remarks>The only network traffic Redline makes: one HTTPS request to api.github.com per check.</remarks>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Version _current;
    private readonly string _directory;
    private readonly SemaphoreSlim _oneCheck = new(1, 1);

    public UpdateService(string version, ILogger<UpdateService> logger, string? directory = null)
    {
        _logger = logger;
        _current = UpdateCatalog.TryParseTag(version.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
        _directory = directory ?? DefaultDirectory;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Redline", _current.ToString()));
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redline", "updates");

    /// <summary>True when running from the MSI's install folder, so an MSI upgrade replaces this copy.</summary>
    public static bool IsInstalledCopy
    {
        get
        {
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Redline");
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
            return string.Equals(Path.GetFullPath(exeDir).TrimEnd('\\'), Path.GetFullPath(installDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public AvailableUpdate? Update { get; private set; }
    public string? InstallerPath { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> or <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Checks for (and, for installed copies, downloads) an update. Never throws.</summary>
    public async Task CheckAsync()
    {
        if (!await _oneCheck.WaitAsync(0).ConfigureAwait(false)) return; // one at a time
        try
        {
            if (State == UpdateState.Ready) return; // already downloaded; nothing newer is needed until it's installed
            Set(UpdateState.Checking, "Checking for updates…");

            var update = await FetchLatestAsync().ConfigureAwait(false);
            LastChecked = DateTimeOffset.Now;
            if (update is null)
            {
                Set(UpdateState.UpToDate, $"Redline {_current} is up to date.");
                return;
            }

            Update = update;
            _logger.LogInformation("Update available: {Version}", update.Version);
            if (!IsInstalledCopy)
            {
                Set(UpdateState.Available, $"Redline {update.Version} is available. This copy wasn't installed with the installer, so download it from GitHub.");
                return;
            }

            Set(UpdateState.Downloading, $"Downloading Redline {update.Version}…");
            InstallerPath = await DownloadAsync(update.Installer).ConfigureAwait(false);
            Set(UpdateState.Ready, $"Redline {update.Version} is ready to install.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("Update check failed: {Reason}", ex.Message);
            Set(UpdateState.Failed, ex is InvalidDataException ? ex.Message : "Couldn't check for updates. Try again later.");
        }
        finally
        {
            _oneCheck.Release();
        }
    }

    /// <summary>Starts the downloaded MSI (it closes this instance). False if nothing is ready.</summary>
    public bool Install()
    {
        if (State != UpdateState.Ready || InstallerPath is null || !File.Exists(InstallerPath)) return false;
        _logger.LogInformation("Installing update {Version}", Update?.Version);
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{InstallerPath}\" /passive") { UseShellExecute = true });
        return true;
    }

    private async Task<AvailableUpdate?> FetchLatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateCatalog.LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases published yet
        response.EnsureSuccessStatusCode();
        return UpdateCatalog.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false), _current);
    }

    /// <summary>Downloads to a .partial file, verifies size and SHA-256, then moves it into place.</summary>
    private async Task<string> DownloadAsync(ReleaseAsset asset)
    {
        if (asset.Sha256 is null)
            throw new InvalidDataException("The update has no checksum, so it can't be verified. Download it from GitHub instead.");

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, asset.Name);
        if (File.Exists(path) && await HashAsync(path).ConfigureAwait(false) == asset.Sha256)
        {
            RemoveOthers(path);
            return path;
        }

        var partial = path + ".partial";
        using (var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target).ConfigureAwait(false);
        }

        long size = new FileInfo(partial).Length;
        var hash = await HashAsync(partial).ConfigureAwait(false);
        if ((asset.Size > 0 && size != asset.Size) || hash != asset.Sha256)
        {
            File.Delete(partial);
            throw new InvalidDataException("The downloaded update didn't match its checksum and was discarded.");
        }

        File.Move(partial, path, overwrite: true);
        RemoveOthers(path);
        return path;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }

    /// <summary>Old installers and abandoned partial downloads.</summary>
    private void RemoveOthers(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Set(UpdateState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _http.Dispose();
        _oneCheck.Dispose();
    }
}
