using System.Globalization;
using System.Runtime.InteropServices;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Settings;

public static class InterfaceText
{
    public static string Brand => Get("纸鸢 · FileKite", "FileKite");
    public static string WindowTitle =>
#if SHARED_INDEX_TRIAL
        Brand + Get(" · 自建索引测试版", " · Index trial");
#else
        Brand;
#endif

    public static string Get(string simplified, string english) =>
        Get(simplified, english, App.Preferences?.Current.Language ?? InterfaceLanguage.System);

    public static string Get(string simplified, string english, InterfaceLanguage language)
    {
        if (language == InterfaceLanguage.System)
        {
            var culture = CultureInfo.CurrentUICulture;
            language = culture.TwoLetterISOLanguageName == "zh"
                ? culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || culture.Name is "zh-TW" or "zh-HK" or "zh-MO"
                    ? InterfaceLanguage.TraditionalChinese : InterfaceLanguage.SimplifiedChinese
                : InterfaceLanguage.English;
        }
        if (language != InterfaceLanguage.TraditionalChinese) return language == InterfaceLanguage.English ? english : simplified;
        if (simplified.Length == 0) return simplified;
        var length = LCMapStringEx("zh-TW", 0x04000000, simplified, simplified.Length, null, 0, 0, 0, 0);
        if (length <= 0) return simplified;
        var result = new char[length];
        return LCMapStringEx("zh-TW", 0x04000000, simplified, simplified.Length, result, length, 0, 0, 0) > 0
            ? LocalTerms(new string(result)) : simplified;
    }

    private static string LocalTerms(string traditional) => traditional
        .Replace("文件夾", "資料夾").Replace("文件", "檔案").Replace("常規", "一般").Replace("設置", "設定")
        .Replace("快捷鍵", "快速鍵").Replace("熱鍵", "快速鍵").Replace("批處理", "批次處理").Replace("搜索", "搜尋")
        .Replace("存儲", "儲存").Replace("數據", "資料").Replace("界面", "介面").Replace("系統托盤", "系統匣").Replace("托盤", "系統匣")
        .Replace("圖標", "圖示").Replace("任務欄", "工作列").Replace("快捷方式", "捷徑")
        .Replace("窗口", "視窗").Replace("剪貼板", "剪貼簿").Replace("緩存", "快取").Replace("鼠標", "滑鼠")
        .Replace("軟件", "軟體").Replace("視頻", "視訊").Replace("硬件", "硬體").Replace("默認", "預設");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int capacity, nint version, nint reserved, nint sort);
}
