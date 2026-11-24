using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using AudioController.App;
using AudioController.App.Controls;
using AudioController.App.Hosting;
using AudioController.App.Services;
using AudioController.App.ViewModels;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// RigControl: the audio views and dialogs load inside a foreign <see cref="Application"/> whose resources are only what
/// RigControl merges (<see cref="AudioHost.ResourcesUri"/>), on an STA thread, with a view model over fakes built the
/// way the MainViewModel tests build theirs. Nothing is shown on screen; no device, vault or hotkey is touched.
/// </summary>
public sealed class AudioViewSmokeTests
{
    [Fact]
    public void AudioView_LoadsAndLaysOut_WithOnlyTheMergedAudioResources()
    {
        WpfTestHost.Run(() =>
        {
            var log = new List<string>();
            var vm = HostingFakes.NewViewModel(HostingFakes.NewLibrary(), log);
            try
            {
                var view = new AudioView(vm);
                Assert.Same(vm, view.DataContext);

                // Measure/Arrange applies the control templates, so a StaticResource missing inside one fails here too.
                view.Measure(new Size(1280, 760));
                view.Arrange(new Rect(0, 0, 1280, 760));
                view.UpdateLayout();
                Assert.True(view.ActualWidth > 0);

                // Not in any window and never attached: "Open Effects" must still be harmless.
                view.ShowEffects();
            }
            finally
            {
                vm.Shutdown();
            }
        });
    }

    [Fact]
    public void AudioView_IsTheViewModelsUiService()
    {
        WpfTestHost.Run(() =>
        {
            var vm = HostingFakes.NewViewModel(HostingFakes.NewLibrary());
            try
            {
                IUiService view = new AudioView(vm);
                Assert.IsAssignableFrom<System.Windows.Controls.UserControl>(view);
            }
            finally
            {
                vm.Shutdown();
            }
        });
    }

    [Fact]
    public void SettingsWindow_LoadsWithTheSettingsViewModel_WithoutBeingShown()
    {
        WpfTestHost.Run(() =>
        {
            var log = new List<string>();
            var vm = HostingFakes.NewViewModel(HostingFakes.NewLibrary(), log);
            try
            {
                var window = new SettingsWindow(vm.CreateSettingsViewModel());
                new WindowInteropHelper(window).EnsureHandle(); // runs SourceInitialized (dark title bar) without showing
                Assert.False(window.IsVisible);
                Assert.False(window.OpenEffectsRequested);
                window.Close(); // Closed detaches the settings view model and saves (to the fake vault)
                lock (log) Assert.Contains("vault-save", log);
            }
            finally
            {
                vm.Shutdown();
            }
        });
    }

    [Fact]
    public void ReportWindow_LoadsWithLines_WithoutBeingShown()
    {
        WpfTestHost.Run(() =>
        {
            var lines = new[]
            {
                new ReportLine(true, "kick.wav", null),
                new ReportLine(false, "long.mp3", "Longer than 30 seconds."),
            };
            var window = new ReportWindow("Import", "1 of 2 imported.", lines);
            new WindowInteropHelper(window).EnsureHandle();
            Assert.Equal("Import", window.Title);
            Assert.False(window.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Host_AttachedToAHiddenWindow_RegistersOnItsHandle_AndShutsDownCleanly()
    {
        // RigControl may start hidden in the tray: the window has a handle (EnsureHandle) but was never shown.
        string name = HostingFakes.UniqueMutexName();
        var log = new List<string>();
        WpfTestHost.Run(() =>
        {
            var host = AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, log));
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            host.AttachToWindow(window);

            // The view sits collapsed next to another section, inside the never-shown window.
            var view = Assert.IsType<AudioView>(host.View);
            view.Visibility = Visibility.Collapsed;
            window.Content = view;

            Assert.DoesNotContain("hotkeys are unavailable", host.ViewModel!.StatusMessage ?? "");
            host.Dispose();
            window.Close();
        });

        Assert.True(HostingFakes.MutexIsFree(name));
    }

    [Fact]
    public void FfmpegMissing_DisablesImport_ExactlyAsTheStandaloneApp()
    {
        string name = HostingFakes.UniqueMutexName();
        var host = WpfTestHost.Run(() => AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, new List<string>())));
        try
        {
            Assert.Equal(AudioHostState.Running, host.State);
            Assert.True(host.ViewModel!.FfmpegMissing);
            Assert.False(host.ViewModel.CanImport);
        }
        finally
        {
            WpfTestHost.Run(host.Dispose);
        }
    }

