using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RigControl.App.Install;

/// <summary>
/// The signed-in user's token without administrator rights, for work an elevated RigControl must do with only the
/// user's own rights (deleting their settings folder at uninstall, see <see cref="Uninstaller"/>).
/// </summary>
internal static class LimitedToken
{
    /// <summary>
    /// A token for acting as <paramref name="userSid"/> without administrator rights, or null when there is none to
    /// be had. Tried in order: this elevated process's linked token (Windows hands it out ready for acting as the user
    /// only to programs with "act as part of the operating system" rights, so for an ordinary administrator it is
    /// usually refused), then the token of the user's Windows shell (Explorer), which runs with their normal rights.
    /// Either one is used only when it belongs to that user, can be used to act as them and has no administrator
    /// rights. The caller disposes the handle.
    /// </summary>
    public static SafeAccessTokenHandle? ForUser(string userSid, Action<string> log)
    {
        var sources = new (string Name, Func<SafeAccessTokenHandle?> Get)[] { ("linked token", Linked), ("Windows shell's token", Shell) };
        foreach (var (name, get) in sources)
        {
            SafeAccessTokenHandle? token;
            try { token = get(); }
            catch (Exception ex) { log($"The {name} could not be read: {ex.Message}"); continue; }
            if (token is null) continue;
            if (Problem(token, userSid) is { } problem)
            {
                log($"The {name} is not used: {problem}.");
                token.Dispose();
                continue;
            }
            return token;
        }
        return null;
    }

    /// <summary>Why this token must not be used to act as the user, or null when it may.</summary>
    internal static string? Problem(SafeAccessTokenHandle token, string userSid)
    {
        if (token.IsInvalid) return "it is not a token";
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (!string.Equals(identity.User?.Value, userSid, StringComparison.OrdinalIgnoreCase)) return "it belongs to another user";
        // TokenType = 8 (2 = an impersonation token), TokenImpersonationLevel = 9 (2 = impersonation, 3 = delegation).
        if (!GetTokenInformation(token, 8, out var type, sizeof(int), out _) || type != 2) return "it is not a token for acting as the user";
        if (!GetTokenInformation(token, 9, out var level, sizeof(int), out _) || level < 2) return "it only identifies the user and can't act as them";
        // TokenElevation = 20: non-zero with administrator rights.
        if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _)) return "whether it has administrator rights could not be read";
        return elevated != 0 ? "it has administrator rights" : null;
    }

    private static SafeAccessTokenHandle? Linked()
    {
        using var self = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        // TokenLinkedToken = 19: the other half of a split administrator token.
        if (!GetTokenPointer(self.AccessToken, 19, out var linked, IntPtr.Size, out _) || linked == IntPtr.Zero) return null;
        try { return Duplicate(linked); }
        finally { CloseHandle(linked); }
    }

    private static SafeAccessTokenHandle? Shell()
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0) return null;
        const uint queryLimitedInformation = 0x1000, tokenQuery = 0x0008, tokenDuplicate = 0x0002;
        var process = OpenProcess(queryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, tokenQuery | tokenDuplicate, out var token)) return null;
            try { return Duplicate(token); }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    /// <summary>A copy of the token made for acting as its user (an impersonation token), or null.</summary>
    private static SafeAccessTokenHandle? Duplicate(IntPtr token)
    {
        const uint tokenQuery = 0x0008, tokenImpersonate = 0x0004, tokenDuplicate = 0x0002;
        const int securityImpersonation = 2, tokenImpersonation = 2;
        if (!DuplicateTokenEx(token, tokenQuery | tokenImpersonate | tokenDuplicate, IntPtr.Zero, securityImpersonation, tokenImpersonation, out var copy))
            return null;
        return new SafeAccessTokenHandle(copy);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenPointer(SafeAccessTokenHandle token, int infoClass, out IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr copy);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
