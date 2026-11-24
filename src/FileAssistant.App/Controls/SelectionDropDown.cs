using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileAssistant.App.Controls;

/// <summary>Shared single-choice dropdown: fixed button, centered flyout and native selection indicator.</summary>
public class SelectionDropDown : DropDownButton
{
    public static readonly DependencyProperty ButtonContentProperty = DependencyProperty.Register(
        nameof(ButtonContent), typeof(object), typeof(SelectionDropDown), new PropertyMetadata(null, OptionsChanged));
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(SelectionDropDown), new PropertyMetadata(null, OptionsChanged));
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(int), typeof(SelectionDropDown), new PropertyMetadata(0, ValueChanged));

    private ListView? choices;
    private object? displayItems;
    private readonly Flyout menu;
    private FocusState openingFocusState;

    public event EventHandler? SelectionCommitted;

    public object? ButtonContent
    {
        get => GetValue(ButtonContentProperty);
        set => SetValue(ButtonContentProperty, value);
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedValue
    {
        get => (int)GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    public SelectionDropDown()
    {
        var resources = Application.Current.Resources;
        Style = (Style)resources["SelectionDropDownButtonStyle"];
        menu = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = (Style)resources["SelectionDropDownPresenterStyle"]
        };
        Flyout = menu;
        menu.Opening += Menu_Opening;
        menu.Opened += Menu_Opened;
        ActualThemeChanged += (_, _) => { if (choices is not null) choices.RequestedTheme = ActualTheme; };
        Loaded += (_, _) => { WorkspaceLocalization.LanguageChanged += LanguageChanged; if (WorkspaceLocalization.GetOptions(this)) RefreshDisplayOptions(); };
        Unloaded += (_, _) => WorkspaceLocalization.LanguageChanged -= LanguageChanged;
    }

    private void LanguageChanged(object? sender, EventArgs args)
    {
        if (WorkspaceLocalization.GetOptions(this)) RefreshDisplayOptions();
    }

    internal void RefreshDisplayOptions()
    {
        displayItems = WorkspaceLocalization.GetOptions(this) && ItemsSource is IEnumerable<SelectionOption> options
            ? options.Select(item => item with { Text = WorkspaceLocalization.Translate(item.Text),
                ButtonText = item.ButtonText is null ? null : WorkspaceLocalization.Translate(item.ButtonText) }).ToArray()
            : ItemsSource;
        if (choices is not null) choices.ItemsSource = displayItems;
        SynchronizeSelection();
    }

    private static void OptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (SelectionDropDown)sender;
        control.RefreshDisplayOptions();
    }

    private static void ValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((SelectionDropDown)sender).SynchronizeSelection();

    private void SynchronizeSelection()
    {
        var selected = (displayItems as IEnumerable<SelectionOption>)?.FirstOrDefault(item => item.Value == SelectedValue);
        Content = ButtonContent ?? selected?.Label ?? string.Empty;
        if (choices is not null) choices.SelectedItem = selected;
    }

    private void Menu_Opening(object? sender, object e)
    {
        if (choices is null)
        {
            var resources = Application.Current.Resources;
            choices = new ListView
            {
                Style = (Style)resources["SelectionDropDownListStyle"],
                ItemContainerStyle = (Style)resources["SelectionDropDownItemStyle"],
                ItemTemplate = (DataTemplate)resources["SelectionDropDownItemTemplate"],
                ItemsSource = displayItems, SelectedValuePath = nameof(SelectionOption.Value),
                IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single
            };
            choices.ItemClick += (_, args) => Commit(args.ClickedItem as SelectionOption);
            choices.KeyDown += Choices_KeyDown;
            menu.Content = choices;
        }
        openingFocusState = FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        SynchronizeSelection();
        choices.RequestedTheme = ActualTheme;
        // Presenter padding is shared with the styles, so wider buttons keep a matching popup.
        var padding = (Thickness)Application.Current.Resources["SelectionDropDownPopupPadding"];
        choices.MinWidth = Math.Max(0, ActualWidth - padding.Left - padding.Right - 2);
        AutomationProperties.SetName(choices, AutomationProperties.GetName(this));
    }

    private void Menu_Opened(object? sender, object e)
    {
        if (choices is null) return;
        if (choices.SelectedItem is { } selected) choices.ScrollIntoView(selected);
        choices.UpdateLayout();
        if (choices.ContainerFromItem(choices.SelectedItem) is ListViewItem item) item.Focus(openingFocusState);
    }

    private void Choices_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
        Commit(choices?.SelectedItem as SelectionOption);
        e.Handled = true;
    }

    private void Commit(SelectionOption? option)
    {
        if (option is null) return;
        SelectedValue = option.Value;
        menu.Hide();
        SelectionCommitted?.Invoke(this, EventArgs.Empty);
    }
}
