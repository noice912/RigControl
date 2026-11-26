using System.Runtime.CompilerServices;

namespace RigControl.App;

/// <summary>
/// RigControl's own entry point (the csproj names it as StartupObject instead of WPF's generated one), so the
/// native-folder check runs before WPF or WebView2 load anything (see <see cref="NativeFolderGuard"/>).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // No WPF or WebView2 type may be touched before this line: their native DLLs would load from wherever .NET
        // unpacked them, which is what the check is about.
        if (NativeFolderGuard.Check(args) is { } exitCode) return exitCode;
        return RunWpf();
    }

    /// <summary>Kept out of Main (and never inlined into it) so WPF's assemblies only load once the check has passed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWpf()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
