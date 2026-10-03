using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Redline.Analysis.Interop;

// Windows Spell Checking API (spellcheck.h), available since Windows 8.
// Vtable order below must match the header exactly — do not reorder members.

internal enum CorrectiveAction
{
    None = 0,
    GetSuggestions = 1,
    Replace = 2,
    Delete = 3,
}

[ComImport]
[Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
internal class SpellCheckerFactoryClass
{
}

[ComImport]
[Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
    IEnumString SupportedLanguages { get; }

    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

    ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
}

[ComImport]
[Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
    string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }

    IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);

    IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);

    void Add([MarshalAs(UnmanagedType.LPWStr)] string word);

    void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);

    void AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from, [MarshalAs(UnmanagedType.LPWStr)] string to);

    byte GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId);

    IEnumString OptionIds { get; }

    string Id { [return: MarshalAs(UnmanagedType.LPWStr)] get; }

    string LocalizedName { [return: MarshalAs(UnmanagedType.LPWStr)] get; }

    uint add_SpellCheckerChanged(IntPtr handler);

    void remove_SpellCheckerChanged(uint eventCookie);

    IntPtr GetOptionDescription([MarshalAs(UnmanagedType.LPWStr)] string optionId);

    IEnumSpellingError ComprehensiveCheck([MarshalAs(UnmanagedType.LPWStr)] string text);
}

[ComImport]
[Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
    /// <summary>Returns S_OK with an error, or S_FALSE when the enumeration is exhausted.</summary>
    [PreserveSig]
    int Next(out ISpellingError? value);
}

[ComImport]
[Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellingError
{
    uint StartIndex { get; }
    uint Length { get; }
    CorrectiveAction CorrectiveAction { get; }
    string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
}
