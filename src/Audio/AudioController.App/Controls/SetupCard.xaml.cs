using System.Windows.Controls;

namespace AudioController.App.Controls;

/// <summary>
/// The Setup card: Microphone + Headset picks, "EQ everything I hear" (Equalizer APO or the router), warnings, the two status
/// lines and "Also play on" (a copy of the headset's sound on other outputs, each with volume and delay). Bound to
/// <see cref="ViewModels.MainViewModel"/>; shown in the main window and at the top of Settings.
/// </summary>
public partial class SetupCard : UserControl
{
    public SetupCard()
    {
        InitializeComponent();
    }
}
