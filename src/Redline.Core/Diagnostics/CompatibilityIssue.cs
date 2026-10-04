using System.Text;
using System.Text.Json;

namespace Redline.Core.Diagnostics;

/// <summary>
/// Builds a pre-filled "new issue" link for one app's compatibility entry. Nothing is sent: the link
/// opens GitHub's issue form in the browser, where the user reads the text and decides whether to submit.
/// </summary>
public static class CompatibilityIssue
{
    /// <summary>GitHub rejects very long URLs; entries are ~1 KB, so this only guards against odd ones.</summary>
    public const int MaxUrlLength = 7500;

    public static string Title(AppCompatibilityEntry entry) =>
        $"Compatibility: {entry.Process} ({CompatibilityLog.StatusOf(entry)})";

    public static string Body(CompatibilityReport header, AppCompatibilityEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("**What happened?**").Append(nl);
        sb.Append("<!-- Describe the problem: which part of the app, what you expected, what Redline did. ")
          .Append("This issue is public, so please don't paste private text. -->").Append(nl).Append(nl);
        sb.Append("**Compatibility record** (from Settings > Compatibility; counts only, no typed text)").Append(nl).Append(nl);
        sb.Append($"- Redline {header.RedlineVersion}, Windows {header.Windows}").Append(nl);
        sb.Append($"- Status: {CompatibilityLog.StatusOf(entry)}").Append(nl).Append(nl);
        sb.Append("```json").Append(nl).Append(json).Append(nl).Append("```").Append(nl);
        return sb.ToString();
    }

    /// <summary>The new-issue URL for <c>owner/repo</c>, or null if it would be too long for GitHub.</summary>
    public static Uri? Url(string owner, string repository, CompatibilityReport header, AppCompatibilityEntry entry)
    {
        var url = $"https://github.com/{owner}/{repository}/issues/new" +
                  $"?title={Uri.EscapeDataString(Title(entry))}" +
                  $"&body={Uri.EscapeDataString(Body(header, entry))}" +
                  "&labels=compatibility";
        return url.Length <= MaxUrlLength ? new Uri(url) : null;
    }
}
