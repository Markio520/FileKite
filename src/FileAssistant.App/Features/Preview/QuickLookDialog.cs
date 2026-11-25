using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.App.Resources;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace FileAssistant.App.Features.Preview;

/// <summary>
/// Spacebar / Button QuickLook modal with responsive viewport sizing, Left/Right navigation,
/// crash-safe mounted MediaPlayerElement lifecycle, backdrop click-to-close, and before/after quality comparison.
/// </summary>
internal static class QuickLookDialog
{
    private static bool open;

    private static bool IsPreviewShortcut(VirtualKey key)
    {
        if (!ShortcutChord.TryParse(App.Preferences.Current.Shortcuts.QuickLook, out var binding) || binding.Key != (uint)key) return false;
        static bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var modifiers = (Down(VirtualKey.Menu) ? 1u : 0) | (Down(VirtualKey.Control) ? 2u : 0) |
            (Down(VirtualKey.Shift) ? 4u : 0) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? 8u : 0);
        return binding.Modifiers == modifiers;
    }

    public static async Task ShowAsync(FrameworkElement owner, IReadOnlyList<SearchFile> files, int initialIndex = 0, bool compare = false,
        (ConversionTarget Target, int Quality)? conversion = null)
    {
        if (open || owner.XamlRoot is null || files.Count == 0) return;
        open = true;
        var xamlRoot = owner.XamlRoot;
        var activeTheme = WorkspaceMaterials.IsActiveLightTheme(App.Preferences.Current) ? ElementTheme.Light : ElementTheme.Dark;
        var index = Math.Clamp(initialIndex, 0, files.Count - 1);
        var dialogOpen = false;
        CancellationTokenSource? cts = null;
        Windows.Media.Playback.MediaPlayer? player = null;
        Windows.Media.Core.MediaSource? mediaSource = null;
        MediaPlayerElement? activeMediaElement = null;
        Border? mediaHost = null;
        TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>? rootChanged = null;

        // Always detach MediaPlayerElement BEFORE disposing MediaPlayer so WinUI 3's MediaTransportControls
        // never calls into a disposed WinRT MediaPlayer during popup teardown (0x80004004 / CoreMessagingXP 0xc000027b).
        void CleanupMedia()
        {
            var p = player;
            var src = mediaSource;
            var el = activeMediaElement;
            player = null;
            mediaSource = null;
            activeMediaElement = null;
            try { p?.Pause(); } catch { }
            try { el?.SetMediaPlayer(null); } catch { }
            if (mediaHost is not null)
            {
                mediaHost.Child = null;
                mediaHost.Visibility = Visibility.Collapsed;
            }
            if (p is not null)
            {
                try { p.Source = null; p.Dispose(); } catch { }
            }
            try { src?.Dispose(); } catch { }
        }

        try
        {
            var titleText = new TextBlock
            {
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            var metaText = new TextBlock
            {
                FontSize = 12,
                Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            var counterBadge = new TextBlock
            {
                FontSize = 11.5,
                Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceSecondaryBrush"),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var prevBtn = new Button
            {
                Content = InterfaceText.Get("‹ 上一项", "‹ Previous"),
                Style = (Style)Application.Current.Resources["WorkspaceToolbarButtonStyle"],
                MinHeight = 30,
                Padding = new Thickness(10, 4, 10, 4),
                Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed
            };
            var nextBtn = new Button
            {
                Content = InterfaceText.Get("下一项 ›", "Next ›"),
                Style = (Style)Application.Current.Resources["WorkspaceToolbarButtonStyle"],
                MinHeight = 30,
                Padding = new Thickness(10, 4, 10, 4),
                Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed
            };
            var closeLabel = InterfaceText.Get("关闭预览（Esc）", "Close preview (Esc)");
            var closeBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 13 },
                Style = (Style)Application.Current.Resources["WorkspaceIconButtonStyle"],
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Top
            };
            AutomationProperties.SetName(closeBtn, closeLabel);
            ToolTipService.SetToolTip(closeBtn, closeLabel);

            WorkspaceMotion.AttachTactilePress(prevBtn);
            WorkspaceMotion.AttachTactilePress(nextBtn);
            WorkspaceMotion.AttachTactilePress(closeBtn);

            var baseImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var compareOverlayImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.94 };
            var compareClip = new RectangleGeometry();
            compareOverlayImage.Clip = compareClip;

            var curtainLine = new Border
            {
                Width = 2,
                Background = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceAccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            var imageStage = new Grid
            {
                MinHeight = 280,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            imageStage.Children.Add(baseImage);
            imageStage.Children.Add(compareOverlayImage);
            imageStage.Children.Add(curtainLine);

            mediaHost = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var textScroll = new ScrollViewer
            {
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(14),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var textBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            textScroll.Content = textBlock;

            var statusText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceSecondaryBrush"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460
            };
            var ring = new ProgressRing { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var compareBar = new Border
            {
                Background = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceCardBrush"),
                BorderBrush = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 8, 12, 8),
                Visibility = Visibility.Collapsed
            };
            var leftLabel = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceTextBrush") };
            var rightLabel = new TextBlock { FontSize = 12, Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceSuccessBrush"), HorizontalAlignment = HorizontalAlignment.Right };
            var compareSlider = new Slider { Minimum = 5, Maximum = 95, Value = 50, StepFrequency = 1 };

            var compareLabelsGrid = new Grid();
            compareLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            compareLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            compareLabelsGrid.Children.Add(leftLabel);
            Grid.SetColumn(rightLabel, 1);
            compareLabelsGrid.Children.Add(rightLabel);

            var compareStack = new StackPanel { Spacing = 4 };
            compareStack.Children.Add(compareLabelsGrid);
            compareStack.Children.Add(compareSlider);
            compareBar.Child = compareStack;

            void UpdateCurtain()
            {
                if (compareBar.Visibility != Visibility.Visible || imageStage.ActualWidth <= 0 || imageStage.ActualHeight <= 0) return;
                var ratio = compareSlider.Value / 100.0;
                var splitX = imageStage.ActualWidth * ratio;
                compareClip.Rect = new Rect(0, 0, splitX, imageStage.ActualHeight);
                curtainLine.Margin = new Thickness(splitX, 0, 0, 0);
            }

            compareSlider.ValueChanged += (_, _) => UpdateCurtain();
            imageStage.SizeChanged += (_, _) => UpdateCurtain();

            var viewportBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceSubtleBrush"),
                BorderBrush = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceBorderBrush"),
                BorderThickness = new Thickness(1),
                MinHeight = 320,
                Padding = new Thickness(12)
            };
            var stageGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            stageGrid.Children.Add(imageStage);
            stageGrid.Children.Add(mediaHost);
            stageGrid.Children.Add(textScroll);
            stageGrid.Children.Add(statusText);
            stageGrid.Children.Add(ring);
            viewportBorder.Child = stageGrid;

            var headerGrid = new Grid { ColumnSpacing = 12 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(titleText);
            titleStack.Children.Add(metaText);
            headerGrid.Children.Add(titleStack);

            var navStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            navStack.Children.Add(counterBadge);
            navStack.Children.Add(prevBtn);
            navStack.Children.Add(nextBtn);
            Grid.SetColumn(navStack, 1);
            headerGrid.Children.Add(navStack);

            Grid.SetColumn(closeBtn, 2);
            headerGrid.Children.Add(closeBtn);

            var contentGrid = new Grid { RowSpacing = 12, Padding = new Thickness(20, 16, 20, 18), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.Children.Add(headerGrid);
            Grid.SetRow(viewportBorder, 1);
            contentGrid.Children.Add(viewportBorder);
            Grid.SetRow(compareBar, 2);
            contentGrid.Children.Add(compareBar);

            var rootGrid = new Grid
            {
                Name = "QuickLookShell",
                Background = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceDialogBrush"),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            rootGrid.Children.Add(contentGrid);

            double? customWidth = null, customHeight = null;
            double currentStageMaxHeight = 420;
            void ResizeDialog()
            {
                var size = xamlRoot.Size;
                var maxW = Math.Max(360, size.Width - 28);
                var maxH = Math.Max(300, size.Height - 28);
                var w = customWidth.HasValue ? Math.Clamp(customWidth.Value, Math.Min(480, maxW), maxW) : Math.Clamp(size.Width - 96, 320, 860);
                var h = customHeight.HasValue ? Math.Clamp(customHeight.Value, Math.Min(360, maxH), maxH) : Math.Clamp(size.Height - 96, 360, 660);
                rootGrid.Width = w;
                rootGrid.Height = h;
                currentStageMaxHeight = Math.Max(180, h - (compareBar.Visibility == Visibility.Visible ? 176 : 112));
                baseImage.MaxHeight = currentStageMaxHeight;
                compareOverlayImage.MaxHeight = currentStageMaxHeight;
                imageStage.MaxHeight = currentStageMaxHeight + 8;
                if (activeMediaElement is not null) activeMediaElement.MaxHeight = currentStageMaxHeight;
                textScroll.MaxHeight = currentStageMaxHeight;
                viewportBorder.MinHeight = Math.Min(260, currentStageMaxHeight);
            }
            ResizeDialog();

            var dialog = new ContentDialog
            {
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                XamlRoot = xamlRoot,
                RequestedTheme = activeTheme,
                Background = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceDialogBrush"),
                Foreground = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceTextBrush"),
                BorderBrush = WorkspaceMaterials.ThemeBrush(activeTheme, "WorkspaceBorderBrush"),
                CornerRadius = new CornerRadius(12),
                Content = rootGrid
            };
            dialog.Resources["ContentDialogMinWidth"] = 320d;
            dialog.Resources["ContentDialogMinHeight"] = 0d;
            dialog.Resources["ContentDialogPadding"] = new Thickness(0);
            dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0);
            dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
            dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            DialogDismiss.Attach(dialog);
            ResizableDialogShell.Attach(
                dialog,
                rootGrid,
                minWidth: 480,
                minHeight: 360,
                onResized: (w, h) => { customWidth = w; customHeight = h; ResizeDialog(); },
                onReset: () => { customWidth = null; customHeight = null; ResizeDialog(); });

            rootChanged = (_, _) => ResizeDialog();
            xamlRoot.Changed += rootChanged;

            void UpdateHeaderMeta()
            {
                var file = files[index];
                titleText.Text = file.Name;
                ToolTipService.SetToolTip(titleText, file.Name);
                var sizeStr = file.IsFolder
                    ? InterfaceText.Get("文件夹", "Folder")
                    : (file.Size is { } s ? WorkspaceFile.FormatSize(s) : InterfaceText.Get("大小未知", "Unknown size"));
                metaText.Text = $"{sizeStr}  ·  {file.FullPath}";
                ToolTipService.SetToolTip(metaText, file.FullPath);
                counterBadge.Text = files.Count > 1 ? $"{index + 1} / {files.Count}" : string.Empty;
                prevBtn.IsEnabled = index > 0;
                nextBtn.IsEnabled = index < files.Count - 1;
            }
            UpdateHeaderMeta();

            async Task LoadCurrentAsync()
            {
                cts?.Cancel();
                cts?.Dispose();
                CleanupMedia();
                imageStage.Visibility = Visibility.Collapsed;
                textScroll.Visibility = Visibility.Collapsed;
                compareBar.Visibility = Visibility.Collapsed;
                curtainLine.Visibility = Visibility.Collapsed;

                if (!dialogOpen) return;

                cts = new CancellationTokenSource();
                var token = cts.Token;
                var file = files[index];
                UpdateHeaderMeta();

                baseImage.Source = null;
                compareOverlayImage.Source = null;
                statusText.Text = string.Empty;

                if (file.IsFolder)
                {
                    ring.IsActive = false;
                    statusText.Text = InterfaceText.Get("文件夹不支持快速图像预览，可双击进入查看内容。", "Double-click a folder to browse its contents.");
                    return;
                }

                ring.IsActive = true;
                try
                {
                    if (Features.Search.SearchResultItem.Category(file) is 4 or 5)
                    {
                        if (!File.Exists(file.FullPath)) throw new FileNotFoundException(InterfaceText.Get("文件已移动或删除。", "The file was moved or deleted."), file.FullPath);
                        // Ensure the ContentDialog visual tree has completed its initial layout pass before mounting MediaPlayerElement
                        await Task.Yield();
                        if (!dialogOpen || token.IsCancellationRequested) return;

                        var el = new MediaPlayerElement
                        {
                            AreTransportControlsEnabled = true,
                            AutoPlay = false,
                            Stretch = Stretch.Uniform,
                            MaxHeight = currentStageMaxHeight,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        if (el.TransportControls is { } tc)
                        {
                            tc.IsCompact = true;
                            tc.IsZoomButtonVisible = false;
                        }

                        activeMediaElement = el;
                        mediaHost.Child = el;
                        mediaHost.Visibility = Visibility.Visible;
                        mediaHost.UpdateLayout();

                        var playback = App.Preferences.Current.Playback;
                        mediaSource = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(file.FullPath));
                        var mp = new Windows.Media.Playback.MediaPlayer { AutoPlay = false, IsMuted = playback == MediaPlayback.Muted };
                        player = mp;
                        mp.MediaFailed += (_, failure) => owner.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (dialogOpen && !token.IsCancellationRequested)
                            {
                                CleanupMedia();
                                statusText.Text = failure.ErrorMessage;
                            }
                        });
                        el.SetMediaPlayer(mp);
                        mp.Source = mediaSource;
                        if (playback != MediaPlayback.Paused) mp.Play();
                        return;
                    }

                    var result = await App.FileProcessing.PreviewAsync(file.FullPath, 960, token);
                    token.ThrowIfCancellationRequested();
                    if (!dialogOpen) return;

                    if (result.Png is { } png)
                    {
                        using var stream = new InMemoryRandomAccessStream();
                        await stream.WriteAsync(png.AsBuffer());
                        stream.Seek(0);
                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(stream);
                        token.ThrowIfCancellationRequested();
                        if (!dialogOpen) return;

                        baseImage.Source = bmp;
                        imageStage.Visibility = Visibility.Visible;
                        WorkspaceMotion.AnimateFadeIn(imageStage, 180);

                        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                        if (compare && ext is (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tiff" or ".tif" or ".webp" or ".avif"))
                        {
                            var (format, quality) = conversion ?? (ConversionTarget.WebP, App.Preferences.Current.ImageQuality);
                            var sample = await Infrastructure.Engines.PreviewComparison.EncodeAsync(png, format, quality, token);
                            using var afterStream = new InMemoryRandomAccessStream();
                            await afterStream.WriteAsync(sample.DecodedPng.AsBuffer());
                            afterStream.Seek(0);
                            var after = new BitmapImage();
                            await after.SetSourceAsync(afterStream);
                            token.ThrowIfCancellationRequested();
                            if (!dialogOpen) return;

                            compareOverlayImage.Opacity = 1;
                            compareOverlayImage.Source = after;
                            var formatLabel = format == ConversionTarget.WebP ? "WebP" : format.ToString().ToUpperInvariant();
                            leftLabel.Text = $"{formatLabel} Q{quality} · {WorkspaceFile.FormatSize(sample.CompressedBytes)}";
                            rightLabel.Text = $"{InterfaceText.Get("原图预览", "Original preview")} · {sample.Width} × {sample.Height}";
                            compareBar.Visibility = Visibility.Visible;
                            curtainLine.Visibility = Visibility.Visible;
                            owner.DispatcherQueue.TryEnqueue(UpdateCurtain);
                        }
                    }
                    else if (result.Kind == PreviewKind.Text)
                    {
                        textBlock.Text = result.Text;
                        textScroll.Visibility = Visibility.Visible;
                        WorkspaceMotion.AnimateFadeIn(textScroll, 180);
                    }
                    else
                    {
                        statusText.Text = string.IsNullOrEmpty(result.Message)
                            ? InterfaceText.Get("此格式暂不支持内联渲染预览。", "An inline preview is unavailable for this format.")
                            : result.Message;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (dialogOpen && !token.IsCancellationRequested) statusText.Text = ex.Message; }
                finally { if (!token.IsCancellationRequested) ring.IsActive = false; }
            }

            prevBtn.Click += async (_, _) => { if (dialogOpen && index > 0) { index--; await LoadCurrentAsync(); } };
            nextBtn.Click += async (_, _) => { if (dialogOpen && index < files.Count - 1) { index++; await LoadCurrentAsync(); } };
            closeBtn.Click += (_, _) => { if (dialogOpen) dialog.Hide(); };

            dialog.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Escape && dialogOpen)
                {
                    e.Handled = true;
                    dialog.Hide();
                }
            };

            rootGrid.KeyDown += async (_, e) =>
            {
                if (!dialogOpen) return;
                if (IsPreviewShortcut(e.OriginalKey)) { e.Handled = true; dialog.Hide(); }
                else if (e.Key == VirtualKey.Left && index > 0) { e.Handled = true; index--; await LoadCurrentAsync(); }
                else if (e.Key == VirtualKey.Right && index < files.Count - 1) { e.Handled = true; index++; await LoadCurrentAsync(); }
            };

            dialog.Opened += (_, _) =>
            {
                dialogOpen = true;
                _ = LoadCurrentAsync();
            };
            dialog.Closing += (_, _) =>
            {
                dialogOpen = false;
                cts?.Cancel();
                CleanupMedia();
            };
            dialog.Closed += (_, _) =>
            {
                dialogOpen = false;
                CleanupMedia();
            };

            await dialog.ShowAsync();
        }
        catch (Exception error) { App.ReportFailure(error); }
        finally
        {
            dialogOpen = false;
            if (rootChanged is not null) xamlRoot.Changed -= rootChanged;
            cts?.Cancel();
            cts?.Dispose();
            CleanupMedia();
            open = false;
        }
    }
}
