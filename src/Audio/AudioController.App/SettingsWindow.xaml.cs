using System.Windows;
using AudioController.App.ViewModels;

namespace AudioController.App;

/// <summary>
/// Modal settings: the Setup picks (microphone + headset + PC-audio switch), stop-all hotkey, comms toggle, a pointer
/// to the main window's Effects panel and the Equalizer APO pre-amp; the manual routing overrides (virtual-mic output,
/// monitor, System EQ source/output) sit in a collapsed Advanced section.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        Services.DarkTitleBar.Attach(this);
        _vm = vm;
        DataContext = vm;
        Closed += (_, _) =>
        {
            _vm.Detach();
            _vm.Main.SaveNow();
        };
    }

    /// <summary>True when the window was closed with "Open Effects" (the owner then shows its Effects panel).</summary>
    public bool OpenEffectsRequested { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenEffects_Click(object sender, RoutedEventArgs e)
    {
        OpenEffectsRequested = true;
        Close();
    }
}
