using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using RigControl.Core.Rpc;
using RigControl.Presence;

namespace RigControl.App;

public sealed record AppVolumeDto(int Pid, string Name, int Volume, bool Muted);

public sealed record PanelAudioDto(int Master, bool MasterMuted, IReadOnlyList<AppVolumeDto> Apps);

public sealed record PanelStateDto(bool Enabled, string Hotkey, string? HotkeyProblem, bool DiscordOpen, string? DiscordProblem, bool DiscordSignedIn,
    bool Controller, bool GameBarButtonOff, bool PauseRemotePlay);

/// <summary>
/// RPC for the in-game panel ("overlay" in the UI): per-app volume (Core Audio sessions), media keys, store shortcuts,
/// Phone Link and Discord inside the panel. Ported from RichPresence's overlay widgets.
/// </summary>
internal sealed class GamePanelModule : IRpcModule
{
    private readonly AppServices _services;
    private readonly Func<GamePanel?> _panel;
    private string? _hotkeyProblem;

    public GamePanelModule(AppServices services, Func<GamePanel?> panel)
    {
        _services = services;
        _panel = panel;
    }

    public string Name => "overlay";

    /// <summary>Fixed shortcuts (the page only names one; nothing else can be opened from here).</summary>
    private static readonly Dictionary<string, string> Stores = new()
    {
        ["steam.library"] = "steam://open/games",
        ["steam.bigpicture"] = "steam://open/bigpicture",
        ["steam.friends"] = "steam://open/friends",
        ["xbox.app"] = @"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App",
        ["xbox.cloud"] = "https://www.xbox.com/play",
        ["xbox.profile"] = "https://account.xbox.com/Profile",
        ["ps.remoteplay"] = "psremoteplay",
        ["ps.chiaki"] = "chiaki",
        ["ps.library"] = "https://library.playstation.com/",
        ["ps.store"] = "https://store.playstation.com/",
    };

