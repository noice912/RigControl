namespace AudioController.App.Services;

/// <summary>One line of a report dialog (import summary, hotkey conflicts).</summary>
public sealed record ReportLine(bool Ok, string Title, string? Detail);

/// <summary>Dialogs the view models need; implemented by the main window.</summary>
public interface IUiService
{
    bool Confirm(string title, string message);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    /// <summary>One dialog listing many items, e.g. per-file import results.</summary>
    void ShowReport(string title, string summary, IReadOnlyList<ReportLine> lines);
}