    [Fact]
    public void EveryResourceKeyUsedInTheAudioXaml_Resolves()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "AudioXaml");
        string[] files = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith("AudioView.xaml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("SettingsWindow.xaml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("ReportWindow.xaml", StringComparison.OrdinalIgnoreCase));

        var reference = new Regex(@"\{(?:StaticResource|DynamicResource)\s+(?:ResourceKey=)?(?<key>[^}\s,{]+)\s*\}", RegexOptions.CultureInvariant);
        var element = new Regex(@"<(?:StaticResource|DynamicResource)\s+ResourceKey=""(?<key>[^""]+)""", RegexOptions.CultureInvariant);
        var definition = new Regex(@"x:Key=""(?<key>[^""]+)""", RegexOptions.CultureInvariant);

        var missing = new List<string>();
        int checkedKeys = 0;
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            var local = definition.Matches(text).Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
            var used = reference.Matches(text).Concat(element.Matches(text))
                .Select(m => m.Groups["key"].Value)
                .Distinct(StringComparer.Ordinal);

            foreach (string key in used)
            {
                checkedKeys++;
                if (local.Contains(key)) continue; // defined in the same file (its own Resources)
                bool found = WpfTestHost.Run(() => Application.Current.TryFindResource(key) is not null);
                if (!found) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        // The message panel AudioHost shows when the section is not running.
        foreach (string key in new[] { "TextBrush", "TextDim", "WindowBg" })
        {
            checkedKeys++;
            if (!WpfTestHost.Run(() => Application.Current.TryFindResource(key) is not null)) missing.Add("AudioHost: " + key);
        }

        Assert.True(checkedKeys > 50, "The XAML scan found almost no resource references: " + checkedKeys);
        Assert.Empty(missing);
    }

    [Fact]
    public void AudioResources_ProvideEveryConverterKeyTheStandaloneAppXamlProvided()
    {
        string[] keys =
        {
            "BoolToVis", "InverseBoolToVis", "NullToVis", "InverseNullToVis", "ZeroToVis", "InverseBool", "HexToBrush",
            "HexToForeground", "EqualsMulti",
        };

        WpfTestHost.Run(() =>
        {
            var resources = new ResourceDictionary { Source = AudioHost.ResourcesUri };
            foreach (string key in keys)
                Assert.True(resources.Contains(key), key);

            // The dark theme is merged in (brushes and implicit styles).
            Assert.NotNull(resources["WindowBg"]);
            Assert.True(resources.Contains(typeof(System.Windows.Controls.Button)));
        });
    }

    [Fact]
    public void HotkeyCaptureEvents_AreUnhookedWhenTheHostIsDisposed()
    {
        string name = HostingFakes.UniqueMutexName();
        WpfTestHost.Run(() =>
        {
            var host = AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, new List<string>()));
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            host.AttachToWindow(window);
            host.Dispose();

            var view = host.View;
            Assert.DoesNotContain(CaptureHandlerTargets(), t => ReferenceEquals(t, view));
            window.Close();
        });
    }

    [Fact]
    public void AttachToWindow_HooksHotkeyCapture_ForSuspendAndResume()
    {
        string name = HostingFakes.UniqueMutexName();
        WpfTestHost.Run(() =>
        {
            var host = AudioHost.Start(WpfTestHost.Dispatcher, HostingFakes.Options(name, new List<string>()));
            var view = host.View;
            Assert.DoesNotContain(CaptureHandlerTargets(), t => ReferenceEquals(t, view));

            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            host.AttachToWindow(window);

            // Both static capture events (focus in a hotkey box) reach this view, which suspends/resumes its manager.
            Assert.Contains(CaptureHandlerTargets(nameof(HotkeyCaptureBox.CaptureStarted)), t => ReferenceEquals(t, view));
            Assert.Contains(CaptureHandlerTargets(nameof(HotkeyCaptureBox.CaptureEnded)), t => ReferenceEquals(t, view));

            host.Dispose();
            Assert.DoesNotContain(CaptureHandlerTargets(nameof(HotkeyCaptureBox.CaptureStarted)), t => ReferenceEquals(t, view));
            Assert.DoesNotContain(CaptureHandlerTargets(nameof(HotkeyCaptureBox.CaptureEnded)), t => ReferenceEquals(t, view));
            window.Close();
        });
    }

    /// <summary>The targets of the handlers currently subscribed to HotkeyCaptureBox's static capture event.</summary>
    private static IEnumerable<object?> CaptureHandlerTargets(string eventName = nameof(HotkeyCaptureBox.CaptureStarted))
    {
        var field = typeof(HotkeyCaptureBox).GetField(eventName,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handler = field!.GetValue(null) as Delegate;
        return handler?.GetInvocationList().Select(d => d.Target).ToList() ?? new List<object?>();
    }
}
