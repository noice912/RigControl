using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Apple;
using RigControl.iPhone.Device;
using RigControl.iPhone.Helper;
using RigControl.Updates;

namespace RigControl.App.IPhone;

/// <summary>
/// The iPhone installer's window (plain WPF, no web page: the password box is a native control in this non-admin
/// process). Shows the connected iPhone, signs in with the Apple ID (password and code go to Apple only), and
/// installs or renews RigControl Mobile. In renewal mode it starts on its own and closes when done.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallerWindow : Window
{
    private readonly InstallerSession _session;
    private readonly bool _renew;
    private readonly bool _update;
    private readonly TextBlock _device = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _account = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _appleId = new() { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(4) };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(4) };
    private readonly Button _signIn = new() { Content = "Sign in", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _signOut = new() { Content = "Sign out", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _install = new() { Content = "Install RigControl Mobile", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 16, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBlock _imported = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _import = new() { Content = "Import certificate (.p12 + .mobileprovision)...", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _installImported = new() { Content = "Install with my certificate", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _removeImported = new() { Content = "Remove", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _installPresigned = new() { Content = "Install a pre-signed .ipa...", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _signInPanel = new();
    private readonly StackPanel _signedInPanel = new() { Orientation = Orientation.Horizontal };
    private readonly CancellationTokenSource _closing = new();
    private DeviceInfo? _phone;
    private bool _busy;

    public InstallerWindow(InstallerSession session, bool renew, bool update = false)
    {
        _session = session;
        _renew = renew;
        _update = update;
        Title = "RigControl - Install on iPhone";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Heading("Your iPhone"));
        root.Children.Add(_device);

        root.Children.Add(Heading("Free Apple ID (blocked by Apple for now)"));
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "Since October 2026 Apple blocks free Apple ID sign-in from Windows, so this part will most likely " +
                   "fail. Use your own certificate below instead (it needs a paid Apple developer account). If you try it: " +
                   "your password and the code Apple sends are passed to Apple only, and RigControl never saves them. " +
                   "What's kept (encrypted for your Windows account) is the sign-in Apple gives back and your signing " +
                   "certificate, so the app can be signed again each week.",
        });
        _signInPanel.Children.Add(new TextBlock { Text = "Apple ID (email)" });
        _signInPanel.Children.Add(_appleId);
        _signInPanel.Children.Add(new TextBlock { Text = "Password" });
        _signInPanel.Children.Add(_password);
        _signInPanel.Children.Add(_signIn);
        root.Children.Add(_signInPanel);
        _signedInPanel.Children.Add(_account);
        _signedInPanel.Children.Add(_signOut);
        root.Children.Add(_signedInPanel);

        root.Children.Add(_install);
        root.Children.Add(_status);

        root.Children.Add(Heading("Your own signing certificate (paid Apple developer account)"));
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "This needs a paid Apple developer certificate: a .p12 and a .mobileprovision profile from your Apple " +
                   "developer account (or a Mac's Xcode), and the profile must list this iPhone. The .p12 password is " +
                   "used once to open the file and is never saved; the certificate is kept encrypted for your Windows account.",
        });
        root.Children.Add(_imported);
        root.Children.Add(_import);
        var importedButtons = new StackPanel { Orientation = Orientation.Horizontal };
        importedButtons.Children.Add(_installImported);
        importedButtons.Children.Add(_removeImported);
        root.Children.Add(importedButtons);
        root.Children.Add(_installPresigned);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _import.Click += async (_, _) => await ImportAsync();
        _installImported.Click += async (_, _) => await InstallImportedAsync();
        _installPresigned.Click += async (_, _) => await InstallPresignedAsync();
        _removeImported.Click += (_, _) =>
        {
            try { Installer()?.Imported.Delete(); Status("The imported certificate and profile were removed."); }
            catch (Exception ex) { ShowError(ex); }
            Refresh();
        };

        _signIn.Click += async (_, _) => await SignInAsync();
        _password.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await SignInAsync(); };
        _signOut.Click += (_, _) =>
        {
            try { Installer()?.SignOut(); }
            catch (Exception ex) { ShowError(ex); }
            Refresh();
        };
        _install.Click += async (_, _) => await InstallAsync();
        Closed += (_, _) => _closing.Cancel();
        Loaded += async (_, _) =>
        {
            try { await StartAsync(); }
            catch (Exception ex) { ShowError(ex); }
        };
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) };

    private IphoneInstaller? Installer()
    {
        try
        {
            return _session.Installer();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return null;
        }
    }

    private async Task StartAsync()
    {
        Refresh();
        await FindPhoneAsync();
        _ = WatchAsync();
        if (_update)
        {
            await UpdateAsync();
            return;
        }
        if (!_renew)
            return;
        var installer = Installer();
        var installed = _phone is null ? null : IphoneInstaller.LoadStatus(IphoneHelper.Paths).FirstOrDefault(a => a.Udid == _phone.Udid);
        if (installed?.Source == InstalledApp.ImportedSource)
        {
            // Re-signing only helps with a profile that runs longer than the one on the iPhone.
            var info = installer?.Imported.Info();
            if (info is not null && info.Covers(_phone!.Udid) && info.Expires > installed.Expires.AddHours(1))
            {
                await InstallImportedAsync();
                if (_status.Text.StartsWith("Done", StringComparison.Ordinal))
                    Close();
            }
            else
            {
                Status($"RigControl Mobile on your iPhone stops opening {installed.Expires.LocalDateTime:g}, when your imported " +
                       "profile or certificate runs out. Import a renewed .mobileprovision (and .p12 if needed), then press " +
                       "\"Install with my certificate\".");
            }
        }
        else if (installed?.Source == InstalledApp.PresignedSource)
        {
            Status($"The app you installed from a pre-signed .ipa stops opening {installed.Expires.LocalDateTime:g}. " +
                   "Install a newly signed .ipa before then.");
        }
        else if (_phone is not null && installer?.CanWorkWithoutPassword() == true)
        {
            await InstallAsync();
            if (_status.Text.StartsWith("Done", StringComparison.Ordinal))
                Close();
        }
        else
        {
            Status("RigControl Mobile on your iPhone needs signing again. Sign in with your Apple ID and press Install.");
        }
    }

    /// <summary>
    /// Auto-update (started by the main app): installs this release's phone app with what's already saved (the imported
    /// certificate, or a usable Apple sign-in) and closes when done. Nothing to do, or nothing it can do without the
    /// person: it closes quietly. An error stays on screen.
    /// </summary>
    private async Task UpdateAsync()
    {
        var plan = await PlanUpdateAsync(_session);
        if (plan is null || _phone is null || _phone.Udid != plan.Value.Udid)
        {
            // The iPhone went away (or another one is in front) since the plan was made: try again later.
            Close();
            return;
        }
        var release = InstallerSession.CurrentRelease();
        IphoneInstaller.NoteUpdateTry(IphoneHelper.Paths, plan.Value.Udid, release, DateTimeOffset.UtcNow);
        _session.Log($"iPhone: updating RigControl Mobile on {_phone.Name} to {release} with the " +
                     (plan.Value.Imported ? "imported certificate." : "saved Apple sign-in."));
        if (plan.Value.Imported) await InstallImportedAsync();
        else await InstallAsync();
        // Done: close. Failed (no network, no phone app for this build...): note it in the log and close too; the
        // main app tries again in 3 hours. A background update never leaves an error window behind.
        if (!_status.Text.StartsWith("Done", StringComparison.Ordinal))
            _session.Log("iPhone: the phone app update didn't finish: " + _status.Text.ReplaceLineEndings(" "));
        Close();
    }

    /// <summary>
    /// What auto-update would do: the iPhone (connected, over USB or Wi-Fi) whose app is older than this release, and
    /// whether to sign with the imported certificate (only for an app installed with it) or the saved Apple sign-in.
    /// Null: nothing due, nothing reachable, or nothing it can do without the person.
    /// </summary>
    internal static async Task<(string Udid, bool Imported)?> PlanUpdateAsync(InstallerSession session)
    {
        try
        {
            var release = InstallerSession.CurrentRelease();
            var now = DateTimeOffset.UtcNow;
            var due = IphoneInstaller.LoadStatus(IphoneHelper.Paths).Where(a => a.UpdateDue(release)).ToList();
            if (due.Count == 0) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var reachable = await session.Devices.ListAsync(cts.Token).ConfigureAwait(false);
            var installer = session.Installer();
            ImportedIdentityInfo? info = null;
            try { info = installer.Imported.Info(); }
            catch (Exception) { /* none usable */ }
            foreach (var app in due.OrderByDescending(a => reachable.Any(d => d.Udid == a.Udid && d.Usb)))
            {
                if (!reachable.Any(d => d.Udid == app.Udid)) continue;
                if (app.Source == InstalledApp.ImportedSource && info is not null && info.Covers(app.Udid) && info.Expires > now.AddHours(1))
                    return (app.Udid, true);
                if (app.Source == InstalledApp.AppleIdSource && installer.CanWorkWithoutPassword())
                    return (app.Udid, false);
            }
            // Note it, so the main app doesn't start this check again for 3 hours.
            foreach (var app in due.Where(a => reachable.Any(d => d.Udid == a.Udid)))
                IphoneInstaller.NoteUpdateTry(IphoneHelper.Paths, app.Udid, release, now);
            session.Log("iPhone: a newer RigControl Mobile is ready, but no reachable iPhone can get it without you " +
                        "(no saved certificate or sign-in for it).");
            return null;
        }
        catch (Exception ex)
        {
            session.Log("iPhone: phone app update check skipped: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private void Refresh()
    {
        var saved = Installer()?.SavedAccount();
        var signedIn = saved is not null && saved.SessionUsable(TimeSpan.FromMinutes(10));
        _signInPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _signedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _account.Text = signedIn ? "Signed in as " + saved!.Session.AppleId : "";
        if (saved is not null && !signedIn) _appleId.Text = saved.Session.AppleId;
        _install.IsEnabled = !_busy && signedIn && _phone is not null;
        ImportedIdentityInfo? imported = null;
        try { imported = Installer()?.Imported.Info(); }
        catch (Exception) { /* shown as "none" */ }
        _imported.Text = imported is null
            ? "No certificate imported."
            : $"{imported.CertificateName} (team {imported.TeamId}), profile \"{imported.ProfileName}\" for {imported.AppId}, " +
              $"valid until {imported.Expires.LocalDateTime:g}." +
              (_phone is not null && !imported.Covers(_phone.Udid) ? " This profile doesn't include the connected iPhone." : "");
        _import.IsEnabled = !_busy;
        _installImported.IsEnabled = !_busy && imported is not null && _phone is not null;
        _removeImported.IsEnabled = !_busy && imported is not null;
        _installPresigned.IsEnabled = !_busy && _phone is not null;
        _signIn.IsEnabled = !_busy;
    }

    private async Task FindPhoneAsync()
    {
        try
        {
            var list = await _session.Devices.ListAsync(_closing.Token);
            var due = _update
                ? IphoneInstaller.LoadStatus(IphoneHelper.Paths).Where(a => a.UpdateDue(InstallerSession.CurrentRelease())).Select(a => a.Udid).ToHashSet()
                : [];
            _phone = list.Where(d => due.Contains(d.Udid)).OrderByDescending(d => d.Usb).FirstOrDefault()
                     ?? list.FirstOrDefault(d => d.Usb) ?? list.FirstOrDefault();
            var installed = _phone is null ? null : IphoneInstaller.LoadStatus(IphoneHelper.Paths).FirstOrDefault(a => a.Udid == _phone.Udid);
            _device.Text = _phone is null
                ? "No iPhone found. Plug your iPhone in with a USB cable, unlock it, and tap Trust if it asks."
                : $"{_phone.Name} ({_phone.ProductType}, iOS {_phone.ProductVersion}) over {(_phone.Usb ? "USB" : "Wi-Fi")}." +
                  (installed is null ? "" : $"\nRigControl Mobile signed {installed.SignedAt.LocalDateTime:g}, valid until {installed.Expires.LocalDateTime:g}.");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _phone = null;
            _device.Text = ex is DeviceException ? ex.Message : "RigControl couldn't ask Apple's USB service about your iPhone: " + ex.Message;
            _session.Log("iPhone installer: device list: " + ex.GetType().Name + ": " + ex.Message);
        }
        Refresh();
    }

    private async Task WatchAsync()
    {
        try
        {
            await foreach (var _ in _session.Devices.WatchAsync(_closing.Token))
                await FindPhoneAsync();
        }
        catch (Exception)
        {
            // Watching is a convenience; the list is read again before installing anyway.
        }
    }

    private async Task SignInAsync()
    {
        var installer = Installer();
        if (installer is null || _busy) return;
        var id = _appleId.Text.Trim();
        if (id.Length == 0 || _password.SecurePassword.Length == 0)
        {
            Status("Type your Apple ID and password.");
            return;
        }
        var password = ReadPassword(_password);
        _password.Clear();
        await Busy("Signing in to Apple...", async () =>
        {
            await installer.SignInAsync(id, password, new SecondFactorDialog(this), _closing.Token);
            Status("Signed in.");
        });
    }

    /// <summary>The password as a char[] the sign-in code wipes after use (never a string).</summary>
    private static char[] ReadPassword(PasswordBox box)
    {
        var secure = box.SecurePassword;
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            var chars = new char[secure.Length];
            Marshal.Copy(ptr, chars, 0, chars.Length);
            return chars;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            secure.Dispose();
        }
    }

    private async Task InstallAsync()
    {
        var installer = Installer();
        if (installer is null || _phone is null || _busy) return;
        var udid = _phone.Udid;
        await Busy("Starting...", async () =>
        {
            var progress = new Progress<string>(Status);
            var ipa = await _session.DownloadIpaAsync(progress, _closing.Token);
            var app = await installer.InstallAsync(udid, ipa, progress, _closing.Token);
            if (_session.LastRelease is { } r) IphoneInstaller.SetRelease(IphoneHelper.Paths, udid, r);
            Status($"Done. RigControl Mobile is on your iPhone, signed until {app.Expires.LocalDateTime:g}. " +
                   "RigControl signs it again before then whenever the iPhone is plugged in.\n" +
                   "First time only: on the iPhone open Settings > General > VPN & Device Management, tap your Apple ID " +
                   "under Developer App and tap Trust.");
        });
        await FindPhoneAsync();
    }

    private async Task ImportAsync()
    {
        var installer = Installer();
        if (installer is null || _busy) return;
        var p12Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose your signing certificate (.p12)",
            Filter = "Signing certificate (*.p12;*.pfx)|*.p12;*.pfx",
            CheckFileExists = true,
        };
        if (p12Dialog.ShowDialog(this) != true) return;
        var profileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose its provisioning profile (.mobileprovision)",
            Filter = "Provisioning profile (*.mobileprovision)|*.mobileprovision",
            CheckFileExists = true,
        };
        if (profileDialog.ShowDialog(this) != true) return;
        var password = PasswordDialog.Ask(this, "Password of " + Path.GetFileName(p12Dialog.FileName));
        if (password is null) return;
        await Busy("Checking the certificate and profile...", () =>
        {
            ImportedIdentityInfo info;
            try
            {
                // Sizes first: a .p12 is a few kB and a profile well under 4 MB.
                if (new FileInfo(p12Dialog.FileName).Length > 1 << 20)
                    throw new InvalidDataException("That .p12 file is too large to be a signing certificate.");
                if (new FileInfo(profileDialog.FileName).Length > RigControl.iPhone.Helper.ImportedIdentityStore.MaxProfileBytes)
                    throw new InvalidDataException("That .mobileprovision file is too large to be a provisioning profile.");
                var p12 = File.ReadAllBytes(p12Dialog.FileName);
                var profile = File.ReadAllBytes(profileDialog.FileName);
                info = installer.Imported.Import(p12, password, profile);
            }
            finally
            {
                Array.Clear(password);
            }
            Status($"Imported {info.CertificateName}. Apps signed with it open until {info.Expires.LocalDateTime:g}." +
                   (_phone is not null && !info.Covers(_phone.Udid) ? " Note: this profile doesn't include the connected iPhone." : ""));
            return Task.CompletedTask;
        });
    }

    private async Task InstallImportedAsync()
    {
        var installer = Installer();
        if (installer is null || _phone is null || _busy) return;
        var udid = _phone.Udid;
        await Busy("Starting...", async () =>
        {
            var progress = new Progress<string>(Status);
            var ipa = await _session.DownloadIpaAsync(progress, _closing.Token);
            var app = await installer.InstallWithImportedAsync(udid, ipa, progress, _closing.Token);
            if (_session.LastRelease is { } r) IphoneInstaller.SetRelease(IphoneHelper.Paths, udid, r);
            Status($"Done. RigControl Mobile is on your iPhone, signed with your certificate until {app.Expires.LocalDateTime:g}.\n" +
                   "First time only: on the iPhone open Settings > General > VPN & Device Management, tap your developer " +
                   "certificate and tap Trust.");
        });
        await FindPhoneAsync();
    }

    private async Task InstallPresignedAsync()
    {
        var installer = Installer();
        if (installer is null || _phone is null || _busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a signed .ipa to install",
            Filter = "iPhone app (*.ipa)|*.ipa",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var udid = _phone.Udid;
        await Busy("Starting...", async () =>
        {
            var progress = new Progress<string>(Status);
            var app = await installer.InstallPresignedAsync(udid, dialog.FileName, progress, _closing.Token);
            Status($"Done. The app is on your iPhone; its signature runs until {app.Expires.LocalDateTime:g}.");
        });
        await FindPhoneAsync();
    }

    private async Task Busy(string start, Func<Task> work)
    {
        _busy = true;
        Refresh();
        Status(start);
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            Status("Stopped.");
        }
        catch (Exception ex)
        {
            // Every failure ends up here, shown and logged (type and message only: none carry secrets).
            _session.Log("iPhone installer: " + ex.GetType().Name + ": " + ex.Message);
            Status(ex.Message);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void Status(string text) => _status.Text = text;

    /// <summary>Shows and logs an error (type and message only: none carry secrets).</summary>
    public void ShowError(Exception ex)
    {
        _session.Log("iPhone installer: " + ex.GetType().Name + ": " + ex.Message);
        Status(ex.Message);
    }
}

/// <summary>Asks for a .p12 password; returns it as a char[] the caller wipes, or null when cancelled.</summary>
[SupportedOSPlatform("windows")]
internal static class PasswordDialog
{
    public static char[]? Ask(Window owner, string title)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "The password you set when exporting the certificate. It's used once to open the file and isn't saved.",
        });
        var box = new PasswordBox { Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = new Button { Content = "Open", IsDefault = true, Padding = new Thickness(12, 4, 12, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => box.Focus();
        if (dialog.ShowDialog() != true)
            return null;
        var secure = box.SecurePassword;
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            var chars = new char[secure.Length];
            Marshal.Copy(ptr, chars, 0, chars.Length);
            return chars;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            secure.Dispose();
            box.Clear();
        }
    }
}

