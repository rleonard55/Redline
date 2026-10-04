using System.Text.Json;
using Redline.Core.Updates;
using Xunit;

namespace Redline.Core.Tests;

public class UpdateCatalogTests
{
    private const string Digest = "0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789abcdef";

    private static string Release(string tag = "v0.6.0", bool draft = false, bool prerelease = false, string assets = "") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/rleonard55/Redline/releases/tag/{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [ {{assets}} ]
        }
        """;

    private static string Asset(string name, string url, string? digest = Digest, long size = 1234) =>
        $$"""
        { "name": "{{name}}", "size": {{size}}, "browser_download_url": "{{url}}"{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} }
        """;

    private static readonly string Msi = Asset("Redline-0.6.0-x64.msi",
        "https://github.com/rleonard55/Redline/releases/download/v0.6.0/Redline-0.6.0-x64.msi");

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V0.6", 0, 6, 0)]
    [InlineData(" v2.0.0 ", 2, 0, 0)]
    public void ParsesTags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateCatalog.TryParseTag(tag, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("v1.2.3-beta")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2.3.4")]
    public void RejectsOtherTags(string? tag) => Assert.False(UpdateCatalog.TryParseTag(tag, out _));

    [Fact]
    public void FindsNewerReleaseWithInstallerAndDigest()
    {
        var update = UpdateCatalog.Parse(Release(assets: Msi), new Version(0, 5, 0, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 6, 0), update.Version);
        Assert.Equal("v0.6.0", update.Tag);
        Assert.Equal("Redline-0.6.0-x64.msi", update.Installer.Name);
        Assert.Equal(1234, update.Installer.Size);
        Assert.Equal(Digest.ToLowerInvariant(), update.Installer.Sha256);
        Assert.Equal("https://github.com/rleonard55/Redline/releases/tag/v0.6.0", update.PageUrl.ToString());
    }

    [Theory]
    [InlineData("v0.5.0")] // same as running (0.5.0.0)
    [InlineData("v0.4.9")]
    public void IgnoresSameOrOlder(string tag) =>
        Assert.Null(UpdateCatalog.Parse(Release(tag, assets: Msi), new Version(0, 5, 0, 0)));

    [Fact]
    public void IgnoresDraftsAndPrereleases()
    {
        Assert.Null(UpdateCatalog.Parse(Release(draft: true, assets: Msi), new Version(0, 5, 0)));
        Assert.Null(UpdateCatalog.Parse(Release(prerelease: true, assets: Msi), new Version(0, 5, 0)));
    }

    [Fact]
    public void PicksTheMsiAmongOtherAssets()
    {
        var assets = string.Join(",",
            Asset("THIRD-PARTY-NOTICES.txt", "https://github.com/x/notices.txt"),
            Asset("Redline-0.6.0-arm64.msi", "https://github.com/x/arm.msi"),
            Msi);
        Assert.Equal("Redline-0.6.0-x64.msi", UpdateCatalog.Parse(Release(assets: assets), new Version(0, 5, 0))!.Installer.Name);
    }

    [Fact]
    public void NoInstallerMeansNoUpdate() =>
        Assert.Null(UpdateCatalog.Parse(Release(assets: Asset("notes.txt", "https://github.com/x/notes.txt")), new Version(0, 5, 0)));

    [Fact]
    public void RejectsNonHttpsDownloads() =>
        Assert.Null(UpdateCatalog.Parse(Release(assets: Asset("Redline-0.6.0-x64.msi", "http://example.com/r.msi")), new Version(0, 5, 0)));

    [Fact]
    public void MissingDigestLeavesHashNull()
    {
        var update = UpdateCatalog.Parse(Release(assets: Asset("Redline-0.6.0-x64.msi", "https://github.com/x/r.msi", digest: null)), new Version(0, 5, 0));
        Assert.Null(update!.Installer.Sha256);
    }

    [Fact]
    public void ThrowsOnGarbage() =>
        Assert.ThrowsAny<JsonException>(() => UpdateCatalog.Parse("<html>rate limited</html>", new Version(0, 5, 0)));
}
