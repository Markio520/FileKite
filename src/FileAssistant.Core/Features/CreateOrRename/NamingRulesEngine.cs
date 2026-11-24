using System.Globalization;
using System.Text.RegularExpressions;

namespace FileAssistant.Core.Features.CreateOrRename;

public static partial class NamingRulesEngine
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) throw new ArgumentException("名称为空、过长或包含 Windows 不支持的字符。");
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem) || DeviceName().IsMatch(stem)) throw new ArgumentException("名称属于 Windows 保留名称，请调整规则。");
    }
    public static string Generate(string name, string parent, DateTimeOffset date, int index, NamingRules rules)
    {
        if (rules.Start < 0 || rules.Step < 1 || rules.Digits is < 1 or > 12 || rules.RemoveAt < 0 || rules.RemoveCount < 0)
            throw new ArgumentException("编号起点须非负、步长至少 1，位数为 1–12；删除位置和长度不能为负数。");
        var changed = rules.Find.Length == 0 ? name : name.Replace(rules.Find, rules.Replacement, StringComparison.Ordinal);
        if (rules.Delete.Length > 0) changed = changed.Replace(rules.Delete, "", StringComparison.Ordinal);
        if (rules.RemoveCount > 0 && rules.RemoveAt > 0)
        {
            // User positions count text elements, not UTF-16 halves of emoji.
            var starts = StringInfo.ParseCombiningCharacters(changed);
            if (rules.RemoveAt <= starts.Length)
            {
                var start = starts[rules.RemoveAt - 1];
                var endIndex = Math.Min((long)starts.Length, (long)rules.RemoveAt - 1 + rules.RemoveCount);
                var end = endIndex == starts.Length ? changed.Length : starts[(int)endIndex];
                changed = changed.Remove(start, end - start);
            }
        }
        var number = checked((long)rules.Start + (long)index * rules.Step).ToString("D" + rules.Digits, CultureInfo.InvariantCulture);
        // Replace tokens in the pattern only; braces in source names remain literal.
        var result = Token().Replace(rules.Pattern, match => match.Value switch
        { "{name}" => changed, "{parent}" => parent, "{date}" => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), "{n}" => number,
            _ => throw new ArgumentException("支持的命名变量为 {name}、{n}、{date}、{parent}。") });
        result = rules.Prefix + result + rules.Suffix;
        return rules.Case switch { NameCase.Lower => result.ToLowerInvariant(), NameCase.Upper => result.ToUpperInvariant(), _ => result };
    }
    public static string MapPath(string path, IReadOnlyList<PathChange> changes)
    {
        // Each change is applied in execution order, including parent moves.
        foreach (var change in changes)
        {
            if (string.Equals(path, change.Before, StringComparison.OrdinalIgnoreCase)) path = change.After;
            else if (change.IsDirectory && path.StartsWith(change.Before.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                path = change.After.TrimEnd('\\', '/') + path[change.Before.TrimEnd('\\', '/').Length..];
        }
        return path;
    }
    [GeneratedRegex("^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DeviceName();
    [GeneratedRegex(@"\{[^{}]*\}")] private static partial Regex Token();
}
