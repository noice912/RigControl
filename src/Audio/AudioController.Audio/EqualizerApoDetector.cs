using System.IO;
using AudioController.Core.Equalizer;
using Microsoft.Win32;

namespace AudioController.Audio;

/// <summary>
/// Read-only, best-effort detection of Equalizer APO: whether it is installed (<c>HKLM\SOFTWARE\EqualizerAPO</c>
/// InstallPath / ConfigPath) and which audio endpoints it is attached to — an endpoint whose
/// <c>MMDevices\Audio\{Render|Capture}\{guid}\FxProperties</c> has a <c>{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},N</c>
/// value naming Equalizer APO's pre-mix or post-mix APO (what its Device Selector writes). Never writes the registry,
/// never throws.
/// </summary>
public static class EqualizerApoDetector
{
    private const string ApoKey = @"SOFTWARE\EqualizerAPO";
    private const string MmDevicesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

    /// <summary>True when Equalizer APO's registry key, install folder and config folder all exist.</summary>
    public static bool IsInstalled => Detect().Installed;

    /// <summary>The config folder (holds config.txt), or null when not installed.</summary>
    public static string? ConfigPath => Detect().ConfigPath;

    /// <summary>The install folder (holds DeviceSelector.exe), or null when not installed.</summary>
    public static string? InstallPath => Detect().InstallPath;

    /// <summary>Everything at once: installed?, paths, attached endpoints. Never throws.</summary>
    public static EqualizerApoState Detect()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var apo = hklm.OpenSubKey(ApoKey, writable: false);
            if (apo is null)
            {
                return EqualizerApoState.NotInstalled;
            }

            string? install = Clean(apo.GetValue("InstallPath") as string);
            string? config = Clean(apo.GetValue("ConfigPath") as string)
                             ?? (install is null ? null : Path.Combine(install, "config"));
            bool installed = install is not null && Directory.Exists(install)
                             && config is not null && Directory.Exists(config);
            if (!installed)
            {
                return EqualizerApoState.NotInstalled;
            }

            return new EqualizerApoState
            {
                Installed = true,
                InstallPath = install,
                ConfigPath = config,
                AttachedEndpointIds = ReadAttached(hklm),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return EqualizerApoState.NotInstalled;
        }
    }

    /// <summary>True when Equalizer APO is attached to the endpoint (e.g. <c>{0.0.0.00000000}.{guid}</c>). Never throws.</summary>
    public static bool IsAttachedTo(string? endpointId) => Detect().IsAttachedTo(endpointId);

    /// <summary>The endpoint ids Equalizer APO is attached to (empty when not installed). Never throws.</summary>
    public static IReadOnlyList<string> AttachedEndpoints() => Detect().AttachedEndpointIds;

    /// <summary>Full path of Equalizer APO's Device Selector, or null when it is not there.</summary>
    public static string? DeviceSelectorPath(EqualizerApoState state)
    {
        if (state?.InstallPath is not { } dir)
        {
            return null;
        }

        string path = Path.Combine(dir, "DeviceSelector.exe");
        return File.Exists(path) ? path : null;
    }

    private static IReadOnlyList<string> ReadAttached(RegistryKey hklm)
    {
        var result = new List<string>();
        foreach (var (flow, capture) in new[] { ("Render", false), ("Capture", true) })
        {
            try
            {
                using var flowKey = hklm.OpenSubKey(MmDevicesKey + "\\" + flow, writable: false);
                if (flowKey is null)
                {
                    continue;
                }

                foreach (var name in flowKey.GetSubKeyNames())
                {
                    try
                    {
                        using var fx = flowKey.OpenSubKey(name + "\\FxProperties", writable: false);
                        if (fx is null)
                        {
                            continue;
                        }

                        bool attached = false;
                        foreach (var valueName in fx.GetValueNames())
                        {
                            if (EqualizerApoFx.NamesEqualizerApo(valueName, fx.GetValue(valueName)))
                            {
                                attached = true;
                                break;
                            }
                        }

                        if (attached && EqualizerApoConfig.EndpointIdFor(capture, name) is { } id)
                        {
                            result.Add(id);
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // One unreadable endpoint does not hide the others.
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Best effort.
            }
        }

        return result.AsReadOnly();
    }

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return p.Length == 0 ? null : p;
    }
}
