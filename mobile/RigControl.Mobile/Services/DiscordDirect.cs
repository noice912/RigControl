using RigControl.Mobile.Core.Music;
#if IOS && DISCORD_SDK
using System.Runtime.InteropServices;
using RigControl.Mobile.Platforms.iOS;
#endif

namespace RigControl.Mobile.Services;

/// <summary>
/// "Sign in with Discord" on the phone: with Discord's official Social SDK the phone sets your Discord status itself,
/// so the Music app song shows with no PC. You approve RigControl in the Discord app (or Discord's page); RigControl
/// never sees your Discord password. The Discord sign-in (refresh token) is kept in this iPhone's Keychain only.
/// iOS pauses apps in the background, so the song is shown while RigControl Mobile is open and cleared when it goes
/// to the background. Only in iPhone builds made with the SDK (DISCORD_SDK) and a Discord app id.
/// </summary>
public sealed class DiscordDirect
{
    private const string RefreshKey = "discord.refresh";
    private readonly DiscordSongGate _gate = new();

    public event EventHandler? Changed;

    /// <summary>The Discord application the status is posted as (set at build time; 0 = not in this build).</summary>
    public static ulong AppId { get; } = ReadAppId();

    public static bool IsAvailable =>
#if IOS && DISCORD_SDK
        AppId != 0;
#else
        false;
#endif

    /// <summary>"off", "signing-in", "connecting", "ready" or "problem".</summary>
    public string State { get; private set; } = "off";

    public string? Message { get; private set; }

    public bool SignedIn => State is "connecting" or "ready";

