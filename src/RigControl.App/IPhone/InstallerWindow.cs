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
    private readonly TextBlock _device = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _account = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _appleId = new() { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(4) };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(4) };
    private readonly Button _signIn = new() { Content = "Sign in", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _signOut = new() { Content = "Sign out", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _install = new() { Content = "Install RigControl Mobile", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 16, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly StackPanel _signInPanel = new();
    private readonly StackPanel _signedInPanel = new() { Orientation = Orientation.Horizontal };
    private readonly CancellationTokenSource _closing = new();
    private DeviceInfo? _phone;
    private bool _busy;

    public InstallerWindow(InstallerSession session, bool renew)
    {
        _session = session;
        _renew = renew;
        Title = "RigControl - Install on iPhone";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Heading("Your iPhone"));
        root.Children.Add(_device);

        root.Children.Add(Heading("Your Apple ID"));
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "A free Apple ID is enough. Your password and the code Apple sends are passed to Apple only: " +
                   "RigControl never saves them. What's kept (encrypted for your Windows account) is the sign-in Apple " +
                   "gives back and your signing certificate, so the app can be signed again each week.",
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
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

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
        if (_renew && _phone is not null && Installer()?.CanWorkWithoutPassword() == true)
        {
            await InstallAsync();
            if (_status.Text.StartsWith("Done", StringComparison.Ordinal))
                Close();
        }
        else if (_renew)
        {
            Status("RigControl Mobile on your iPhone needs signing again. Sign in with your Apple ID and press Install.");
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
        _signIn.IsEnabled = !_busy;
    }

    private async Task FindPhoneAsync()
    {
        try
        {
            var list = await _session.Devices.ListAsync(_closing.Token);
            _phone = list.FirstOrDefault(d => d.Usb) ?? list.FirstOrDefault();
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
            Status($"Done. RigControl Mobile is on your iPhone, signed until {app.Expires.LocalDateTime:g}. " +
                   "RigControl signs it again before then whenever the iPhone is plugged in.\n" +
                   "First time only: on the iPhone open Settings > General > VPN & Device Management, tap your Apple ID " +
                   "under Developer App and tap Trust.");
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
