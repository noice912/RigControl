using System.Text;
using System.Windows;
using AudioController.App.Services;

namespace AudioController.App;

/// <summary>One dialog listing many results (per-file import outcomes, hotkey conflicts).</summary>
public partial class ReportWindow : Window
{
    private readonly string _summary;
    private readonly IReadOnlyList<ReportLine> _lines;

    public ReportWindow(string title, string summary, IReadOnlyList<ReportLine> lines)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = title;
        _summary = summary;
        _lines = lines;
        SummaryText.Text = summary;
        LinesList.ItemsSource = lines;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_summary);
        foreach (var line in _lines)
        {
            sb.Append(line.Ok ? "[ok] " : "[failed] ").Append(line.Title);
            if (!string.IsNullOrWhiteSpace(line.Detail)) sb.Append(" — ").Append(line.Detail);
            sb.AppendLine();
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception)
        {
            // The clipboard can be locked by another program; copying is a convenience only.
        }
    }
}
