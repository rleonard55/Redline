using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Redline.Analysis.Grmr;

/// <summary>A downloadable model file and what it must hash to.</summary>
public sealed record ModelFile(string FileName, string Url, long Size, string Sha256);

/// <summary>The model file Redline uses: GRMR-V3-G1B (Gemma 3 1B fine-tuned for grammar correction), Q4_K_M.</summary>
public static class GrmrModel
{
    public static readonly ModelFile File = new(FileName, Url, Size, Sha256);

    public const string FileName = "GRMR-V3-G1B-Q4_K_M.gguf";
    public const string DisplayName = "GRMR-V3-G1B";

    /// <summary>Pinned to a commit of qingy2024/GRMR-V3-G1B-GGUF so the hash below always matches.</summary>
    public const string Url =
        "https://huggingface.co/qingy2024/GRMR-V3-G1B-GGUF/resolve/7d2aa920de02ee255d852b00e90c39d9662274f5/" + FileName;

    public const long Size = 806_056_704;
    public const string Sha256 = "e01b82bf4e779fbda738662e83ea3c99ddbfe84c5235eb70fa8b4a6e9de17317";

    public const string ModelPage = "https://huggingface.co/qingy2024/GRMR-V3-G1B";
    public const string BaseModelTerms = "https://ai.google.dev/gemma/terms";
}

public enum ModelState { NotInstalled, Downloading, Installed, Failed }

/// <summary>
/// The downloaded grammar model in %LOCALAPPDATA%\Redline\models. Downloads resume from a
/// .partial file, and the result is only used after its size and SHA-256 match <see cref="GrmrModel"/>.
/// Thread-safe; <see cref="Changed"/> is raised on a background thread.
/// </summary>
public sealed class GrmrModelStore : IDisposable
{
    private readonly HttpClient _http;
    private readonly ModelFile _file;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _download;
    private long _lastReportedPercent = -1;

    public GrmrModelStore(string directory, string userAgentVersion, ILogger<GrmrModelStore>? logger = null,
        HttpMessageHandler? handler = null, ModelFile? file = null)
    {
        Directory = directory;
        _file = file ?? GrmrModel.File;
        _logger = logger ?? NullLogger<GrmrModelStore>.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // an 800 MB download; stalls are caught by the read timeout below
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Redline", userAgentVersion));
        State = File.Exists(ModelPath) && new FileInfo(ModelPath).Length == _file.Size ? ModelState.Installed : ModelState.NotInstalled;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redline", "models");

    public string Directory { get; }
    public string ModelPath => Path.Combine(Directory, _file.FileName);

    /// <summary>Download size in bytes.</summary>
    public long Size => _file.Size;
    private string PartialPath => ModelPath + ".partial";

    public ModelState State { get; private set; }

    /// <summary>0..1 while downloading.</summary>
    public double Progress { get; private set; }

    /// <summary>Why the last download failed (for the Settings window).</summary>
    public string? Error { get; private set; }

    /// <summary>The verified model path, or null when it isn't installed.</summary>
    public string? InstalledPath => State == ModelState.Installed ? ModelPath : null;

    public event Action? Changed;

    /// <summary>Starts (or resumes) the download in the background. No-op if installed or already downloading.</summary>
    public void StartDownload()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (State is ModelState.Installed or ModelState.Downloading) return;
            _download = new CancellationTokenSource();
            token = _download.Token;
            SetState(ModelState.Downloading, progress: 0, error: null);
        }
        Changed?.Invoke();
        _ = Task.Run(() => DownloadAsync(token));
    }

    public void CancelDownload()
    {
        lock (_gate) _download?.Cancel();
    }

    /// <summary>
    /// Deletes the model (and any partial download). The caller must have released the model
    /// first; returns false if the file is still in use.
    /// </summary>
    public bool Remove()
    {
        CancelDownload();
        try
        {
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
            if (File.Exists(PartialPath)) File.Delete(PartialPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Couldn't remove the grammar model: {Reason}", ex.Message);
            return false;
        }
        lock (_gate) SetState(ModelState.NotInstalled, 0, null);
        Changed?.Invoke();
        _logger.LogInformation("Grammar model removed");
        return true;
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            long have = File.Exists(PartialPath) ? new FileInfo(PartialPath).Length : 0;
            if (have > _file.Size) { File.Delete(PartialPath); have = 0; }

            using var request = new HttpRequestMessage(HttpMethod.Get, _file.Url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            HttpResponseMessage response;
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(60)); // the client has no overall timeout (see ctor)
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
            }
            using var _ = response;
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                have = 0; // server ignored the range: start over
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Downloading grammar model ({Mode})", have > 0 ? $"resuming at {have / (1024 * 1024)} MB" : "from the start");

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(PartialPath, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = have;
                while (true)
                {
                    // A stalled connection fails after 60 s without data instead of hanging forever.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(TimeSpan.FromSeconds(60));
                    int read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    if (total > _file.Size) throw new InvalidDataException("The download is larger than expected.");
                    ReportProgress((double)total / _file.Size);
                }
            }

            if (new FileInfo(PartialPath).Length != _file.Size)
                throw new InvalidDataException("The download ended early.");
            string hash;
            await using (var stream = File.OpenRead(PartialPath))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(hash, _file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(PartialPath);
                throw new InvalidDataException("The downloaded file didn't match the expected checksum.");
            }

            File.Move(PartialPath, ModelPath, overwrite: true);
            _logger.LogInformation("Grammar model installed");
            lock (_gate) SetState(ModelState.Installed, 1, null);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Grammar model download cancelled");
            lock (_gate) SetState(ModelState.NotInstalled, 0, null); // the .partial stays for a resume
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Grammar model download failed: {Reason}", ex.Message);
            var message = ex is HttpRequestException or OperationCanceledException or IOException
                ? "The download failed. Check your connection and try again."
                : ex.Message;
            lock (_gate) SetState(ModelState.Failed, 0, message);
            Changed?.Invoke();
        }
    }

    private void ReportProgress(double progress)
    {
        long percent = (long)(progress * 100);
        if (Interlocked.Exchange(ref _lastReportedPercent, percent) == percent) return;
        Progress = progress;
        Changed?.Invoke();
    }

    /// <summary>Caller holds _gate, and raises <see cref="Changed"/> after releasing it.</summary>
    private void SetState(ModelState state, double progress, string? error)
    {
        State = state;
        Progress = progress;
        Error = error;
        Interlocked.Exchange(ref _lastReportedPercent, -1);
    }

    public void Dispose()
    {
        CancelDownload();
        _http.Dispose();
    }
}
