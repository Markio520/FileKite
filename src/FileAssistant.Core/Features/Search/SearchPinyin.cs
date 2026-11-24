using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Core.Features.Search;

public static class SearchPinyin
{
    // ICU is supplied by Windows. Transliterate a Han run together so ICU can use its contextual readings.
    private static readonly object Sync = new();
    private readonly record struct Forms(string Full, string Keyboard, string Initials);
    private static readonly Dictionary<string, Forms> HanRuns = new(StringComparer.Ordinal);
    private static readonly Lazy<Transliterator?> HanLatin = new(Open);
    private static readonly Lazy<uint> IcuVersion = new(ReadIcuVersion);

    /// <summary>Identifies the ICU data used for derived search keys. Zero requires live matching.</summary>
    public static uint SearchKeyVersion => IcuVersion.Value;

    /// <summary>Builds the full, keyboard and initial strings used by ASCII filename searches.
    /// Other literal scripts or an unavailable reading retain the regular per-name matcher.</summary>
    public static bool TryGetSearchKeys(string name, out (string Full, string Keyboard, string Initials) keys)
    {
        keys = ("", "", "");
        StringBuilder? full = null, keyboard = null, initials = null;
        int unchanged = 0;
        for (int i = 0; i < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, i, out var rune)) return false;
            if (!IsHan(rune))
            {
                // Punctuation and emoji remain literal separators. A cased letter
                // from another script can have a non-obvious OrdinalIgnoreCase fold.
                if (!rune.IsAscii && Rune.IsLetterOrDigit(rune)) return false;
                i += rune.Utf16SequenceLength;
                continue;
            }
            int start = i;
            do { i += rune.Utf16SequenceLength; }
            while (i < name.Length && Rune.TryGetRuneAt(name, i, out rune) && IsHan(rune));
            if (Romanize(name.AsSpan(start, i - start)) is not { } forms) return false;
            full ??= new StringBuilder(name.Length * 2);
            keyboard ??= new StringBuilder(name.Length * 2);
            initials ??= new StringBuilder(name.Length);
            full.Append(name, unchanged, start - unchanged).Append(forms.Full);
            keyboard.Append(name, unchanged, start - unchanged).Append(forms.Keyboard);
            initials.Append(name, unchanged, start - unchanged).Append(forms.Initials);
            unchanged = i;
        }
        if (full != null)
            keys = (full.Append(name, unchanged, name.Length - unchanged).ToString(),
                keyboard!.Append(name, unchanged, name.Length - unchanged).ToString(),
                initials!.Append(name, unchanged, name.Length - unchanged).ToString());
        return true;
    }

    private static uint ReadIcuVersion()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var bytes = new byte[4];
            GetVersion(bytes);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    public static bool IsQuery(string text)
    {
        bool letter = false;
        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('ü' or 'Ü')) return false;
            letter |= !char.IsAsciiDigit(c);
        }
        return letter;
    }

    public static bool Matches(string name, string query)
    {
        if (!IsQuery(query)) return false;
        bool keyboard = query.AsSpan().IndexOfAny("vVüÜ") >= 0;
        if (keyboard) query = query.Replace("ü", "v", StringComparison.OrdinalIgnoreCase);
        StringBuilder? full = null, initials = null;
        int unchanged = 0;
        for (var i = 0; i < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, i, out var rune) || !IsHan(rune)) { i++; continue; }
            int start = i;
            do { i += rune.Utf16SequenceLength; }
            while (i < name.Length && Rune.TryGetRuneAt(name, i, out rune) && IsHan(rune));
            var forms = Romanize(name.AsSpan(start, i - start));
            if (forms == null) return false;
            // Most numbered filenames share a Han run. It can match without reconstructing either full filename.
            var spelling = keyboard ? forms.Value.Keyboard : forms.Value.Full;
            if (spelling.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                forms.Value.Initials.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            if (full == null && (start == 0 || !char.IsAsciiLetterOrDigit(name[start - 1])) &&
                (i == name.Length || !char.IsAsciiLetterOrDigit(name[i])))
            {
                unchanged = i;
                continue;
            }
            full ??= new StringBuilder(name.Length * 2);
            initials ??= new StringBuilder(name.Length);
            full.Append(name, unchanged, start - unchanged).Append(spelling);
            initials.Append(name, unchanged, start - unchanged).Append(forms.Value.Initials);
            unchanged = i;
        }
        if (full == null || initials == null) return unchanged > 0 && name.Contains(query, StringComparison.OrdinalIgnoreCase);
        full.Append(name, unchanged, name.Length - unchanged);
        initials.Append(name, unchanged, name.Length - unchanged);
        return full.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || initials.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static Forms? Romanize(ReadOnlySpan<char> run)
    {
        lock (Sync)
        {
            var cache = HanRuns.GetAlternateLookup<ReadOnlySpan<char>>();
            if (cache.TryGetValue(run, out var result)) return result;
            if (HanLatin.Value is not { } converter) return null;
            var buffer = new char[run.Length * 12 + 16];
            run.CopyTo(buffer);
            int length = run.Length, limit = length, error = 0;
            Transform(converter, buffer, ref length, buffer.Length, 0, ref limit, ref error);
            if (error > 0) return null;
            var full = new StringBuilder(length);
            StringBuilder? keyboard = null;
            var initials = new StringBuilder(run.Length);
            bool first = true;
            foreach (char c in buffer.AsSpan(0, length))
            {
                if (char.IsWhiteSpace(c)) { first = true; continue; }
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (c == '\u0308' && full.Length > 0 && full[^1] == 'u')
                    {
                        keyboard ??= new StringBuilder(full.ToString());
                        keyboard[^1] = 'v';
                    }
                    continue;
                }
                full.Append(c);
                keyboard?.Append(c);
                if (first) initials.Append(c);
                first = false;
            }
            var spelling = full.ToString();
            result = new(spelling, keyboard?.ToString() ?? spelling, initials.ToString());
            if (HanRuns.Count >= 4096) HanRuns.Clear();
            cache.TryAdd(run, result);
            return result;
        }
    }

    private static bool IsHan(Rune rune) => rune.Value is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af;

    private static Transliterator? Open()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            int error = 0;
            var converter = OpenNative("Han-Latin; NFD", -1, 0, IntPtr.Zero, 0, IntPtr.Zero, ref error);
            if (error <= 0 && !converter.IsInvalid) return converter;
            converter.Dispose();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    private sealed class Transliterator : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Transliterator() : base(true) { }
        protected override bool ReleaseHandle() { SearchPinyin.Close(handle); return true; }
    }

    [DllImport("icu.dll", EntryPoint = "utrans_openU", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern Transliterator OpenNative(string id, int idLength, int direction, IntPtr rules, int rulesLength, IntPtr parseError, ref int error);
    [DllImport("icu.dll", EntryPoint = "utrans_transUChars", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Transform(Transliterator converter, [In, Out] char[] buffer, ref int length, int capacity, int start, ref int limit, ref int error);
    [DllImport("icu.dll", EntryPoint = "utrans_close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void Close(IntPtr converter);

    [DllImport("icu.dll", EntryPoint = "u_getVersion", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void GetVersion([Out] byte[] version);
}
