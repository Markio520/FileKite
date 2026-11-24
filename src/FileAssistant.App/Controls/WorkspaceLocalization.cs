using FileAssistant.App.Features.Settings;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;

namespace FileAssistant.App.Controls;

/// <summary>Localizes explicitly marked interface labels. File names, paths, group names, and user text keep their original bindings.</summary>
public sealed partial class WorkspaceLocalization : DependencyObject
{
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(nameof(Revision), typeof(int), typeof(WorkspaceLocalization), new PropertyMetadata(0));
    public int Revision { get => (int)GetValue(RevisionProperty); private set => SetValue(RevisionProperty, value); }

    private static WorkspaceLocalization? source;
    private static AppPreferences? observed;
    private static InterfaceLanguage? lastLanguage;
    private static string? lastCulture;
    private static readonly WordConverter Converter = new();
    internal static event EventHandler? LanguageChanged;

    public static void Refresh()
    {
        if (App.DispatcherQueue is { HasThreadAccess: false } queue) { queue.TryEnqueue(Refresh); return; }
        var language = App.Preferences?.Current.Language ?? InterfaceLanguage.System;
        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (lastLanguage == language && lastCulture == culture) return;
        lastLanguage = language; lastCulture = culture;
        if (source is not null) source.Revision++;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
    private static void Observe()
    {
        if (ReferenceEquals(observed, App.Preferences)) return;
        if (observed is not null) observed.Changed -= SettingsChanged;
        observed = App.Preferences;
        if (observed is not null) observed.Changed += SettingsChanged;
        Refresh();
    }
    private static void SettingsChanged(object? sender, EventArgs args) => Refresh();

    // WinUI's Run.Text is a regular property. Use the same live language binding through this proxy.
    private static readonly DependencyProperty InlineTextProperty = DependencyProperty.RegisterAttached("InlineText", typeof(string),
        typeof(WorkspaceLocalization), new PropertyMetadata("", (d, args) => ((Run)d).Text = (string)args.NewValue));
    public static readonly DependencyProperty TextProperty = Word("Text", d => d switch
    {
        TextBlock => TextBlock.TextProperty, Run => InlineTextProperty, MenuFlyoutItem => MenuFlyoutItem.TextProperty,
        MenuFlyoutSubItem => MenuFlyoutSubItem.TextProperty, _ => null
    });
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    public static readonly DependencyProperty ContentProperty = Word("Content", d => d is ContentControl ? ContentControl.ContentProperty : null);
    public static string GetContent(DependencyObject d) => (string)d.GetValue(ContentProperty);
    public static void SetContent(DependencyObject d, string value) => d.SetValue(ContentProperty, value);

    public static readonly DependencyProperty PlaceholderProperty = Word("Placeholder", d => d switch
    {
        TextBox => TextBox.PlaceholderTextProperty, AutoSuggestBox => AutoSuggestBox.PlaceholderTextProperty,
        CalendarDatePicker => CalendarDatePicker.PlaceholderTextProperty, FilterSearchBox => FilterSearchBox.PlaceholderTextProperty, _ => null
    });
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty HeaderProperty = Word("Header", d => d switch
    {
        TextBox => TextBox.HeaderProperty, PivotItem => PivotItem.HeaderProperty, _ => null
    });
    public static string GetHeader(DependencyObject d) => (string)d.GetValue(HeaderProperty);
    public static void SetHeader(DependencyObject d, string value) => d.SetValue(HeaderProperty, value);

    public static readonly DependencyProperty NameProperty = Word("Name", _ => AutomationProperties.NameProperty);
    public static string GetName(DependencyObject d) => (string)d.GetValue(NameProperty);
    public static void SetName(DependencyObject d, string value) => d.SetValue(NameProperty, value);

    public static readonly DependencyProperty TipProperty = Word("Tip", _ => ToolTipService.ToolTipProperty);
    public static string GetTip(DependencyObject d) => (string)d.GetValue(TipProperty);
    public static void SetTip(DependencyObject d, string value) => d.SetValue(TipProperty, value);

    public static readonly DependencyProperty HelpProperty = Word("Help", _ => AutomationProperties.HelpTextProperty);
    public static string GetHelp(DependencyObject d) => (string)d.GetValue(HelpProperty);
    public static void SetHelp(DependencyObject d, string value) => d.SetValue(HelpProperty, value);

    public static readonly DependencyProperty OptionsProperty = DependencyProperty.RegisterAttached("Options", typeof(bool), typeof(WorkspaceLocalization),
        new PropertyMetadata(false, (d, _) => { Observe(); if (d is SelectionDropDown menu) menu.RefreshDisplayOptions(); }));
    public static bool GetOptions(DependencyObject d) => (bool)d.GetValue(OptionsProperty);
    public static void SetOptions(DependencyObject d, bool value) => d.SetValue(OptionsProperty, value);

    private static DependencyProperty Word(string name, Func<DependencyObject, DependencyProperty?> target) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(WorkspaceLocalization), new PropertyMetadata("", (d, args) =>
        {
            if (target(d) is not { } property) return;
            Observe();
            source ??= new();
            BindingOperations.SetBinding(d, property, new Binding
            {
                Mode = BindingMode.OneWay, Source = source, Path = new PropertyPath(nameof(Revision)),
                Converter = Converter, ConverterParameter = args.NewValue as string ?? ""
            });
        }));

    private sealed class WordConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => Translate((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
}
