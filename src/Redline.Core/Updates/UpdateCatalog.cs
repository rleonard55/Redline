using System.Text.Json;
using System.Text.RegularExpressions;

namespace Redline.Core.Updates;

/// <summary>The installer asset of a GitHub release. <see cref="Sha256"/> is lowercase hex, from GitHub's asset digest.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>A release newer than the running version, with its MSI.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, Uri PageUrl, ReleaseAsset Installer);

/// <summary>
/// Reads GitHub's "latest release" response (GET /repos/{owner}/{repo}/releases/latest). Releases are
/// tagged vX.Y.Z and carry Redline-X.Y.Z-x64.msi (see installer/release.ps1).
/// </summary>
public static partial class UpdateCatalog
{
    public const string Owner = "rleonard55";
    public const string Repository = "Redline";

    public static Uri LatestReleaseApi { get; } = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Owner}/{Repository}/releases");

    [GeneratedRegex(@"^Redline-\d+\.\d+\.\d+-x64\.msi$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();

    [GeneratedRegex(@"^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Sha256Digest();

    /// <summary>"v1.2.3" or "1.2.3" → 1.2.3 (missing parts are 0; pre-release suffixes are rejected).</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag?.Trim() ?? string.Empty;
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var parsed) || parsed.Revision > 0) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Drops the revision so 0.5.0.0 (assembly) compares equal to 0.5.0 (tag).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// The update described by <paramref name="latestReleaseJson"/>, or null when it isn't newer than
    /// <paramref name="current"/>, is a draft or pre-release, or has no installer asset.
    /// </summary>
    /// <exception cref="JsonException">The response isn't the expected JSON.</exception>
    public static AvailableUpdate? Parse(string latestReleaseJson, Version current)
    {
        using var doc = JsonDocument.Parse(latestReleaseJson);
        var root = doc.RootElement;

        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Str(root, "tag_name");
        if (!TryParseTag(tag, out var version) || version <= Normalize(current)) return null;
        if (!Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var page)) page = ReleasesPage;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name");
            if (name is null || !InstallerName().IsMatch(name)) continue;
            if (!Uri.TryCreate(Str(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                continue;

            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            var digest = Sha256Digest().Match(Str(asset, "digest") ?? string.Empty);
            return new AvailableUpdate(version, tag!, page,
                new ReleaseAsset(name, url, size, digest.Success ? digest.Groups[1].Value.ToLowerInvariant() : null));
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