    public void SetHotkeyProblem(string? problem) => _hotkeyProblem = problem;

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        switch (method)
        {
            case "getState": return State();
            case "getAudio": return Audio();
            case "setVolume": return SetVolume(RpcArgs.Require<VolumeArgs>(args));
            case "mediaKey": MediaKey(RpcArgs.Require<KeyArgs>(args).Key); return null;
            case "open": Open(RpcArgs.Require<OpenArgs>(args).Id); return null;
            case "phone": Phone(RpcArgs.Require<PhoneArgs>(args)); return null;
            case "hide": await OnUi(p => p.Hide()); return null;
            case "openDiscord": await OnUiAsync(p => p.OpenDiscordAsync()); return State();
            case "closeDiscord": await OnUi(p => p.CloseDiscord()); return State();
            case "forgetDiscord": await OnUiAsync(p => p.Discord.SignOutAndForgetAsync()); return State();
            case "setSettings": return await SetSettings(RpcArgs.Require<SettingsArgs>(args));
            default: throw RpcException.UnknownMethod(Name, method);
        }
    }

    private GamePanel Panel() => _panel() ?? throw RpcException.Unavailable("The panel isn't ready yet.");

    private Task OnUi(Action<GamePanel> a) => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => a(Panel())).Task;

    private async Task OnUiAsync(Func<GamePanel, Task> a) =>
        await (await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => a(Panel())).Task).ConfigureAwait(false);

    private PanelStateDto State()
    {
        var s = _services.Settings.Get<GamePanelSettings>(GamePanelSettings.Section);
        var p = _panel();
        return new PanelStateDto(s.Enabled, s.Hotkey, _hotkeyProblem, p?.DiscordOpen ?? false, p?.Discord.Problem, p?.Discord.SignedInProfileExists ?? false,
            s.Controller, s.GameBarButtonOff, s.PauseRemotePlay);
    }

    private async Task<PanelStateDto> SetSettings(SettingsArgs a)
    {
        if (a.Hotkey is { } hk && !GamePanel.TryParseHotkey(hk, out _, out _))
            throw RpcException.InvalidArgs($"\"{hk}\" isn't a shortcut RigControl understands (for example Ctrl+Alt+G).");
        _services.Settings.Update<GamePanelSettings>(GamePanelSettings.Section, s =>
        {
            if (a.Enabled is { } e) s.Enabled = e;
            if (a.Hotkey is { } h) s.Hotkey = h.Trim();
            if (a.Controller is { } c) s.Controller = c;
            if (a.GameBarButtonOff is { } g) s.GameBarButtonOff = g;
            if (a.PauseRemotePlay is { } r) s.PauseRemotePlay = r;
        });
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _hotkeyProblem = _panel()?.ApplySettings());
        return State();
    }

    // ---------------------------------------------------------------- volume

    private static MMDevice Speakers()
    {
        using var en = new MMDeviceEnumerator();
        return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static PanelAudioDto Audio()
    {
        using var dev = Speakers();
        var apps = new List<AppVolumeDto>();
        var sessions = dev.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
            var pid = (int)s.GetProcessID;
            if (pid == 0 || apps.Any(a => a.Pid == pid)) continue;
            string name;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = s.DisplayName is { Length: > 0 } dn && !dn.StartsWith('@') ? dn : p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } fd ? fd : p.ProcessName;
            }
            catch
            {
                continue;
            }
            apps.Add(new AppVolumeDto(pid, name, (int)Math.Round(s.SimpleAudioVolume.Volume * 100), s.SimpleAudioVolume.Mute));
        }
        var ep = dev.AudioEndpointVolume;
        return new PanelAudioDto((int)Math.Round(ep.MasterVolumeLevelScalar * 100), ep.Mute, apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static PanelAudioDto SetVolume(VolumeArgs a)
    {
        using var dev = Speakers();
        var v = Math.Clamp(a.Volume ?? -1, -1, 100);
        if (a.Pid is null or 0)
        {
            if (v >= 0) dev.AudioEndpointVolume.MasterVolumeLevelScalar = v / 100f;
            if (a.Muted is { } m) dev.AudioEndpointVolume.Mute = m;
        }
        else
        {
            var sessions = dev.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if ((int)s.GetProcessID != a.Pid) continue;
                if (v >= 0) s.SimpleAudioVolume.Volume = v / 100f;
                if (a.Muted is { } m) s.SimpleAudioVolume.Mute = m;
            }
        }
        return Audio();
    }

    // ---------------------------------------------------------------- keys, links, phone

    private static void MediaKey(string? key)
    {
        byte vk = key switch
        {
            "prev" => 0xB1,
            "play" => 0xB3,
            "next" => 0xB0,
            _ => throw RpcException.InvalidArgs("Unknown media key."),
        };
        keybd_event(vk, 0, 0x1, UIntPtr.Zero);       // extended key down
        keybd_event(vk, 0, 0x1 | 0x2, UIntPtr.Zero); // up
    }

    private void Open(string? id)
    {
        if (id is null || !Stores.TryGetValue(id, out var target)) throw RpcException.InvalidArgs("Unknown shortcut.");
        System.Windows.Application.Current.Dispatcher.Invoke(() => _panel()?.Hide());
        try
        {
            if (target == "chiaki")
            {
                // chiaki-ng's installer puts it in Program Files; without it, its download page.
                var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"chiaki-ng\chiaki.exe");
                target = System.IO.File.Exists(exe) ? exe : "https://github.com/streetpea/chiaki-ng/releases/latest";
            }
            if (target == "psremoteplay")
            {
                var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Sony\PS Remote Play\RemotePlay.exe");
                target = System.IO.File.Exists(exe) ? exe : "https://remoteplay.dl.playstation.net/remoteplay/";
            }
            if (target.StartsWith("shell:", StringComparison.Ordinal)) Launcher.StartShell(target);
            else Launcher.Start(target);
        }
        catch (Exception ex)
        {
            throw RpcException.Unavailable("Couldn't open that: " + ex.Message);
        }
    }

    private static void Phone(PhoneArgs a)
    {
        var digits = Regex.Replace(a.Number ?? "", @"[^\d+]", "");
        if (digits.Length > 0) digits = (digits[0] == '+' ? "+" : "") + digits.Replace("+", "");
        string link = a.Kind switch
        {
            "call" when digits.Trim('+').Length >= 3 => "tel:" + digits,
            "text" when digits.Trim('+').Length >= 3 => "sms:" + digits,
            "open" => "ms-phone:",
            "call" or "text" => throw RpcException.InvalidArgs("Type a phone number first."),
            _ => throw RpcException.InvalidArgs("Unknown phone action."),
        };
        try { Launcher.StartShell(link); }
        catch (Exception ex) { throw RpcException.Unavailable("Phone Link didn't open: " + ex.Message); }
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private sealed class VolumeArgs { public int? Pid { get; set; } public int? Volume { get; set; } public bool? Muted { get; set; } }
    private sealed class KeyArgs { public string? Key { get; set; } }
    private sealed class OpenArgs { public string? Id { get; set; } }
    private sealed class PhoneArgs { public string? Kind { get; set; } public string? Number { get; set; } }
    private sealed class SettingsArgs
    {
        public bool? Enabled { get; set; }
        public string? Hotkey { get; set; }
        public bool? Controller { get; set; }
        public bool? GameBarButtonOff { get; set; }
        public bool? PauseRemotePlay { get; set; }
    }
}
