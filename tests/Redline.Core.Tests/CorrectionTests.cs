using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Xunit;

namespace Redline.Core.Tests;

public class CorrectionMathTests
{
    [Fact]
    public void Apply_ReplacesRange()
    {
        Assert.Equal("This is a test", CorrectionMath.Apply("This is an test", new TextRange(8, 2), "a"));
    }

    [Theory]
    [InlineData("the the cat", 4, 3, "the cat")]        // takes the space before
    [InlineData("I saw the the.", 10, 3, "I saw the.")] // before punctuation
    [InlineData("end word", 4, 4, "end")]               // at end of text
    [InlineData("the the\nnext", 4, 3, "the\nnext")]    // before a line break
    [InlineData("word next", 0, 4, "next")]             // at text start: takes the space after
    [InlineData("a,word,b", 2, 4, "a,,b")]              // no adjacent spaces: unchanged
    public void ExpandDeletion_TakesOneAdjacentSpace(string text, int start, int length, string expected)
    {
        var range = CorrectionMath.ExpandDeletion(text, new TextRange(start, length));
        Assert.Equal(expected, CorrectionMath.Apply(text, range, string.Empty));
    }

    [Fact]
    public void ProviderUnitCandidates_PlainText_HasOneCandidate()
    {
        Assert.Equal([new TextRange(8, 2)], CorrectionMath.ProviderUnitCandidates("This is an test", new TextRange(8, 2)));
    }

    [Fact]
    public void ProviderUnitCandidates_CountsCrLfAndSurrogatesAsOneUnitInAlternates()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var text = "a\r\nb" + emoji + " word";
        int start = text.IndexOf("word", StringComparison.Ordinal);
        Assert.Equal(7, start); // a, CR, LF, b, emoji (2 units), space

        var candidates = CorrectionMath.ProviderUnitCandidates(text, new TextRange(start, 4));

        Assert.Equal(new TextRange(7, 4), candidates[0]);    // UTF-16
        Assert.Contains(new TextRange(6, 4), candidates);    // surrogate pair as one, or CRLF as one
        Assert.Contains(new TextRange(5, 4), candidates);    // both
        Assert.Equal(3, candidates.Count);                   // the two single conventions coincide here
    }
}

public class IgnoreListTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"redline-ignore-{Guid.NewGuid():N}", "ignored_rules.txt");

    private static TextIssue Issue(string text, string rule) => new()
    {
        StartOffset = 0, Length = text.Length, OriginalText = text, Category = IssueCategory.Grammar, Message = "m", RuleId = rule,
    };

    [Fact]
    public void SessionIgnore_IsPerRuleAndText_AndNotPersisted()
    {
        var list = new IgnoreList(_path);
        list.IgnoreInSession(Issue("an", "Harper:A"));

        Assert.True(list.IsIgnored(Issue("an", "Harper:A")));
        Assert.False(list.IsIgnored(Issue("an", "Harper:B")));
        Assert.False(list.IsIgnored(Issue("the", "Harper:A")));
        Assert.False(new IgnoreList(_path).IsIgnored(Issue("an", "Harper:A")));
    }

    [Fact]
    public void IgnoredRules_Persist_AndCanBeRestored()
    {
        var list = new IgnoreList(_path);
        int changes = 0;
        list.Changed += () => changes++;

        list.IgnoreRule("Harper:Style:Too wordy.");
        Assert.True(new IgnoreList(_path).IsIgnored(Issue("anything", "Harper:Style:Too wordy.")));

        Assert.True(list.RestoreRule("Harper:Style:Too wordy."));
        Assert.False(new IgnoreList(_path).IsIgnored(Issue("anything", "Harper:Style:Too wordy.")));
        Assert.Equal(2, changes);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

public class IssueCacheManagerTests
{
    private sealed class FakeDictionary : IPersonalDictionary
    {
        private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
        public event Action? Changed;
        public bool Contains(string word) => _words.Contains(word);
        public void Add(string word) { _words.Add(word); Changed?.Invoke(); }
        public bool Remove(string word) => _words.Remove(word);
        public IReadOnlyCollection<string> Words => _words;
    }

    private static TextIssue Spelling(string word, int at) => new()
    {
        StartOffset = at, Length = word.Length, OriginalText = word, Category = IssueCategory.Spelling, Message = "m", RuleId = "Spelling:Misspelling",
    };

    private static TextIssue Grammar(string text, int at) => new()
    {
        StartOffset = at, Length = text.Length, OriginalText = text, Category = IssueCategory.Grammar, Message = "m", RuleId = "Harper:G",
    };

    [Fact]
    public void Get_RejectsOtherSnapshotVersions()
    {
        var cache = new IssueCacheManager(new FakeDictionary(), new IgnoreList(null));
        cache.Update("s1", IssueSet.From([Spelling("tset", 0)], 5));

        Assert.NotNull(cache.Get("s1", 5));
        Assert.Null(cache.Get("s1", 6));
        Assert.NotNull(cache.Get("s1"));
        Assert.Null(cache.Get("other"));
    }

    [Fact]
    public void Update_NeverGoesBackwards()
    {
        var cache = new IssueCacheManager(new FakeDictionary(), new IgnoreList(null));
        cache.Update("s1", IssueSet.From([Spelling("new", 0)], 9));
        cache.Update("s1", IssueSet.From([Spelling("old", 0)], 3));
        Assert.Equal("new", cache.Get("s1")!.Issues.Single().OriginalText);
    }

    [Fact]
    public void DictionaryAndIgnores_FilterImmediately_AndRepublish()
    {
        var dict = new FakeDictionary();
        var ignores = new IgnoreList(null);
        var cache = new IssueCacheManager(dict, ignores);
        var published = new List<IssueSet>();
        cache.IssuesChanged += (_, e) => published.Add(e.Issues);

        cache.Update("s1", IssueSet.From([Spelling("Redline", 0), Grammar("an", 10), Spelling("tset", 20)], 1));
        Assert.Equal(3, published[^1].Issues.Count);

        dict.Add("redline");
        Assert.Equal(["an", "tset"], published[^1].Issues.Select(i => i.OriginalText));

        ignores.IgnoreInSession(Grammar("an", 10));
        Assert.Equal(["tset"], published[^1].Issues.Select(i => i.OriginalText));
        Assert.Equal(["tset"], cache.Get("s1", 1)!.Issues.Select(i => i.OriginalText));
    }

    [Fact]
    public void DictionaryWords_OnlyFilterSpelling()
    {
        var dict = new FakeDictionary();
        dict.Add("an");
        var cache = new IssueCacheManager(dict, new IgnoreList(null));
        cache.Update("s1", IssueSet.From([Grammar("an", 0)], 1));
        Assert.Single(cache.Get("s1")!.Issues);
    }

    [Fact]
    public void Capacity_EvictsLeastRecentlyUpdated()
    {
        var cache = new IssueCacheManager(new FakeDictionary(), new IgnoreList(null), capacity: 2);
        cache.Update("a", IssueSet.Empty);
        cache.Update("b", IssueSet.Empty);
        cache.Update("c", IssueSet.Empty);
        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("b"));
        Assert.NotNull(cache.Get("c"));
    }
}
