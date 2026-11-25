using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using MainApp = FileAssistant.App.App;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private async Task CheckBranding(FrameworkElement root, WorkspacePage workspace)
    {
        var restarting = extraMode == "branding-reopen";
        Require(Preferences.Current.AppIcon == (restarting ? "amber" : "cobalt"), "Unexpected startup icon.");
        typeof(MainApp).GetMethod("InstallDesktopShell", InstancePrivate)!.Invoke(this, null);
        var shell = (DesktopShell)typeof(MainApp).GetField("shell", InstancePrivate)!.GetValue(this)!;
        var drop = (IDisposable)typeof(MainApp).GetField("trayDrop", InstancePrivate)!.GetValue(this)!;
        var windowImage = (Image)workspace.FindName("TitleBarAppIcon");
        try
        {
            await CheckImage(windowImage, Preferences.Current.AppIcon);
            VerifyIcons(shell, Preferences.Current.AppIcon);
            var opening = workspace.OpenSettingsAsync(initialPage: "appearance");
            var dialog = await Opened(workspace, opening);
            if (restarting)
            {
                Require(Preferences.Current.Language == InterfaceLanguage.English && Window.Title == "FileKite" && shell.Title == "FileKite", "Language did not survive restart.");
                Require(WithId<ToggleButton>(dialog, "Settings.AppIcon.amber").IsChecked == true, "Selection did not survive restart.");
                await Navigate(dialog, "about");
                await CheckImage(WithId<Image>(dialog, "Settings.About.Icon"), "amber");
                Require(WithId<TextBlock>(dialog, "Settings.About.Brand").Text == "FileKite", "About did not restore FileKite.");
                dialog.Hide(); await opening;
                evidence.Add(new { restored = "amber", window = Window.Title, tray = shell.Title });
                return;
            }
            foreach (var (language, option, brand, shortName, quit) in new[]
            {
                (InterfaceLanguage.SimplifiedChinese, "简体中文", "纸鸢 · FileKite", "纸鸢", "彻底退出纸鸢"),
                (InterfaceLanguage.TraditionalChinese, "繁體中文", "紙鳶 · FileKite", "紙鳶", "徹底退出紙鳶"),
                (InterfaceLanguage.English, "English", "FileKite", "FileKite", "Quit FileKite")
            })
            {
                await Navigate(dialog, "general");
                var languageMenu = Elements(dialog).OfType<SelectionDropDown>().Single(d =>
                    d.ItemsSource is IEnumerable<SelectionOption> choices && choices.Any(o => o.Text == "English"));
                if (Preferences.Current.Language != language) await PickOption(languageMenu, option);
                await Until(() => Preferences.Current.Language == language && Window.Title == brand && shell.Title == brand, "Live branding failed: " + option);
                Require(shell.MenuLabel(TrayCommand.Exit) == quit, "Tray exit label differs: " + shell.MenuLabel(TrayCommand.Exit));
                Require(((TextBlock)workspace.FindName("TitleBarBrandText")).Text == shortName, "Title bar name differs: " + option);
                var subtitle = (TextBlock)workspace.FindName("TitleBarBrandSubtitle");
                Require((subtitle.Visibility == Visibility.Visible) == (language != InterfaceLanguage.English), "Title bar subtitle duplicated the English name.");
                var search = Part<TextBox>(dialog, "SettingsSearch");
                search.Text = language == InterfaceLanguage.English ? "taskbar" : language == InterfaceLanguage.TraditionalChinese ? "圖示" : "纸鸢";
                await Settle(dialog);
                Require(WithId<ToggleButton>(dialog, "Settings.AppIcon.cobalt").IsLoaded, "Cross-category search did not find icons.");
                await Navigate(dialog, "appearance");
                foreach (var choice in AppIconCatalog.Choices)
                {
                    var button = WithId<ToggleButton>(dialog, "Settings.AppIcon." + choice.Id);
                    button.StartBringIntoView();
                    await Settle(dialog);
                    var labels = Elements(button).OfType<TextBlock>().Select(t => t.Text).ToArray();
                    var chinese = language == InterfaceLanguage.TraditionalChinese ? choice.Id switch
                    {
                        "cobalt" => "原生鈷藍", "charcoal" => "炭墨點朱", "pine" => "松針墨綠",
                        "vermilion" => "丹朱赤紅", "amber" => "藍珀雙面", _ => throw new InvalidOperationException(choice.Id)
                    } : choice.Chinese;
                    Require(labels.Contains(chinese) && labels.Contains(choice.English), "Missing bilingual icon label: " + choice.Id);
                    var preview = Elements(button).OfType<Image>().Single();
                    Require(preview.Width == 44 && preview.Height == 44, "Wrong preview size.");
                    await CheckImage(preview, choice.Id);
                    InvokeButton(button);
                    await Until(() => Preferences.Current.AppIcon == choice.Id, "Button did not persist " + choice.Id);
                    await CheckImage(windowImage, choice.Id);
                    await CheckImage(Part<Image>(dialog, "RailBrandIcon"), choice.Id);
                    var hashes = VerifyIcons(shell, choice.Id);
                    Require(AppIconCatalog.Choices.Count(c => WithId<ToggleButton>(dialog, "Settings.AppIcon." + c.Id).IsChecked == true) == 1
                        && button.IsChecked == true, "Wrong selection indicator.");
                    await Navigate(dialog, "about");
                    Require(WithId<TextBlock>(dialog, "Settings.About.Brand").Text == brand, "About title differs.");
                    await CheckImage(WithId<Image>(dialog, "Settings.About.Icon"), choice.Id);
                    evidence.Add(new { language = language.ToString(), choice.Id, brand, quit, chinese, choice.English, hashes });
                    await Navigate(dialog, "appearance");
                }
                WithId<ToggleButton>(dialog, "Settings.AppIcon.amber").StartBringIntoView();
                await Settle(dialog);
                await SavePng(Part<Grid>(dialog, "DialogLayout"), "icons-" + language);
            }
            await ResizeWindow(root, 565, 720);
            await Preferences.UpdateAsync(s => s with { TopNavigation = true });
            var compactName = (TextBlock)workspace.FindName("TitleBarCompactBrand");
            await Until(() => compactName.Visibility == Visibility.Visible && compactName.Text == "FileKite", "Small integrated title bar lost its brand.");
            await Navigate(dialog, "appearance");
            var layout = Part<Grid>(dialog, "DialogLayout");
            var tiles = AppIconCatalog.Choices.Select(c => Bounds(WithId<ToggleButton>(dialog, "Settings.AppIcon." + c.Id), layout)).ToArray();
            Require(tiles.Select(t => t.Y).Distinct().Count() >= 2 && tiles.All(t => t.Width >= 44 && t.X >= 0 &&
                t.X + t.Width <= layout.ActualWidth), "Narrow icon gallery was cropped.");
            WithId<ToggleButton>(dialog, "Settings.AppIcon.amber").StartBringIntoView();
            await Settle(dialog); await SavePng(layout, "icons-compact");
            evidence.Add(new { compactGalleryWidth = layout.ActualWidth, compactBrand = compactName.Text, tiles });
            await Preferences.UpdateAsync(s => s with { TopNavigation = false });
            await Navigate(dialog, "about");
            await Preferences.UpdateAsync(s => s with { AppIcon = "pine" });
            await CheckImage(WithId<Image>(dialog, "Settings.About.Icon"), "pine");
            var savedTray = TrayHandle(shell);
            IconSend(WindowHandle, RegisterWindowMessage("TaskbarCreated"), 0, 0);
            Require(TrayHandle(shell) == savedTray, "Explorer recreation lost the selected icon.");
            VerifyIcons(shell, "pine");
            dialog.Hide(); await opening;
            await CycleIcons(10);
            var before = await CountGui(root);
            await CycleIcons(50);
            var halfway = await CountGui(root);
            await CycleIcons(50);
            var after = await CountGui(root);
            evidence.Add(new { repeatedChanges = 100, before, halfway, after, trayRegisteredOnPrivateDesktop = shell.TrayAvailable });
            Log($"Native handles at 0/50/100 switches: USER {before.User}/{halfway.User}/{after.User}, GDI {before.Gdi}/{halfway.Gdi}/{after.Gdi}");
            Require(Math.Max(halfway.User, after.User) <= before.User + 3 && Math.Max(halfway.Gdi, after.Gdi) <= before.Gdi + 3,
                "Native handles grew after repeated switches.");
            Require(Preferences.Current.AppIcon == "amber", "Restart fixture lost its final selection.");
        }
        finally { drop.Dispose(); shell.Dispose(); }
    }

    private static async Task CycleIcons(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var id = AppIconCatalog.Choices[i % AppIconCatalog.Choices.Count].Id;
            await Preferences.UpdateAsync(s => s with { AppIcon = id });
        }
    }

    private sealed record GuiCounts(uint User, uint Gdi);
    private static async Task<GuiCounts> CountGui(FrameworkElement root)
    {
        await Settle(root);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Settle(root);
        return new(GetGuiResources(-1, 1), GetGuiResources(-1, 0));
    }

    private static async Task CheckImage(Image image, string id) => await Until(
        () => image.Source is BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } bitmap
            && bitmap.UriSource.AbsolutePath.EndsWith("/" + id + ".png", StringComparison.Ordinal),
        $"Native image {image.Name} did not decode {id} ({(image.Source as BitmapImage)?.UriSource}).");

    private static nint TrayHandle(DesktopShell shell) => (nint)typeof(DesktopShell).GetField("icon", InstancePrivate)!.GetValue(shell)!;

    private static string[] VerifyIcons(DesktopShell shell, string id)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcons", id + ".ico");
        var dpi = GetDpiForWindow(WindowHandle);
        var results = new List<string>();
        foreach (var (handle, small) in new[] { (IconSend(WindowHandle, 0x7F, 0, 0), true),
            (IconSend(WindowHandle, 0x7F, 1, 0), false), (TrayHandle(shell), true) })
        {
            var expected = LoadImage(0, source, 1, GetSystemMetricsForDpi(small ? 49 : 11, dpi), GetSystemMetricsForDpi(small ? 50 : 12, dpi), 0x10);
            Require(expected != 0 && handle != 0, "Missing native icon: " + id);
            try
            {
                var actual = PixelHash(handle);
                Require(actual == PixelHash(expected), "Native pixels differ from " + source);
                results.Add(actual);
            }
            finally { DestroyIcon(expected); }
        }
        return results.ToArray();
    }

    private static string PixelHash(nint icon)
    {
        Require(GetIconInfo(icon, out var info), "HICON was already released.");
        try
        {
            Require(GetObject(info.Color, Marshal.SizeOf<NativeBitmap>(), out var bitmap) != 0, "Invalid color bitmap.");
            var bytes = new byte[checked(bitmap.Height * bitmap.Stride)];
            Require(GetBitmapBits(info.Color, bytes.Length, bytes) == bytes.Length, "Incomplete HICON pixel data.");
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            if (info.Mask != 0) DeleteObject(info.Mask);
            if (info.Color != 0) DeleteObject(info.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeIcon { public int IsIcon; public uint X, Y; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, Stride; public ushort Planes, Bpp; public nint Bits; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint IconSend(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string path, uint kind, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out NativeIcon info);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int metric, uint dpi);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(nint obj, int size, out NativeBitmap bmp);
    [DllImport("gdi32.dll")] private static extern int GetBitmapBits(nint bitmap, int size, [Out] byte[] bytes);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
