using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Safety;
using RigControl.Core.Settings;

namespace RigControl.Core.Tests;

public class JsonDefaultsTests
{
    private sealed record Sample(string DisplayName, double Value, float Other, ModuleState State);

    [Fact]
    public void Serialises_camelCase_enums_and_null_for_non_finite_numbers()
    {
        var json = JsonDefaults.Serialize(new Sample("x", double.NaN, float.PositiveInfinity, ModuleState.Degraded));
        Assert.Equal("{\"displayName\":\"x\",\"value\":null,\"other\":null,\"state\":\"degraded\"}", json);
    }

    [Fact]
    public void Finite_numbers_round_trip()
    {
        var json = JsonDefaults.Serialize(new Sample("x", 41.5, 2.25f, ModuleState.Ready));
        var back = JsonDefaults.Deserialize<Sample>(json)!;
        Assert.Equal(41.5, back.Value);
        Assert.Equal(2.25f, back.Other);
        Assert.Equal(ModuleState.Ready, back.State);
    }
}

public class RpcDispatcherTests
{
    private sealed class EchoModule : IRpcModule
    {
        public string Name => "echo";

        public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct) => method switch
        {
            "say" => Task.FromResult<object?>(new { said = RpcArgs.Require<SayArgs>(args).Text }),
            "none" => Task.FromResult<object?>(null),
            "boom" => throw new InvalidOperationException("kaboom"),
            "unsafe" => throw RpcException.Unsafe("too low"),
            "cancel" => throw new OperationCanceledException(),
            _ => throw RpcException.UnknownMethod(Name, method),
        };

        private sealed class SayArgs
        {
            public string? Text { get; set; }
        }
    }

    private static RpcDispatcher Create()
    {
        var d = new RpcDispatcher(NullLog.Instance);
        d.Register(new EchoModule());
        return d;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Routes_a_call_and_echoes_the_id()
    {
        var r = Parse(await Create().HandleAsync("{\"id\":7,\"method\":\"echo.say\",\"params\":{\"text\":\"hi\"}}"));
        Assert.Equal(7, r.GetProperty("id").GetInt32());
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("hi", r.GetProperty("result").GetProperty("said").GetString());
    }

    [Fact]
    public async Task Null_result_is_ok()
    {
        var r = Parse(await Create().HandleAsync("{\"id\":\"a\",\"method\":\"echo.none\"}"));
        Assert.Equal("a", r.GetProperty("id").GetString());
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData("not json", "badRequest")]
    [InlineData("[1]", "badRequest")]
    [InlineData("{\"id\":1}", "badRequest")]
    [InlineData("{\"id\":1,\"method\":\"nodot\"}", "badRequest")]
    [InlineData("{\"id\":1,\"method\":\"ghost.x\"}", "unknownModule")]
    [InlineData("{\"id\":1,\"method\":\"echo.ghost\"}", "unknownMethod")]
    [InlineData("{\"id\":1,\"method\":\"echo.say\"}", "invalidArgs")]
    [InlineData("{\"id\":1,\"method\":\"echo.say\",\"params\":{\"text\":5}}", "invalidArgs")]
    [InlineData("{\"id\":1,\"method\":\"echo.boom\"}", "internal")]
    [InlineData("{\"id\":1,\"method\":\"echo.unsafe\"}", "unsafe")]
    [InlineData("{\"id\":1,\"method\":\"echo.cancel\"}", "cancelled")]
    public async Task Failures_become_error_envelopes_and_never_throw(string request, string expectedCode)
    {
        var r = Parse(await Create().HandleAsync(request));
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, r.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(r.GetProperty("error").GetProperty("message").GetString()));
    }

    [Fact]
    public void Module_names_with_a_dot_are_rejected()
    {
        var d = new RpcDispatcher(NullLog.Instance);
        Assert.Throws<ArgumentException>(() => d.Register(new BadName()));
    }

    private sealed class BadName : IRpcModule
    {
        public string Name => "a.b";
        public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct) => Task.FromResult<object?>(null);
    }
}

public class FanCurveTests
{
    private static CurvePoint P(int t, int d) => new(t, d);

    [Fact]
    public void Interpolates_and_clamps()
    {
        var c = FanCurve.Create(new[] { P(30, 40), P(50, 60), P(70, 100) }, 20, out var err)!;
        Assert.Null(err);
        Assert.Equal(40, c.Evaluate(10));
        Assert.Equal(40, c.Evaluate(30));
        Assert.Equal(50, c.Evaluate(40));
        Assert.Equal(60, c.Evaluate(50));
        Assert.Equal(80, c.Evaluate(60));
        Assert.Equal(100, c.Evaluate(70));
        Assert.Equal(100, c.Evaluate(95));
    }

    [Fact]
    public void Unreadable_temperature_means_full_speed()
    {
        var c = FanCurve.Create(new[] { P(30, 40), P(70, 80) }, 20, out _)!;
        Assert.Equal(100, c.Evaluate(double.NaN));
    }

    [Fact]
    public void Sample_covers_inclusive_range()
    {
        var c = FanCurve.Create(new[] { P(20, 50), P(60, 100) }, 20, out _)!;
        var s = c.Sample(20, 60);
        Assert.Equal(41, s.Length);
        Assert.Equal(50, s[0]);
        Assert.Equal(100, s[^1]);
        for (var i = 1; i < s.Length; i++) Assert.True(s[i] >= s[i - 1]);
    }

