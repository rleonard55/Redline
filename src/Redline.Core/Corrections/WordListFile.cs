using System.Text;

namespace Redline.Core.Corrections;

/// <summary>
/// Reads word lists to import into the personal dictionary: Office custom dictionaries
/// (<c>%APPDATA%\Microsoft\UProof\CUSTOM.DIC</c>, UTF-16 with a BOM), Windows spelling dictionaries
/// (<c>#LID 1033</c> header line) and plain text files, one word per line.
/// </summary>
public static class WordListFile
{
    /// <summary>Longer lines aren't words (a stray binary or prose file).</summary>
    public const int MaxWordLength = 64;

    /// <summary>Folder Word keeps its custom dictionaries in, if it exists.</summary>
    public static string? OfficeDictionaryFolder
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UProof");
            return Directory.Exists(folder) ? folder : null;
        }
    }

    public static IReadOnlyList<string> Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Distinct single words in file order. Skips blank lines, <c>#</c> comment/header lines, and
    /// entries with whitespace or control characters (the dictionary holds single words).
    /// </summary>
    public static IReadOnlyList<string> Parse(byte[] bytes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var line in Decode(bytes).Split('\n'))
        {
            var word = line.Trim();
            if (word.Length is 0 or > MaxWordLength || word[0] == '#')
                continue;
            if (word.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                continue;
            if (seen.Add(word))
                words.Add(word);
        }
        return words;
    }

    /// <summary>BOM if present; otherwise UTF-8, or Latin-1 for older ANSI files that aren't valid UTF-8.</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..])
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..])
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        int start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
