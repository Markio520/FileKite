using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FileAssistant.App.Controls;

/// <summary>
/// App-wide shared rule: clicking on the dimmed backdrop outside any ContentDialog card dismisses it cleanly,
/// while ignoring drags started inside the card and ignoring clicks consumed by transient inner Flyouts/ComboBoxes.
/// </summary>
public static class DialogDismiss
{
    private static readonly ConditionalWeakTable<ContentDialog, DismissState> attachedDialogs = new();
    private static readonly ConditionalWeakTable<XamlRoot, object> registeredRoots = new();
    private static readonly object sentinel = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DialogDismiss),
        new PropertyMetadata(true));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private sealed class DismissState
    {
        public bool PressedOutside;
        public uint PressedPointerId;
        public bool Dismissing;
        public FrameworkElement? CardElement;
        public readonly HashSet<UIElement> HookedElements = [];
        public PointerEventHandler? PressedHandler;
        public PointerEventHandler? ReleasedHandler;
        public PointerEventHandler? CanceledHandler;
    }

    public static ContentDialog Attach(ContentDialog dialog)
    {
        if (attachedDialogs.TryGetValue(dialog, out _)) return dialog;
        var state = new DismissState();
        attachedDialogs.Add(dialog, state);
        dialog.Opened += Dialog_Opened;
        dialog.Closed += Dialog_Closed;
        return dialog;
    }

    /// <summary>
    /// Automatically hooks any ContentDialog that receives focus under this window's XamlRoot,
    /// ensuring even ad-hoc ContentDialog instances obey the shared backdrop-dismiss rule unless opted out.
    /// </summary>
    public static void RegisterWindowRoot(FrameworkElement root)
    {
        void TryHookRoot()
        {
            if (root.XamlRoot is not { } xamlRoot || !registeredRoots.TryAdd(xamlRoot, sentinel)) return;
            FocusManager.GotFocus += (_, args) =>
            {
                for (var cur = args.NewFocusedElement as DependencyObject; cur is not null; cur = VisualTreeHelper.GetParent(cur))
                {
                    if (cur is ContentDialog cd)
                    {
                        if (GetIsEnabled(cd))
                        {
                            Attach(cd);
                            if (attachedDialogs.TryGetValue(cd, out var state))
                                WireTemplateParts(cd, state);
                        }
                        break;
                    }
                }
            };
        }

        if (root.IsLoaded) TryHookRoot();
        root.Loaded += (_, _) => TryHookRoot();
    }

    private static void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (!GetIsEnabled(sender) || !attachedDialogs.TryGetValue(sender, out var state)) return;
        WireTemplateParts(sender, state);
        sender.DispatcherQueue?.TryEnqueue(() =>
        {
            if (sender.IsLoaded && GetIsEnabled(sender))
                WireTemplateParts(sender, state);
        });
    }

    private static void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (!attachedDialogs.TryGetValue(sender, out var state)) return;
        state.PressedOutside = false;
        state.Dismissing = false;
        DetachHandlers(state);
    }

    private static void DetachHandlers(DismissState state)
    {
        foreach (var el in state.HookedElements)
        {
            if (state.PressedHandler is not null) el.RemoveHandler(UIElement.PointerPressedEvent, state.PressedHandler);
            if (state.ReleasedHandler is not null) el.RemoveHandler(UIElement.PointerReleasedEvent, state.ReleasedHandler);
            if (state.CanceledHandler is not null) el.RemoveHandler(UIElement.PointerCanceledEvent, state.CanceledHandler);
        }
        state.HookedElements.Clear();
    }

    private static void WireTemplateParts(ContentDialog dialog, DismissState state)
    {
        ResizableDialogShell.AttachAuto(dialog);
        var card = FindDescendant<FrameworkElement>(dialog, "BackgroundElement") ?? dialog.Content as FrameworkElement;
        if (card is null) return;
        state.CardElement = card;

        state.PressedHandler ??= new PointerEventHandler((_, e) =>
        {
            if (!GetIsEnabled(dialog) || state.CardElement is not { } activeCard)
            {
                state.PressedOutside = false;
                return;
            }
            if (HasOpenTransientPopupAboveDialog(dialog) || IsPointerInside(e, activeCard))
            {
                state.PressedOutside = false;
                return;
            }
            state.PressedOutside = true;
            state.PressedPointerId = e.Pointer.PointerId;
        });

        state.ReleasedHandler ??= new PointerEventHandler((_, e) =>
        {
            if (!state.PressedOutside || state.Dismissing || e.Pointer.PointerId != state.PressedPointerId) return;
            state.PressedOutside = false;
            if (!GetIsEnabled(dialog) || state.CardElement is not { } activeCard) return;
            if (HasOpenTransientPopupAboveDialog(dialog) || IsPointerInside(e, activeCard)) return;
            state.Dismissing = true;
            e.Handled = true;
            dialog.Hide();
            dialog.DispatcherQueue?.TryEnqueue(() => state.Dismissing = false);
        });

        state.CanceledHandler ??= new PointerEventHandler((_, _) => state.PressedOutside = false);

        void Hook(UIElement? target)
        {
            if (target is null || !state.HookedElements.Add(target)) return;
            target.AddHandler(UIElement.PointerPressedEvent, state.PressedHandler, handledEventsToo: true);
            target.AddHandler(UIElement.PointerReleasedEvent, state.ReleasedHandler, handledEventsToo: true);
            target.AddHandler(UIElement.PointerCanceledEvent, state.CanceledHandler, handledEventsToo: true);
        }

        Hook(dialog);
        if (FindDescendant<Panel>(dialog, "LayoutRoot") is { } layoutRoot)
        {
            layoutRoot.Background ??= new SolidColorBrush(Colors.Transparent);
            Hook(layoutRoot);
        }

        // WinUI 3 ContentDialog hosts its dimmed SmokeLayerBackground Rectangle in a separate non-light-dismiss Popup
        // under PopupRoot. Hooking open non-light-dismiss Popup children captures clicks anywhere on the outer smoke layer.
        if (dialog.XamlRoot is { } xamlRoot)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (!popup.IsOpen || popup.Child is null || popup.IsLightDismissEnabled) continue;
                if (popup.Child is ToolTip || FindDescendantOfType<ToolTip>(popup.Child) is not null) continue;
                Hook(popup.Child);
            }
        }
    }

    private static bool IsPointerInside(PointerRoutedEventArgs e, FrameworkElement card)
    {
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return true;
        try
        {
            var rootPt = e.GetCurrentPoint(null).Position;
            var bounds = card.TransformToVisual(null).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            return rootPt.X >= bounds.Left && rootPt.Y >= bounds.Top && rootPt.X <= bounds.Right && rootPt.Y <= bounds.Bottom;
        }
        catch
        {
            var pt = e.GetCurrentPoint(card).Position;
            return pt.X >= 0 && pt.Y >= 0 && pt.X <= card.ActualWidth && pt.Y <= card.ActualHeight;
        }
    }

    private static bool HasOpenTransientPopupAboveDialog(ContentDialog dialog)
    {
        if (dialog.XamlRoot is not { } xamlRoot) return false;
        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
        foreach (var popup in popups)
        {
            if (!popup.IsOpen || popup.Child is null) continue;
            if (ReferenceEquals(popup.Child, dialog)) continue;
            if (popup.Child is ToolTip || FindDescendantOfType<ToolTip>(popup.Child) is not null) continue;
            // Transient Flyouts, MenuFlyouts, ComboBox dropdowns, and DatePickers use IsLightDismissEnabled = true.
            if (popup.IsLightDismissEnabled) return true;
        }
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindDescendantOfType<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendantOfType<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
