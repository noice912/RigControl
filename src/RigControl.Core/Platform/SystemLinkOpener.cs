using System.Diagnostics;

namespace RigControl.Core.Platform;

/// <summary>Opens links and folders with the system's own "open" command. Only https links are ever opened.</summary>
public sealed class SystemLinkOpener : ILinkOpener
{
    public bool OpenHttpsUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        Start(Opener(), uri.AbsoluteUri);
        return true;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false })?.Dispose();
            return;
        }
        Start(Opener(), path);
    }

    private static string Opener() => OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open";

    /// <summary>The target is passed as one argument and never through a shell, so nothing in it can run as a command.</summary>
    private static void Start(string program, string target)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(target);
        Process.Start(psi)?.Dispose();
    }
}
