namespace FileAssistant.Core.Settings;

// Modifier bits match RegisterHotKey (Alt=1, Control=2, Shift=4, Windows=8).
public readonly record struct ShortcutChord(uint Modifiers, uint Key)
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 9, ["Backspace"] = 8, ["Delete"] = 0x2E,
        ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, [","] = 0xBC,
        ["."] = 0xBE, ["/"] = 0xBF, [";"] = 0xBA, ["'"] = 0xDE, ["["] = 0xDB, ["]"] = 0xDD,
        ["-"] = 0xBD, ["="] = 0xBB, ["\\"] = 0xDC, ["`"] = 0xC0
    };

    public static bool TryParse(string? text, out ShortcutChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 48) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint mods = 0;
        foreach (var value in parts[..^1])
        {
            uint bit = value.ToUpperInvariant() switch { "ALT" => 1, "CTRL" or "CONTROL" => 2, "SHIFT" => 4, "WIN" or "WINDOWS" => 8, _ => 0 };
            if (bit == 0 || (mods & bit) != 0) return false;
            mods |= bit;
        }
        var last = parts[^1].ToUpperInvariant();
        uint key;
        if (last.Length == 1 && char.IsAsciiLetterOrDigit(last[0])) key = last[0];
        else if (last.StartsWith('F') && int.TryParse(last.AsSpan(1), out var n) && n is >= 1 and <= 24) key = (uint)(0x6F + n);
        else if (!Named.TryGetValue(last, out key)) return false;
        chord = new(mods, key);
        return true;
    }

    public static string? KeyName(uint key)
    {
        if (key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return "F" + (key - 0x6F);
        return Named.FirstOrDefault(item => item.Value == key).Key;
    }
}
