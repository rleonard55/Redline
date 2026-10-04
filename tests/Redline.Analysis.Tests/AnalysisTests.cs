using Redline.Analysis;
using Redline.Analysis.Harper;
using Redline.Core.Models;
using Xunit;

namespace Redline.Analysis.Tests;

public sealed class PersonalDictionaryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"redline-dict-{Guid.NewGuid():N}", "personal_dictionary.txt");

    [Fact]
    public void AddedWords_PersistAndMatchCaseInsensitively()
    {
        var dict = new PersonalDictionary(_path);
        dict.Add("Redline");
        dict.Add("Harper");

        var reloaded = new PersonalDictionary(_path);
        Assert.True(reloaded.Contains("redline"));
        Assert.True(reloaded.Contains("HARPER"));
        Assert.Equal(2, reloaded.Words.Count);
    }

    [Fact]
    public void Remove_UpdatesFile()
    {
        var dict = new PersonalDictionary(_path);
        dict.Add("foo");
        Assert.True(dict.Remove("FOO"));
        Assert.False(new PersonalDictionary(_path).Contains("foo"));
    }

    [Fact]
    public void AddRange_AddsNewWordsOnce_AndRaisesChangedOnce()
    {
        var dict = new PersonalDictionary(_path);
        dict.Add("Redline");
        int changes = 0;
        dict.Changed += () => changes++;

        Assert.Equal(2, dict.AddRange(["redline", "Harper", " ", "two words", "Contoso"]));
        Assert.Equal(1, changes);
        Assert.Equal(3, new PersonalDictionary(_path).Words.Count);

        Assert.Equal(0, dict.AddRange(["harper"]));
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    public void Add_RejectsInvalidEntries(string word)
    {
        Assert.Throws<ArgumentException>(() => new PersonalDictionary(null).Add(word));
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>Integration tests against the real Windows Spell Checking API (en-US ships with Windows).</summary>
public class SpellAnalyzerTests
{
    private static TextAnalysisRequest Request(string text, int offset = 0) => new() { Text = text, ContextOffset = offset, SnapshotVersion = 7 };

    [Fact]
    public void Engine_IsAvailable()
    {
        var analyzer = new SpellAnalyzer(new PersonalDictionary(null));
        Assert.True(analyzer.IsAvailable);
        Assert.Equal("en-US", analyzer.LanguageTag);
    }

    [Fact]
    public void SupportedLanguages_IncludesEnglish() =>
        Assert.Contains("en-US", SpellAnalyzer.SupportedLanguages(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void UninstalledLanguage_FallsBackToEnglish()
    {
        var analyzer = new SpellAnalyzer(new PersonalDictionary(null), "xx-XX");
        Assert.True(analyzer.IsAvailable);
        Assert.Equal("en-US", analyzer.LanguageTag);
    }

    [Fact]
    public async Task FlagsMisspelling_WithSuggestionsAndCorrectOffsets()
    {
        var analyzer = new SpellAnalyzer(new PersonalDictionary(null));
        const string text = "This sentance is wrong.";

        var issues = await analyzer.AnalyzeAsync(Request(text, offset: 100), CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal(100 + text.IndexOf("sentance", StringComparison.Ordinal), issue.StartOffset);
        Assert.Equal("sentance", issue.OriginalText);
        Assert.Contains("sentence", issue.Suggestions);
        Assert.Equal(IssueCategory.Spelling, issue.Category);
        Assert.Equal(7, issue.SnapshotVersion);
    }

    [Fact]
    public async Task CorrectText_HasNoIssues()
    {
        var analyzer = new SpellAnalyzer(new PersonalDictionary(null));
        var issues = await analyzer.AnalyzeAsync(Request("The quick brown fox jumps over the lazy dog."), CancellationToken.None);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task PersonalDictionaryWords_AreNotFlagged()
    {
        var dict = new PersonalDictionary(null);
        dict.Add("Zorblax");
        var analyzer = new SpellAnalyzer(dict);

        var issues = await analyzer.AnalyzeAsync(Request("Zorblax and Quuxwidget"), CancellationToken.None);

        Assert.Equal("Quuxwidget", Assert.Single(issues).OriginalText);
    }

    [Fact]
    public async Task OffsetsAreUtf16_AfterSupplementaryCharacters()
    {
        var analyzer = new SpellAnalyzer(new PersonalDictionary(null));
        var text = char.ConvertFromUtf32(0x1F600) + " a mispeled word";

        var issue = Assert.Single(await analyzer.AnalyzeAsync(Request(text), CancellationToken.None));
        Assert.Equal("mispeled", text.Substring(issue.StartOffset, issue.Length));
    }
}

public class HarperResultMapperTests
{
    private const string Json = """
        {"ok":true,"error":null,"lints":[
          {"start":8,"length":2,"kind":"Miscellaneous","message":"Use 'a' before consonant sounds.","priority":31,"suggestions":["a"]},
          {"start":11,"length":4,"kind":"Spelling","message":"Did you mean 'test'?","priority":63,"suggestions":["test"]},
          {"start":0,"length":4,"kind":"Readability","message":"Style note.","priority":127,"suggestions":[]},
          {"start":500,"length":4,"kind":"Grammar","message":"out of range","priority":1,"suggestions":[]}
        ]}
        """;

    private static readonly TextAnalysisRequest Request = new() { Text = "This is an tset.", ContextOffset = 50, SnapshotVersion = 3 };

    [Fact]
    public void Map_TranslatesOffsetsAndCategories_SkipsSpellingAndOutOfRange()
    {
        var issues = HarperResultMapper.Map(Json, Request);

        Assert.Equal(2, issues.Count);

        var grammar = issues[0];
        Assert.Equal(58, grammar.StartOffset);
        Assert.Equal("an", grammar.OriginalText);
        Assert.Equal(IssueCategory.Grammar, grammar.Category);
        Assert.Equal(["a"], grammar.Suggestions);
        Assert.Equal("Harper", grammar.Analyzer);

        Assert.Equal(IssueCategory.Style, issues[1].Category);
    }

    [Fact]
    public void Map_IncludeSpelling_KeepsSpellingLints()
    {
        var issues = HarperResultMapper.Map(Json, Request, includeSpelling: true);
        Assert.Contains(issues, i => i.Category == IssueCategory.Spelling && i.OriginalText == "tset");
    }

    [Fact]
    public void Map_ErrorResponse_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HarperResultMapper.Map("""{"ok":false,"error":"harper panicked","lints":[]}""", Request));
    }
}

public class HarperAnalyzerTests
{
    [Fact]
    public async Task Analyzer_WorksWhenNativeLibraryPresent_DegradesWhenAbsent()
    {
        var analyzer = new HarperAnalyzer();
        var issues = await analyzer.AnalyzeAsync(new TextAnalysisRequest { Text = "This is an test." }, CancellationToken.None);

        if (analyzer.IsAvailable)
            Assert.Contains(issues, i => i.OriginalText == "an");
        else
            Assert.Empty(issues); // harper_ffi.dll not built: must be a no-op, not a crash
    }
}
