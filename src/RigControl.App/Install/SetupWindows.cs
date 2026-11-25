using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RigControl.App.Install;

/// <summary>
/// The setup window: terms (accept), folder and ticks, progress, finish. Runs elevated. Installing runs off the
/// window's thread so the window keeps drawing.
/// </summary>
internal sealed class SetupWindow
{
    private readonly Window _window;
    private readonly string _version;
    private readonly string _sourceExe;
    private readonly string _userSid;
    private readonly Action<string> _log;
    private readonly TextBox _folder = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _desktop = SetupUi.Tick("Put a RigControl shortcut on the desktop", false);
    private readonly CheckBox _autostart = SetupUi.Tick("Start RigControl with Windows (as administrator, in the tray)", true);

    /// <summary>The installed RigControl, once installing worked.</summary>
    public InstallResult? Result { get; private set; }

    /// <summary>The finish screen's "Start RigControl now" tick.</summary>
    public bool StartNow { get; private set; }

    public SetupWindow(string version, string sourceExe, string userSid, string? existingRoot, Action<string> log)
    {
        _version = version;
        _sourceExe = sourceExe;
        _userSid = userSid;
        _log = log;
        _folder.Text = existingRoot ?? InstallLayout.DefaultRoot();
        _folder.Background = SetupUi.Panel;
        _folder.Foreground = SetupUi.Text;
        _folder.BorderBrush = SetupUi.Line;
        _folder.CaretBrush = SetupUi.Text;
        _window = SetupUi.Window("Install RigControl " + version, 720, 600);
        // Closing the window half way would end the process half way through the steps.
        _window.Closing += (_, e) => e.Cancel = _busy;
        ShowTerms();
    }

    private bool _busy;

    public void ShowDialog() => _window.ShowDialog();

    private void ShowTerms()
    {
        var page = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(SetupUi.Heading("Terms of use"));
        head.Children.Add(SetupUi.Para("RigControl " + _version + " installs only once you accept these terms. Accepting them is also your agreement to install the helper programs they list."));
        DockPanel.SetDock(head, Dock.Top);
        page.Children.Add(head);
        page.Children.Add(SetupUi.Scroller(new TextBlock
        {
            Text = TermsText.Plain(LicenceFiles.Read(LicenceFiles.Terms) ?? "The terms of use are missing from this copy of RigControl."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SetupUi.Text,
            LineHeight = 21,
        }));

        var cancel = SetupUi.Button("Cancel");
        cancel.Click += (_, _) => _window.Close();
        var accept = SetupUi.Button("I accept", primary: true);
        accept.IsEnabled = LicenceFiles.Read(LicenceFiles.Terms) is not null;
        accept.Click += (_, _) => ShowOptions(null);
        _window.Content = SetupUi.Frame(page, cancel, accept);
    }

    private void ShowOptions(string? problem)
    {
        var page = new StackPanel();
        page.Children.Add(SetupUi.Heading("Where to install"));
        page.Children.Add(SetupUi.Para("RigControl runs as administrator, so it goes in a folder only administrators can change. Your settings stay where they are."));

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var browse = SetupUi.Button("Browse…");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the install folder", Multiselect = false };
            if (dialog.ShowDialog(_window) == true)
                _folder.Text = Path.GetFileName(dialog.FolderName).Equals("RigControl", StringComparison.OrdinalIgnoreCase)
                    ? dialog.FolderName : Path.Combine(dialog.FolderName, "RigControl");
        };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_folder);
        page.Children.Add(row);
        page.Children.Add(_desktop);
        page.Children.Add(_autostart);
        page.Children.Add(SetupUi.Para("When RigControl first starts it installs the helper programs the terms list (OpenRGB, PawnIO, Equalizer APO). " +
                                       "A copy of RigControl you used before stays where it is; RigControl tells you where at the end."));
        if (problem is not null) page.Children.Add(SetupUi.Para(problem, SetupUi.Hot));

        var back = SetupUi.Button("Back");
        back.Click += (_, _) => ShowTerms();
        var install = SetupUi.Button("Install", primary: true);
        install.Click += (_, _) =>
        {
            if (InstallLayout.RootProblem(_folder.Text) is { } p) ShowOptions(p);
            else _ = InstallAsync();
        };
        _window.Content = SetupUi.Frame(page, back, install);
    }

    private async Task InstallAsync()
    {
        var page = new StackPanel();
        page.Children.Add(SetupUi.Heading("Installing RigControl"));
        var status = SetupUi.Para("Starting…", SetupUi.Text);
        var bar = new ProgressBar { Minimum = 0, Maximum = Installer.Steps, Height = 8, Foreground = SetupUi.Coolant, Background = SetupUi.Panel, BorderThickness = new Thickness(0) };
        page.Children.Add(status);
        page.Children.Add(bar);
        _window.Content = SetupUi.Frame(page);

        var request = new InstallRequest(_folder.Text.Trim(), _sourceExe, _version, _userSid, _desktop.IsChecked == true, _autostart.IsChecked == true);
        _busy = true;
        try
        {
            Result = await Task.Run(() => Installer.Run(request, (step, text) => _window.Dispatcher.BeginInvoke(() =>
            {
                bar.Value = step;
                status.Text = text;
            }), _log));
            _busy = false;
            ShowFinish(Result);
        }
        catch (Exception ex)
        {
            _busy = false;
            _log("Install failed: " + ex);
            ShowOptions("Installing did not finish: " + (ex is InstallFailedException ? ex.Message : ex.Message + " (the log has the details)"));
        }
    }

    private void ShowFinish(InstallResult result)
    {
        var page = new StackPanel();
        page.Children.Add(SetupUi.Heading("RigControl is installed"));
        page.Children.Add(SetupUi.Para("It is in " + result.Layout.Root + ", with a shortcut in the Start menu. Remove it from Windows' Installed apps."));
        if (result.OldCopies.Count > 0)
        {
            page.Children.Add(SetupUi.Para("RigControl no longer uses the copy you had before. It was left on disk; delete it yourself when you like:", SetupUi.Warm));
            foreach (var old in result.OldCopies) page.Children.Add(SetupUi.Para("    " + old, SetupUi.Text));
        }
        foreach (var note in result.Notes) page.Children.Add(SetupUi.Para(note, SetupUi.Warm));
        var start = SetupUi.Tick("Start RigControl now", true);
        page.Children.Add(start);

        var finish = SetupUi.Button("Finish", primary: true);
        finish.Click += (_, _) =>
        {
            StartNow = start.IsChecked == true;
            _window.Close();
        };
        _window.Content = SetupUi.Frame(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, finish);
    }
}