/// <summary>Asks for the two-factor code Apple sent, with a way to get it by text message instead.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SecondFactorDialog(Window owner) : ISecondFactorPrompt
{
    public Task<SecondFactorAnswer?> AskAsync(SecondFactorRequest request, CancellationToken cancellationToken) =>
        owner.Dispatcher.InvokeAsync(() => Ask(request)).Task;

    private SecondFactorAnswer? Ask(SecondFactorRequest request)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = "Apple ID verification code",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = request.Kind == SecondFactorKind.Sms
                ? $"Type the 6-digit code Apple texted to {request.Phone?.MaskedNumber ?? "your phone"}."
                : "Type the 6-digit code shown on your other Apple devices (tap Allow there first).",
        });
        var code = new TextBox { MaxLength = 6, FontSize = 20, Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(code);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = new Button { Content = "Verify", IsDefault = true, Padding = new Thickness(12, 4, 12, 4) };
        buttons.Children.Add(ok);
        SecondFactorAnswer? answer = null;
        ok.Click += (_, _) =>
        {
            var digits = new string(code.Text.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length != 6) return;
            answer = SecondFactorAnswer.WithCode(digits);
            dialog.DialogResult = true;
        };
        foreach (var phone in request.Phones.Take(3))
        {
            if (request.Kind == SecondFactorKind.Sms && request.Phone?.Id == phone.Id) continue;
            var sms = new Button { Content = "Text " + phone.MaskedNumber, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            sms.Click += (_, _) => { answer = SecondFactorAnswer.SendSms(phone); dialog.DialogResult = true; };
            buttons.Children.Add(sms);
        }
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => code.Focus();
        return dialog.ShowDialog() == true ? answer : null;
    }
}