    public static IEnumerable<object?[]> BadCurves()
    {
        yield return new object?[] { null };
        yield return new object?[] { new[] { P(30, 50) } };                         // too few points
        yield return new object?[] { new[] { P(30, 50), P(30, 60) } };              // temperature not increasing
        yield return new object?[] { new[] { P(40, 50), P(30, 60) } };              // temperature decreasing
        yield return new object?[] { new[] { P(30, 60), P(50, 50) } };              // duty decreasing
        yield return new object?[] { new[] { P(30, 10), P(50, 60) } };              // below minimum duty (20)
        yield return new object?[] { new[] { P(30, 50), P(50, 101) } };             // above 100
        yield return new object?[] { new[] { P(-1, 50), P(50, 60) } };              // temperature below 0
        yield return new object?[] { new[] { P(30, 50), P(101, 60) } };             // temperature above 100
    }

    [Theory]
    [MemberData(nameof(BadCurves))]
    public void Rejects_unsafe_or_malformed_curves(CurvePoint[]? points)
    {
        var c = FanCurve.Create(points, 20, out var err);
        Assert.Null(c);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Critical_rule_is_enforced_when_requested()
    {
        var pts = new[] { P(30, 40), P(80, 100) };
        Assert.NotNull(FanCurve.Create(pts, 20, out _));
        Assert.Null(FanCurve.Create(pts, 20, out var err, criticalTempC: 60, requiredDutyAtCriticalPercent: 100, enforceCritical: true));
        Assert.Contains("60", err);
        Assert.NotNull(FanCurve.Create(new[] { P(30, 40), P(60, 100) }, 20, out _, 60, 100, true));
    }

    [Theory]
    [InlineData(0, 20, 20)]
    [InlineData(55, 20, 55)]
    [InlineData(140, 20, 100)]
    public void Duty_clamp(int requested, int min, int expected) => Assert.Equal(expected, Duty.Clamp(requested, min));
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class Prefs
    {
        public int Level { get; set; } = 3;
        public string? Name { get; set; }
    }

    [Fact]
    public void Missing_section_returns_defaults_and_values_persist()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path, NullLog.Instance);
        Assert.Equal(3, store.Get<Prefs>("p").Level);

        Assert.True(store.Set("p", new Prefs { Level = 9, Name = "n" }));
        store.Update<Prefs>("p", x => x.Level++);

        var reloaded = new SettingsStore(path, NullLog.Instance).Get<Prefs>("p");
        Assert.Equal(10, reloaded.Level);
        Assert.Equal("n", reloaded.Name);
        Assert.Contains("\"level\"", File.ReadAllText(path));
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_not_fatal()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path, NullLog.Instance);
        Assert.Equal(3, store.Get<Prefs>("p").Level);
        Assert.True(store.Set("p", new Prefs { Level = 1 }));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }
}

public class ModuleHostTests
{
    private sealed class FakeModule : IAppModule
    {
        private readonly Func<Task> _start;
        public FakeModule(string name, Func<Task> start) { Name = name; _start = start; }
        public string Name { get; }
        public string DisplayName => Name.ToUpperInvariant();
        public ModuleStatus Status { get; private set; } = ModuleStatus.NotStarted;
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; init; }

        public async Task StartAsync(ModuleContext context, CancellationToken ct)
        {
            await _start();
            Status = new ModuleStatus(ModuleState.Ready);
        }

        public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct) => Task.FromResult<object?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new InvalidOperationException("dispose failed");
            return ValueTask.CompletedTask;
        }
    }

    private static ModuleContext Ctx() => new(
        new EventBus(), NullLog.Instance,
        new SettingsStore(Path.Combine(Path.GetTempPath(), "rigcontrol-tests-" + Guid.NewGuid().ToString("N") + ".json"), NullLog.Instance),
        new AppEnvironment("d", "l", false, "1.0.0", "x.exe"));

    [Fact]
    public async Task One_failing_module_does_not_stop_the_others()
    {
        var good = new FakeModule("good", () => Task.CompletedTask);
        var bad = new FakeModule("bad", () => throw new InvalidOperationException("no device")) { ThrowOnDispose = true };
        var host = new ModuleHost(NullLog.Instance);
        host.Add(bad);
        host.Add(good);

        await host.StartAllAsync(_ => Ctx(), TimeSpan.FromSeconds(5), CancellationToken.None);

        var info = host.Describe().ToDictionary(i => i.Name);
        Assert.Equal(ModuleState.Ready, info["good"].State);
        Assert.Equal(ModuleState.Failed, info["bad"].State);
        Assert.Equal("no device", info["bad"].Message);

        await host.DisposeAsync(); // must not throw even though "bad" throws on dispose
        Assert.True(good.Disposed);
        Assert.True(bad.Disposed);
    }

    [Fact]
    public async Task A_slow_module_does_not_block_startup()
    {
        var gate = new TaskCompletionSource();
        var slow = new FakeModule("slow", () => gate.Task);
        var host = new ModuleHost(NullLog.Instance);
        host.Add(slow);

        await host.StartAllAsync(_ => Ctx(), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal(ModuleState.Starting, host.Describe()[0].State);

        gate.SetResult();
        for (var i = 0; i < 50 && host.Describe()[0].State != ModuleState.Ready; i++) await Task.Delay(20);
        Assert.Equal(ModuleState.Ready, host.Describe()[0].State);
    }

    [Fact]
    public void EventBus_survives_a_throwing_subscriber()
    {
        var bus = new EventBus();
        var got = 0;
        bus.Published += (_, _) => throw new InvalidOperationException();
        bus.Published += (_, _) => got++;
        bus.Publish("t", null);
        Assert.Equal(1, got);
    }
}
