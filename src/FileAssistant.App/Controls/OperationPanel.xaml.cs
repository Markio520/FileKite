using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Controls;

public sealed partial class OperationPanel : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(OperationPanel),
        new PropertyMetadata(false, (sender, _) => ((OperationPanel)sender).ApplyDensity()));

    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(UIElement), typeof(OperationPanel),
        new PropertyMetadata(null, (sender, e) => WorkspaceMotion.AlignSingleLineInputs(e.NewValue as DependencyObject)));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(UIElement), typeof(OperationPanel), new PropertyMetadata(null));

    public UIElement? Body { get => (UIElement?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public UIElement? Footer { get => (UIElement?)GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    public OperationPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => WorkspaceMotion.AlignSingleLineInputs(Body);
    }

    private void ApplyDensity()
    {
        if (IsCompact)
            BodySurface.Template = (ControlTemplate)Application.Current.Resources["OpCompactBodyTemplate"];
        else BodySurface.ClearValue(Control.TemplateProperty);
        BodyScroll.Padding = IsCompact ? new Thickness(11, 10, 11, 12) : new Thickness(16, 18, 16, 20);
    }

    public void ScrollToTop() => BodyScroll.ChangeView(null, 0, null, disableAnimation: true);
}
