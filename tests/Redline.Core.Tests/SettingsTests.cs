using Redline.Core.Settings;
using Xunit;

namespace Redline.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+.", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBE)]
    [InlineData("ctrl + alt + space", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20)]
    [InlineData("Win+Alt+Space", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x20)]
    [InlineData("Ctrl+Shift+F12", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7B)]
    [InlineData("Ctrl+Alt+;", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBA)]
    [InlineData("Alt+R", HotkeyModifiers.Alt, 'R')]
    public void Parses(string text, HotkeyModifiers modifiers, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(modifiers, vk), hk);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]                // no modifier
    [InlineData("Shift+A")]          // Shift alone would steal typing
    [InlineData("Ctrl+Alt+")]        // no key
    [InlineData("Hyper+A")]          // unknown modifier
    [InlineData("Ctrl+Alt+Banana")]  // unknown key
    public void RejectsInvalid(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Theory]
    [InlineData("Ctrl+Alt+.")]
    [InlineData("Win+Alt+Space")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("Ctrl+Alt+;")]
    [InlineData("Ctrl+Alt+K")]
    public void RoundTrips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(text, hk.ToString());
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"redline-settings-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void MissingFile_CreatesDefaults()
    {
        var store = new SettingsStore(FilePath);
        Assert.True(File.Exists(FilePath));
        Assert.True(store.Current.Writing.Spelling);
        Assert.False(store.Current.Writing.StyleSuggestions);
        Assert.Equal("Ctrl+Alt+.", store.Current.General.Hotkey);
        Assert.True(store.Current.General.HoverSuggestions);
        Assert.Contains("\"analysisDelayMs\": 300", File.ReadAllText(FilePath));
    }

    [Fact]
    public void IsNew_OnlyWhenNoFileExisted()
    {
        var first = new SettingsStore(FilePath);
        Assert.True(first.IsNew);
        Assert.False(first.Current.General.WelcomeShown);
        first.Update(s => s with { General = s.General with { WelcomeShown = true } });

        var second = new SettingsStore(FilePath);
        Assert.False(second.IsNew);
        Assert.True(second.Current.General.WelcomeShown);
        Assert.False(new SettingsStore(null).IsNew);
    }

    [Fact]
    public void Update_PersistsAndNotifies()
    {
        var store = new SettingsStore(FilePath);
        RedlineSettings? seen = null;
        store.Changed += (_, updated) => seen = updated;

        store.Update(s => s with { Writing = s.Writing with { Grammar = false } });

        Assert.False(seen!.Writing.Grammar);
        Assert.False(new SettingsStore(FilePath).Current.Writing.Grammar);
    }

    [Fact]
    public void Update_WithNoEffectiveChange_DoesNotNotify()
    {
        var store = new SettingsStore(FilePath);
        int changes = 0;
        store.Changed += (_, _) => changes++;
        store.Update(s => s with { });
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Validation_ClampsAndNormalizes()
    {
        var store = new SettingsStore(FilePath);
        store.Update(s => s with
        {
            General = s.General with { AnalysisDelayMs = 5, Hotkey = "Shift+A", Language = "  " },
            Applications = new ApplicationSettings { Excluded = ["keepass", "C:\\Tools\\Vault.exe", "KeePass.exe", " "] },
        });

        var c = store.Current;
        Assert.Equal(GeneralSettings.MinDelayMs, c.General.AnalysisDelayMs);
        Assert.Equal(GeneralSettings.DefaultHotkey, c.General.Hotkey);
        Assert.Equal("en-US", c.General.Language);
        Assert.Equal(["keepass.exe", "Vault.exe"], c.Applications.Excluded);
    }

    [Fact]
    public void CorruptFile_IsSetAside_AndDefaultsUsed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");

        var store = new SettingsStore(FilePath);

        Assert.True(store.Current.General.Enabled);
        Assert.True(File.Exists(FilePath + ".bad"));
        Assert.Equal("{ this is not json", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void HandEditedFile_WithCommentsAndMissingSections_Loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            {
              // user turned grammar off
              "writing": { "grammar": false, },
              "general": { "analysisDelayMs": 9999 }
            }
            """);

        var c = new SettingsStore(FilePath).Current;

        Assert.False(c.Writing.Grammar);
        Assert.True(c.Writing.Spelling);
        Assert.Equal(GeneralSettings.MaxDelayMs, c.General.AnalysisDelayMs);
        Assert.Equal("Ctrl+Alt+.", c.General.Hotkey);
        Assert.True(c.General.HoverSuggestions); // settings added later default on in older files
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public class SettingsEffectTests
{
    private sealed class NoDictionary : Redline.Core.Interfaces.IPersonalDictionary
    {
        public event Action? Changed { add { } remove { } }
        public bool Contains(string word) => false;
        public void Add(string word) { }
        public bool Remove(string word) => false;
        public IReadOnlyCollection<string> Words => [];
    }

    private static Redline.Core.Models.TextIssue Issue(Redline.Core.Models.IssueCategory c, int at) => new()
    {
        StartOffset = at, Length = 1, OriginalText = "x", Category = c, Message = "m",
    };

    [Fact]
    public void WritingSettings_FilterCategories_AndRepublish()
    {
        var cache = new Redline.Core.Pipeline.IssueCacheManager(new NoDictionary(), new Redline.Core.Corrections.IgnoreList(null));
        int published = 0;
        cache.IssuesChanged += (_, _) => published++;
        cache.Update("s", Redline.Core.Pipeline.IssueSet.From(
        [
            Issue(Redline.Core.Models.IssueCategory.Spelling, 0),
            Issue(Redline.Core.Models.IssueCategory.Grammar, 2),
            Issue(Redline.Core.Models.IssueCategory.Punctuation, 4),
            Issue(Redline.Core.Models.IssueCategory.Style, 6),
        ], 1));

        // Defaults: style off.
        cache.SetWriting(new WritingSettings());
        Assert.Equal(3, cache.Get("s")!.Issues.Count);

        cache.SetWriting(new WritingSettings { Grammar = false, StyleSuggestions = true });
        Assert.Equal([Redline.Core.Models.IssueCategory.Spelling, Redline.Core.Models.IssueCategory.Style],
            cache.Get("s")!.Issues.Select(i => i.Category));

        Assert.True(published >= 3);
    }
}

public class AppExclusionTests
{
    [Theory]
    [InlineData("WINWORD.EXE", "Microsoft Word", "Microsoft Word")]
    [InlineData("chrome.exe", "Google Chrome", "Google Chrome")]
    [InlineData("Notepad.exe", "Notepad.exe", "Notepad")]     // description is just the file name
    [InlineData("ms-teams.exe", null, "ms-teams")]              // no access to the exe (elevated)
    [InlineData("app.exe", "   ", "app")]
    public void DisplayName_PrefersTheFileDescription(string process, string? description, string expected) =>
        Assert.Equal(expected, AppExclusion.DisplayName(process, description));

    [Fact]
    public void Exclude_AddsOnce_AndIsExcludedMatchesAnyCase()
    {
        var s = new RedlineSettings().Validated();
        Assert.False(AppExclusion.IsExcluded(s, "WINWORD.EXE"));

        s = AppExclusion.Exclude(s, "WINWORD.EXE").Validated();
        s = AppExclusion.Exclude(s, "winword.exe").Validated();
        Assert.Equal(["WINWORD.EXE"], s.Applications.Excluded);
        Assert.True(AppExclusion.IsExcluded(s, "winword"));
    }
}
