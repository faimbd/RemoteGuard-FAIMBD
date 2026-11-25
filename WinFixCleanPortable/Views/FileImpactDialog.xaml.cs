using System.Windows;
using System.Windows.Media;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Services;

using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace WinFixCleanPortable.Views;

public enum FileImpactDialogAction
{
    None,
    MoveToRecycleBin,
    DeletePermanently
}

public partial class FileImpactDialog : Window
{
    private readonly FileImpactAnalysis _analysis;

    public FileImpactDialogAction RequestedAction { get; private set; } = FileImpactDialogAction.None;

    public FileImpactDialog(FileImpactAnalysis analysis)
    {
        InitializeComponent();
        _analysis = analysis;

        FileNameText.Text = analysis.FileName;
        FileMetaText.Text = $"{analysis.SizeFormatted}  •  Type: {analysis.Extension}";
        FilePathText.Text = analysis.FullPath;
        RiskLevelText.Text = analysis.RiskLevel;
        RiskBadgeBorder.Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(analysis.RiskBadgeColor));
        RiskTitleText.Text = analysis.RiskTitle;
        WhatIsText.Text = analysis.WhatIsThisFile;
        ImpactText.Text = analysis.WhatHappensIfDeleted;
        RecommendationText.Text = analysis.Recommendation;
    }

    private void GoogleWhatIs_Click(object sender, RoutedEventArgs e)
        => FileImpactService.OpenUrlInBrowser(_analysis.GoogleWhatIsUrl);

    private void GoogleImpact_Click(object sender, RoutedEventArgs e)
        => FileImpactService.OpenUrlInBrowser(_analysis.GoogleDeleteImpactUrl);

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
        => FormatHelper.RevealInExplorer(_analysis.FullPath);

    private void RecycleNow_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = FileImpactDialogAction.MoveToRecycleBin;
        DialogResult = true;
        Close();
    }

    private void DeletePermanentNow_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            $"PERMANENTLY DELETE '{_analysis.FileName}' ({_analysis.SizeFormatted})?\nThis CANNOT be undone!",
            "Confirm Permanent Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            RequestedAction = FileImpactDialogAction.DeletePermanently;
            DialogResult = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = FileImpactDialogAction.None;
        DialogResult = false;
        Close();
    }
}
