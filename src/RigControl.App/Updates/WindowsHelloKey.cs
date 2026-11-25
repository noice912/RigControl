using System.Runtime.InteropServices;
using RigControl.Updates;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace RigControl.App.Updates;

/// <summary>
/// Dev mode's key: a Windows Hello key named "RigControl.Developer", kept in this PC's TPM for this Windows account.
/// Signing asks the user (PIN, face or finger) every time. The private half never leaves the chip.
/// </summary>
internal sealed class WindowsHelloKey : IDeviceKey
{
    public const string KeyName = "RigControl.Developer";

    public async Task<bool> SupportedAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return false; }
    }

    public async Task<byte[]> CreateAsync()
    {
        using var front = PromptToFront.Start();
        var result = await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.ReplaceExisting);
        if (result.Status != KeyCredentialStatus.Success) throw Failure(result.Status, "registering this PC");
        return PublicKey(result.Credential);
    }

    public async Task<byte[]?> OpenAsync()
    {
        var result = await KeyCredentialManager.OpenAsync(KeyName);
        return result.Status switch
        {
            KeyCredentialStatus.Success => PublicKey(result.Credential),
            KeyCredentialStatus.NotFound => null,
            _ => throw Failure(result.Status, "opening this PC's dev key"),
        };
    }

    public async Task<byte[]> SignAsync(byte[] data)
    {
        var open = await KeyCredentialManager.OpenAsync(KeyName);
        if (open.Status != KeyCredentialStatus.Success) throw Failure(open.Status, "opening this PC's dev key");
        using var front = PromptToFront.Start();
        var signed = await open.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(data));
        if (signed.Status == KeyCredentialStatus.UserCanceled) throw new OperationCanceledException();
        if (signed.Status != KeyCredentialStatus.Success) throw Failure(signed.Status, "checking it is you");
        CryptographicBuffer.CopyToByteArray(signed.Result, out var signature);
        return signature;
    }

    public async Task DeleteAsync()
    {
        try { await KeyCredentialManager.DeleteAsync(KeyName); }
        catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException) { /* there was none */ }
    }

    private static byte[] PublicKey(KeyCredential credential)
    {
        var blob = credential.RetrievePublicKey(CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo);
        CryptographicBuffer.CopyToByteArray(blob, out var spki);
        return spki;
    }

    private static Exception Failure(KeyCredentialStatus status, string doing) => status switch
    {
        KeyCredentialStatus.UserCanceled => new OperationCanceledException("Windows Hello was cancelled."),
        KeyCredentialStatus.SecurityDeviceLocked => new InvalidOperationException("The security chip is locked after too many tries. Wait a while, then try again."),
        KeyCredentialStatus.UserPrefersPassword => new InvalidOperationException("Windows Hello was not used. Set up Windows Hello (a PIN is enough) and try again."),
        _ => new InvalidOperationException("Windows Hello failed while " + doing + " (" + status + ")."),
    };

    /// <summary>
    /// Windows Hello's prompt can open behind RigControl's window. For a few seconds after asking, look for it and bring
    /// it to the front. Only Windows' own credential window is touched.
    /// </summary>
    private sealed class PromptToFront : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public static PromptToFront Start()
        {
            var p = new PromptToFront();
            _ = Task.Run(() => p.Loop());
            return p;
        }

        private async Task Loop()
        {
            try
            {
                for (var i = 0; i < 40 && !_stop.IsCancellationRequested; i++)
                {
                    var hwnd = FindWindow("Credential Dialog Xaml Host", null);
                    if (hwnd != IntPtr.Zero)
                    {
                        SetForegroundWindow(hwnd);
                        return;
                    }
                    await Task.Delay(150, _stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // the prompt is done
            }
        }

        // Only cancelled, never disposed: the search may still be reading the token.
        public void Dispose() => _stop.Cancel();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
