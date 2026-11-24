namespace AudioController.Core.Models;

/// <summary>How a sound slot responds to activation.</summary>
public enum PlayMode
{
    /// <summary>Play once from the start; pressing again layers another instance.</summary>
    OneShot = 0,

    /// <summary>Press to start, press again to stop.</summary>
    Toggle = 1,

    /// <summary>Plays only while the button or hotkey is held down.</summary>
    HoldToPlay = 2,
}
