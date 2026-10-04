namespace Redline.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,      // values match RegisterHotKey's MOD_* flags
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A global hotkey such as "Ctrl+Alt+." — modifiers plus one key. <see cref="VirtualKey"/> is a
/// Windows virtual-key code. Requires at least one of Ctrl/Alt/Win so it can't swallow ordinary typing.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly (string Name, int Vk)[] NamedKeys =
    [
        (".", 0xBE), (",", 0xBC), (";", 0xBA), ("/", 0xBF), ("'", 0xDE), ("[", 0xDB), ("]", 0xDD),
        ("-", 0xBD), ("=", 0xBB), ("`", 0xC0), ("\\", 0xDC),
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23),
    ];

    public override string ToString()
    {
        // Windows convention: Win first, then Ctrl, Alt, Shift ("Win+Shift+S", "Ctrl+Alt+Del").
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // "Ctrl++" can't be split on '+', so peel the key off the end first.
        var trimmed = text.Trim();
        int split = trimmed.LastIndexOf('+', trimmed.Length - 2 < 0 ? 0 : trimmed.Length - 2);
        if (split <= 0) return false;
        var keyPart = trimmed[(split + 1)..].Trim();
        var modifierParts = trimmed[..split].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var modifiers = HotkeyModifiers.None;
        foreach (var m in modifierParts)
        {
            modifiers |= m.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => (HotkeyModifiers)(-1),
            };
            if ((int)modifiers < 0) return false;
        }

        if ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
            return false; // Shift-only or no modifier would steal normal typing

        int vk = KeyCode(keyPart);
        if (vk == 0) return false;

        hotkey = new Hotkey(modifiers, vk);
        return true;
    }

    private static int KeyCode(string key)
    {
        foreach (var (name, vk) in NamedKeys)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return vk;

        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            return char.ToUpperInvariant(key[0]); // VK_A..Z, VK_0..9 equal their ASCII codes

        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 24)
            return 0x70 + f - 1; // VK_F1..F24
        return 0;
    }

    private static string KeyName(int vk)
    {
        foreach (var (name, code) in NamedKeys)
            if (code == vk) return name;
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        return $"VK{vk:X2}";
    }
}
