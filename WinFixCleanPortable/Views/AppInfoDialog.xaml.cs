using System.IO;
using System.Windows;
using System.Windows.Media;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;
using WinFixCleanPortable.Services;

using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace WinFixCleanPortable.Views;

public partial class AppInfoDialog : Window
{
    private readonly InstalledAppItem _app;

    public bool UninstallRequested { get; private set; }

    public AppInfoDialog(InstalledAppItem app)
    {
        InitializeComponent();
        _app = app;

        AppNameText.Text = app.DisplayName;
        AppMetaText.Text = app.InfoFooter;
        CategoryText.Text = app.InfoCategory;
        DescriptionText.Text = app.InfoDescription;
        AdviceText.Text = app.InfoAdvice;
        AdviceLabelText.Text = app.InfoAdviceLabel;
        AdviceBadge.Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(app.InfoAdviceColor));

        if (app.HasExtraNote)
        {
            ExtraNoteText.Text = app.InfoExtraNote;
            ExtraNoteBorder.Visibility = Visibility.Visible;
            if (app.IsBrowserCore)
            {
                ExtraNoteBorder.Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#2A1010"));
                ExtraNoteBorder.BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#B91C1C"));
                ExtraNoteText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FECACA"));
            }
        }

        if (app.IsWebApp)
            UninstallBtn.Content = $"🗑 Remove only this web app ({app.HostBrowserName} stays)";
    }

    private void GoogleWhatIs_Click(object sender, RoutedEventArgs e) => FileImpactService.OpenUrlInBrowser(_app.GoogleWhatIsUrl);
    private void GoogleSafe_Click(object sender, RoutedEventArgs e) => FileImpactService.OpenUrlInBrowser(_app.GoogleSafeUninstallUrl);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_app.InstallLocation) && Directory.Exists(_app.InstallLocation))
            FormatHelper.OpenFolderInExplorer(_app.InstallLocation);
        else
            System.Windows.MessageBox.Show("Install folder is not recorded for this app.", "Folder Unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        UninstallRequested = true;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