    private void Set(string state, string? message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ulong ReadAppId()
    {
        var value = typeof(DiscordDirect).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DiscordAppId")?.Value;
        return ulong.TryParse(value, out var id) ? id : 0;
    }

#if IOS && DISCORD_SDK
    private unsafe DiscordSdk.Handle* _client;
    private IDispatcherTimer? _pump;
    private GCHandle _self;
    private int _generation; // bumped by sign-out: steps started before it stop where they are

    /// <summary>Signs in with Discord (opens the Discord app or Discord's page). Returns a sentence for the person.</summary>
    public async Task<string> SignInAsync()
    {
        if (!IsAvailable)
            return "This build of RigControl Mobile can't sign in with Discord.";
        if (State is "signing-in" or "connecting")
            return "Already signing in to Discord.";
        var gen = _generation;
        Set("signing-in", "Approve RigControl in Discord, then come back here.");
        try
        {
            var (pending, verifier) = await MainThread.InvokeOnMainThreadAsync(StartAuthorize);
            var (code, redirect) = await pending.WaitAsync(TimeSpan.FromMinutes(10));
            var exchange = await OnMain(() => StartExchange(code, verifier, redirect, null));
            var (access, refresh) = await exchange.WaitAsync(TimeSpan.FromMinutes(1));
            if (gen != _generation)
                return "Signed out of Discord.";
            await SecureStorage.Default.SetAsync(RefreshKey, refresh);
            await ConnectAsync(access, gen);
            return "Signed in with Discord. Your Music app song shows on Discord while RigControl is open.";
        }
        catch (Exception ex) when (ex is DiscordException or TimeoutException or OperationCanceledException)
        {
            var message = ex is TimeoutException ? "Discord didn't answer in time. Try again." : ex.Message;
            if (gen == _generation)
                Set("problem", message);
            return message;
        }
    }

    /// <summary>Forgets the Discord sign-in on this phone and clears the status.</summary>
    public async Task SignOutAsync()
    {
        Interlocked.Increment(ref _generation);
        SecureStorage.Default.Remove(RefreshKey);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            unsafe
            {
                if (_client is not null)
                {
                    DiscordSdk.Discord_Client_ClearRichPresence(_client);
                    DiscordSdk.Discord_Client_Disconnect(_client);
                }
            }
        });
        _gate.Reset();
        Set("off", "Signed out of Discord on this phone.");
    }

    /// <summary>The app is on screen: reconnect with the kept sign-in (no prompt), if there is one.</summary>
    public async Task ResumeAsync()
    {
        if (!IsAvailable || State is "signing-in" or "connecting" or "ready")
            return;
        string? refresh;
        try { refresh = await SecureStorage.Default.GetAsync(RefreshKey); }
        catch (Exception) { return; } // Keychain unavailable right now (locked): try again next time
        if (string.IsNullOrEmpty(refresh))
            return;
        var gen = _generation;
        Set("connecting", "Connecting to Discord...");
        try
        {
            var exchange = await OnMain(() => StartExchange(null, null, null, refresh));
            var (access, newRefresh) = await exchange.WaitAsync(TimeSpan.FromMinutes(1));
            if (gen != _generation)
                return;
            await SecureStorage.Default.SetAsync(RefreshKey, newRefresh);
            await ConnectAsync(access, gen);
        }
        catch (Exception ex) when (ex is DiscordException or TimeoutException or OperationCanceledException)
        {
            if (gen != _generation)
                return;
            // Only a sign-in Discord itself refuses (revoked, expired) is forgotten; offline keeps it for next time.
            if (ex is DiscordException { Refused: true })
            {
                SecureStorage.Default.Remove(RefreshKey);
                Set("problem", "Discord no longer accepts this phone's sign-in. Sign in with Discord again.");
            }
            else
                Set("problem", "Couldn't connect to Discord (" + (ex is TimeoutException ? "no answer" : ex.Message) + "). It tries again when you open RigControl.");
        }
    }

    /// <summary>The app goes to the background: the status is cleared and the connection closed.</summary>
    public Task PauseAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        unsafe
        {
            if (_client is null)
                return;
            DiscordSdk.Discord_Client_ClearRichPresence(_client);
            DiscordSdk.Discord_RunCallbacks();
            DiscordSdk.Discord_Client_Disconnect(_client);
        }
        _gate.Reset();
        if (State is "ready" or "connecting")
            Set("off", null);
    });

    /// <summary>What the Music app plays (null: nothing). Sent when it changed (see DiscordSongGate).</summary>
    public Task ReportAsync(PhoneMusicTrack? track)
    {
        if (State != "ready")
            return Task.CompletedTask;
        var song = DiscordSong.From(track, DateTimeOffset.UtcNow);
        if (!_gate.ShouldSend(song, DateTimeOffset.UtcNow))
            return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() => Show(song));
    }

    private unsafe void Show(DiscordSong? song)
    {
        if (_client is null)
            return;
        if (song is null)
        {
            DiscordSdk.Discord_Client_ClearRichPresence(_client);
            return;
        }
        DiscordSdk.Handle activity;
        DiscordSdk.Discord_Activity_Init(&activity);
        DiscordSdk.Handle times = default;
        var haveTimes = false;
        try
        {
            DiscordSdk.Discord_Activity_SetType(&activity, DiscordSdk.ActivityListening);
            using (var name = new DiscordSdk.Utf8("Apple Music"))
                DiscordSdk.Discord_Activity_SetName(&activity, name.Value);
            using (var title = new DiscordSdk.Utf8(song.Title))
            {
                var t = title.Value;
                DiscordSdk.Discord_Activity_SetDetails(&activity, &t);
            }
            if (song.Artist is { } artist)
                using (var by = new DiscordSdk.Utf8("by " + artist))
                {
                    var a = by.Value;
                    DiscordSdk.Discord_Activity_SetState(&activity, &a);
                }
            var display = DiscordSdk.DisplayDetails;
            DiscordSdk.Discord_Activity_SetStatusDisplayType(&activity, &display);
            if (song.StartMs is { } start && song.EndMs is { } end)
            {
                DiscordSdk.Discord_ActivityTimestamps_Init(&times);
                haveTimes = true;
                DiscordSdk.Discord_ActivityTimestamps_SetStart(&times, (ulong)start);
                DiscordSdk.Discord_ActivityTimestamps_SetEnd(&times, (ulong)end);
                DiscordSdk.Discord_Activity_SetTimestamps(&activity, &times);
            }
            DiscordSdk.Discord_Client_UpdateRichPresence(_client, &activity, &OnPresenceSet, &FreeNothing, IntPtr.Zero);
        }
        finally
        {
            if (haveTimes)
                DiscordSdk.Discord_ActivityTimestamps_Drop(&times);
            DiscordSdk.Discord_Activity_Drop(&activity);
        }
    }

    // ---------------------------------------------------------------- the SDK's steps (main thread)

    private unsafe void EnsureClient()
    {
        if (_client is not null)
            return;
        _client = (DiscordSdk.Handle*)NativeMemory.AllocZeroed((nuint)sizeof(DiscordSdk.Handle));
        DiscordSdk.Discord_Client_Init(_client);
        DiscordSdk.Discord_Client_SetApplicationId(_client, AppId);
        _self = GCHandle.Alloc(this);
        DiscordSdk.Discord_Client_SetStatusChangedCallback(_client, &OnStatus, &FreeNothing, GCHandle.ToIntPtr(_self));
        // Callbacks only run inside Discord_RunCallbacks: pump it while the app runs.
        _pump = Application.Current!.Dispatcher.CreateTimer();
        _pump.Interval = TimeSpan.FromMilliseconds(100);
        _pump.Tick += (_, _) => DiscordSdk.Discord_RunCallbacks();
        _pump.Start();
    }

    /// <summary>Runs <paramref name="start"/> on the main thread and hands back the task it started (not awaited there).</summary>
    private static async Task<Task<T>> OnMain<T>(Func<Task<T>> start)
    {
        Task<T>? started = null;
        await MainThread.InvokeOnMainThreadAsync(() => { started = start(); });
        return started!;
    }

    /// <summary>Starts the Discord sign-in (main thread). Returns the answer to wait for, and the PKCE verifier.</summary>
    private unsafe (Task<(string Code, string Redirect)> Pending, string Verifier) StartAuthorize()
    {
        EnsureClient();
        DiscordSdk.Handle verifier, challenge, args;
        DiscordSdk.Discord_Client_CreateAuthorizationCodeVerifier(_client, &verifier);
        DiscordSdk.Discord_AuthorizationCodeVerifier_Challenge(&verifier, &challenge);
        DiscordSdk.DString v;
        DiscordSdk.Discord_AuthorizationCodeVerifier_Verifier(&verifier, &v);
        var verifierText = DiscordSdk.TakeString(v);
        DiscordSdk.DString scopes;
        DiscordSdk.Discord_Client_GetDefaultPresenceScopes(&scopes);
        var scopeText = DiscordSdk.TakeString(scopes);

        var done = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscordSdk.Discord_AuthorizationArgs_Init(&args);
        try
        {
            DiscordSdk.Discord_AuthorizationArgs_SetClientId(&args, AppId);
            using (var sc = new DiscordSdk.Utf8(scopeText))
                DiscordSdk.Discord_AuthorizationArgs_SetScopes(&args, sc.Value);
            DiscordSdk.Discord_AuthorizationArgs_SetCodeChallenge(&args, &challenge);
            DiscordSdk.Discord_Client_Authorize(_client, &args, &OnAuthorized, &FreeHandle, GCHandle.ToIntPtr(GCHandle.Alloc(done)));
        }
        finally
        {
            DiscordSdk.Discord_AuthorizationArgs_Drop(&args);
            DiscordSdk.Discord_AuthorizationCodeChallenge_Drop(&challenge);
            DiscordSdk.Discord_AuthorizationCodeVerifier_Drop(&verifier);
        }
        return (done.Task, verifierText);
    }

    /// <summary>Starts a new sign-in from a code, or a fresh one from the kept refresh token (main thread).</summary>
    private unsafe Task<(string Access, string Refresh)> StartExchange(string? code, string? verifier, string? redirect, string? refresh)
    {
        EnsureClient();
        var done = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ud = GCHandle.ToIntPtr(GCHandle.Alloc(done));
        if (refresh is not null)
        {
            using var r = new DiscordSdk.Utf8(refresh);
            DiscordSdk.Discord_Client_RefreshToken(_client, AppId, r.Value, &OnToken, &FreeHandle, ud);
        }
        else
        {
            using var c = new DiscordSdk.Utf8(code!);
            using var v = new DiscordSdk.Utf8(verifier!);
            using var u = new DiscordSdk.Utf8(redirect!);
            DiscordSdk.Discord_Client_GetToken(_client, AppId, c.Value, v.Value, u.Value, &OnToken, &FreeHandle, ud);
        }
        return done.Task;
    }

    /// <summary>Starts handing the sign-in to the SDK (main thread). Null answer: accepted.</summary>
    private unsafe Task<DiscordSdk.Problem?> StartUpdateToken(string access)
    {
        var updated = new TaskCompletionSource<DiscordSdk.Problem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var t = new DiscordSdk.Utf8(access))
            DiscordSdk.Discord_Client_UpdateToken(_client, DiscordSdk.TokenBearer, t.Value, &OnTokenSet, &FreeHandle,
                GCHandle.ToIntPtr(GCHandle.Alloc(updated)));
        return updated.Task;
    }

    private async Task ConnectAsync(string access, int gen)
    {
        var pending = await OnMain(() => StartUpdateToken(access));
        if (await pending.WaitAsync(TimeSpan.FromSeconds(30)) is { } problem)
            throw new DiscordException(problem.Message, problem.Refused);
        if (gen != _generation)
            return; // signed out meanwhile: don't connect
        Set("connecting", "Connecting to Discord...");
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            unsafe
            {
                if (gen == _generation)
                    DiscordSdk.Discord_Client_Connect(_client);
            }
        });
    }

    // ---------------------------------------------------------------- callbacks (inside Discord_RunCallbacks)

    [UnmanagedCallersOnly]
    private static unsafe void OnAuthorized(DiscordSdk.Handle* result, DiscordSdk.DString code, DiscordSdk.DString redirect, IntPtr ud)
    {
        var problem = DiscordSdk.TakeResult(result);
        var c = DiscordSdk.TakeString(code);
        var r = DiscordSdk.TakeString(redirect);
        if (GCHandle.FromIntPtr(ud).Target is TaskCompletionSource<(string, string)> done)
        {
            if (problem is null && c.Length > 0) done.TrySetResult((c, r));
            else done.TrySetException(new DiscordException(problem?.Message ?? "Discord sign-in was cancelled.", refused: false));
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnToken(DiscordSdk.Handle* result, DiscordSdk.DString access, DiscordSdk.DString refresh, int type, int expires,
        DiscordSdk.DString scopes, IntPtr ud)
    {
        var problem = DiscordSdk.TakeResult(result);
        var a = DiscordSdk.TakeString(access);
        var r = DiscordSdk.TakeString(refresh);
        DiscordSdk.TakeString(scopes);
        if (GCHandle.FromIntPtr(ud).Target is TaskCompletionSource<(string, string)> done)
        {
            if (problem is null && a.Length > 0) done.TrySetResult((a, r));
            else done.TrySetException(new DiscordException(problem?.Message ?? "Discord didn't give a sign-in.", problem?.Refused ?? false));
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnTokenSet(DiscordSdk.Handle* result, IntPtr ud)
    {
        var problem = DiscordSdk.TakeResult(result);
        if (GCHandle.FromIntPtr(ud).Target is TaskCompletionSource<DiscordSdk.Problem?> done)
            done.TrySetResult(problem);
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnPresenceSet(DiscordSdk.Handle* result, IntPtr ud)
    {
        var problem = DiscordSdk.TakeResult(result);
        if (problem is not null)
            System.Diagnostics.Debug.WriteLine("[discord] status not set: " + problem.Message);
    }

    [UnmanagedCallersOnly]
    private static void OnStatus(int status, int error, int detail, IntPtr ud)
    {
        if (GCHandle.FromIntPtr(ud).Target is not DiscordDirect me)
            return;
        if (status == DiscordSdk.StatusReady)
        {
            me._gate.Reset();
            me.Set("ready", "Signed in with Discord: your Music app song shows on Discord while RigControl is open.");
        }
        else if (status == DiscordSdk.StatusDisconnected && error != 0 && me.State is "connecting" or "ready")
            me.Set("problem", $"Discord disconnected (error {error}). It reconnects when you open RigControl again.");
    }

    [UnmanagedCallersOnly]
    private static void FreeHandle(IntPtr ud)
    {
        if (ud != IntPtr.Zero)
            GCHandle.FromIntPtr(ud).Free();
    }

    [UnmanagedCallersOnly]
    private static void FreeNothing(IntPtr ud)
    {
    }

    private sealed class DiscordException(string message, bool refused) : Exception(message)
    {
        public bool Refused { get; } = refused;
    }
#else
    public Task<string> SignInAsync() => Task.FromResult("This build of RigControl Mobile can't sign in with Discord.");
    public Task SignOutAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ReportAsync(PhoneMusicTrack? track) => Task.CompletedTask;
#endif
}
