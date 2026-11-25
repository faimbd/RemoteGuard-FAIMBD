using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHAlign = System.Windows.HorizontalAlignment;

namespace WinFixCleanPortable.Views;

/// <summary>
/// Black &amp; Gold input / type-to-confirm dialog (built in code).
/// If <c>requiredText</c> is given, OK is only enabled after the user types it exactly (used before formatting).
/// </summary>
public class InputDialog : Window
{
    private readonly WpfTextBox _box;
    private readonly WpfButton _ok;
    private readonly string? _required;

    public string ResponseText => _box.Text.Trim();

    public InputDialog(string title, string message, string defaultText = "", string? requiredText = null, bool danger = false)
    {
        _required = requiredText;
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x09, 0x09, 0x0B));
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0xF6, 0xEE));
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = danger ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xCA, 0xCA)) : Foreground
        });

        if (requiredText != null)
        {
            root.Children.Add(new TextBlock
            {
                Text = $"Type  {requiredText}  to confirm:",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xC5, 0x42)),
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        _box = new WpfTextBox { Text = defaultText, Margin = new Thickness(0, 0, 0, 16) };
        if (System.Windows.Application.Current.TryFindResource("DarkTextBox") is Style tbStyle) _box.Style = tbStyle;
        root.Children.Add(_box);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = WpfHAlign.Right };
        _ok = new WpfButton { Content = danger ? "Confirm" : "OK", Margin = new Thickness(0, 0, 8, 0), IsDefault = true, MinWidth = 90 };
        var cancel = new WpfButton { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        if (System.Windows.Application.Current.TryFindResource(danger ? "DangerButton" : "PrimaryButton") is Style okStyle) _ok.Style = okStyle;
        if (System.Windows.Application.Current.TryFindResource("GhostButton") is Style cStyle) cancel.Style = cStyle;
        _ok.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        _box.TextChanged += (_, _) => UpdateOk();
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); UpdateOk(); };
    }

    private void UpdateOk()
    {
        _ok.IsEnabled = _required == null
            ? _box.Text.Trim().Length > 0
            : _box.Text.Trim().Equals(_required, StringComparison.OrdinalIgnoreCase);
    }
}