/// <summary>"RigControl is installed. Open it / Reinstall", when this version or a newer one is installed already.</summary>
internal static class InstalledWindow
{
    public enum Choice { Close, Open, Reinstall }

    public static Choice Ask(InstalledCopy installed, string myVersion)
    {
        var choice = Choice.Close;
        var w = SetupUi.Window("RigControl", 560, 300);
        var page = new StackPanel();
        page.Children.Add(SetupUi.Heading("RigControl is installed"));
        page.Children.Add(SetupUi.Para($"RigControl {installed.Version ?? "(unknown version)"} is installed in {installed.Layout.Root}. This file is version {myVersion}."));
        var close = SetupUi.Button("Close");
        close.Click += (_, _) => w.Close();
        var reinstall = SetupUi.Button("Reinstall");
        reinstall.Click += (_, _) => { choice = Choice.Reinstall; w.Close(); };
        var open = SetupUi.Button("Open it", primary: true);
        open.IsEnabled = File.Exists(installed.Layout.AppExe);
        open.Click += (_, _) => { choice = Choice.Open; w.Close(); };
        w.Content = SetupUi.Frame(page, close, reinstall, open);
        w.ShowDialog();
        return choice;
    }
}

/// <summary>The uninstall confirmation, its progress and its end.</summary>
internal sealed class UninstallWindow
{
    private readonly Window _window = SetupUi.Window("Uninstall RigControl", 600, 380);
    private readonly InstalledCopy _installed;
    private readonly string _userSid;
    private readonly Action<string> _log;

    public UninstallWindow(InstalledCopy installed, string userSid, Action<string> log)
    {
        _installed = installed;
        _userSid = userSid;
        _log = log;
        _window.Closing += (_, e) => e.Cancel = _busy;
        Confirm();
    }

    private bool _busy;

    public void ShowDialog() => _window.ShowDialog();

    private void Confirm()
    {
        var page = new StackPanel();
        page.Children.Add(SetupUi.Heading("Remove RigControl?"));
        page.Children.Add(SetupUi.Para($"RigControl closes and its files are removed from {_installed.Layout.Root}, with its shortcuts and Start with Windows."));
        page.Children.Add(SetupUi.Para("The helper programs (OpenRGB, PawnIO, Equalizer APO) stay installed, as the terms say, because other programs may use them. Remove them from Windows' Installed apps if you no longer want them."));
        var purge = SetupUi.Tick("Also delete my settings, logs and Discord sign-in", false);
        page.Children.Add(purge);
        var cancel = SetupUi.Button("Cancel");
        cancel.Click += (_, _) => _window.Close();
        var go = SetupUi.Button("Uninstall", danger: true);
        go.Click += (_, _) => _ = RunAsync(purge.IsChecked == true);
        _window.Content = SetupUi.Frame(page, cancel, go);
    }

    private async Task RunAsync(bool purge)
    {
        var page = new StackPanel();
        page.Children.Add(SetupUi.Heading("Removing RigControl"));
        var status = SetupUi.Para("Starting…", SetupUi.Text);
        page.Children.Add(status);
        _window.Content = SetupUi.Frame(page);
        UninstallResult result;
        _busy = true;
        try
        {
            result = await Task.Run(() => Uninstaller.Run(_installed.Layout, _userSid, purge, System.Environment.ProcessPath ?? string.Empty,
                text => _window.Dispatcher.BeginInvoke(() => status.Text = text), _log));
        }
        catch (Exception ex)
        {
            _log("Uninstall failed: " + ex);
            result = new UninstallResult(new[] { "Removing did not finish: " + ex.Message });
        }
        _busy = false;

        var done = new StackPanel();
        done.Children.Add(SetupUi.Heading(result.Notes.Count == 0 ? "RigControl has been removed" : "RigControl has been removed, mostly"));
        foreach (var note in result.Notes) done.Children.Add(SetupUi.Para(note, SetupUi.Warm));
        var close = SetupUi.Button("Close", primary: true);
        close.Click += (_, _) => _window.Close();
        _window.Content = SetupUi.Frame(done, close);
    }
}
