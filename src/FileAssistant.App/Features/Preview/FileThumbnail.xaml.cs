using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using FileAssistant.App.Controls;
using FileAssistant.Core.Features.Search;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace FileAssistant.App.Features.Preview;

/// <summary>Loads only realized file tiles with subtle breathing skeleton shimmer and fade-in.</summary>
public sealed partial class FileThumbnail : UserControl
{
    private CancellationTokenSource? loading;
    private DispatcherQueueTimer? shimmerTimer;
    private long shimmerStartTicks;

    public static readonly DependencyProperty FileProperty = DependencyProperty.Register(nameof(File), typeof(SearchFile), typeof(FileThumbnail),
        new PropertyMetadata(null, (owner, _) => ((FileThumbnail)owner).Refresh()));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(FileThumbnail),
        new PropertyMetadata("\uE8A5", (owner, args) => ((FileThumbnail)owner).FallbackIcon.Glyph = (string)args.NewValue));
    public SearchFile? File { get => (SearchFile?)GetValue(FileProperty); set => SetValue(FileProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public FileThumbnail()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { StopShimmer(); loading?.Cancel(); ThumbnailImage.Source = null; };
    }

    private void StartShimmer()
    {
        SkeletonOverlay.Visibility = Visibility.Visible;
        shimmerStartTicks = Stopwatch.GetTimestamp();
        if (shimmerTimer is null && DispatcherQueue is { } q)
        {
            shimmerTimer = q.CreateTimer();
            shimmerTimer.Interval = TimeSpan.FromMilliseconds(32);
            shimmerTimer.IsRepeating = true;
            shimmerTimer.Tick += (_, _) =>
            {
                var sec = Stopwatch.GetElapsedTime(shimmerStartTicks).TotalSeconds;
                SkeletonOverlay.Opacity = 0.22 + 0.28 * (0.5 + 0.5 * Math.Sin(sec * 5.2));
            };
        }
        shimmerTimer?.Start();
    }

    private void StopShimmer()
    {
        shimmerTimer?.Stop();
        if (SkeletonOverlay is not null)
        {
            SkeletonOverlay.Visibility = Visibility.Collapsed;
            SkeletonOverlay.Opacity = 0;
        }
    }

    private async void Refresh()
    {
        loading?.Cancel();
        loading = null;
        StopShimmer();
        if (!IsLoaded) return;
        ThumbnailImage.Source = null;
        FallbackIcon.Visibility = Visibility.Visible;
        if (File is null || File.IsFolder) return;
        using var request = new CancellationTokenSource();
        loading = request;
        StartShimmer();
        try
        {
            var preview = await App.FileProcessing.PreviewAsync(File.FullPath, 300, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (preview.Png is not { } png) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            request.Token.ThrowIfCancellationRequested();
            StopShimmer();
            ThumbnailImage.Source = bitmap;
            FallbackIcon.Visibility = Visibility.Collapsed;
            WorkspaceMotion.AnimateFadeIn(ThumbnailImage, 220);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (ReferenceEquals(loading, request))
            {
                loading = null;
                StopShimmer();
            }
        }
    }
}
