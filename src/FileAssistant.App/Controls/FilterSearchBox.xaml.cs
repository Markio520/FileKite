using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Controls;

/// <summary>A shared type selector and live query field, combined or separated in a task toolbar.</summary>
public sealed partial class FilterSearchBox : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(FilterSearchBox), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(int), typeof(FilterSearchBox), new PropertyMetadata(0));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FilterSearchBox), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(FilterSearchBox), new PropertyMetadata("搜索当前列表"));
    public static readonly DependencyProperty SeparateFieldsProperty = DependencyProperty.Register(
        nameof(SeparateFields), typeof(bool), typeof(FilterSearchBox), new PropertyMetadata(false,
            (owner, _) => ((FilterSearchBox)owner).UpdateFieldLayout()));

    public object? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public int SelectedValue { get => (int)GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string PlaceholderText { get => (string)GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }
    public bool SeparateFields { get => (bool)GetValue(SeparateFieldsProperty); set => SetValue(SeparateFieldsProperty, value); }

    public FilterSearchBox()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateFieldLayout();
            WorkspaceMotion.AlignSingleLineInputs(QueryInput);
        };
    }

    private void UpdateFieldLayout() => VisualStateManager.GoToState(this, SeparateFields ? "Separated" : "Combined", false);

    public void FocusQuery() => QueryInput.Focus(FocusState.Programmatic);

    private void Query_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) Text = sender.Text;
    }

    private void Query_GotFocus(object sender, RoutedEventArgs e) => VisualStateManager.GoToState(this, "Focused", true);
    private void Query_LostFocus(object sender, RoutedEventArgs e) => VisualStateManager.GoToState(this, "Unfocused", true);
}
