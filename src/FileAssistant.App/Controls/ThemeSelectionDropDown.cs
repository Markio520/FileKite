using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using FileAssistant.App.Features.Settings;

namespace FileAssistant.App.Controls;

/// <summary>Application appearance selector using the shared single-choice menu.</summary>
public sealed class ThemeSelectionDropDown : SelectionDropDown
{
    private AppearanceViewModel? appearance;

    public ThemeSelectionDropDown()
    {
        WorkspaceLocalization.SetOptions(this, true);
        ItemsSource = new SelectionOption[]
        {
            new((int)WorkspaceAppearance.Light, "浅色（沙白画廊）"),
            new((int)WorkspaceAppearance.CharcoalGlass, "深色（炭黑微光）"),
            new((int)WorkspaceAppearance.System, "跟随系统")
        };
        ButtonContent = new FontIcon { Glyph = "\uE790", FontSize = 16 };
        MinWidth = 0;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        WorkspaceLocalization.SetTip(this, "切换应用外观");
        WorkspaceLocalization.SetName(this, "切换应用外观");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SelectionCommitted += (_, _) =>
        {
            if (appearance is not null) appearance.Style = (WorkspaceAppearance)SelectedValue;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (appearance is not null) return;
        appearance = App.Appearance;
        SelectedValue = (int)appearance.Style;
        appearance.PropertyChanged += AppearanceChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (appearance is not null) appearance.PropertyChanged -= AppearanceChanged;
        appearance = null;
    }

    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(AppearanceViewModel.Style)) SelectedValue = (int)appearance!.Style; }
}
