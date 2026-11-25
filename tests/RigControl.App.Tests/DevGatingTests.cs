using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RigControl.App.Updates;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Updates;

namespace RigControl.App.Tests;

/// <summary>Dev mode exists only for a signed-in GitHub account with push access; the backend refuses everyone else.</summary>
public sealed class DevGatingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-devgate-" + Guid.NewGuid().ToString("N"));

    public DevGatingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private sealed class Fake : HttpMessageHandler
    {
        public bool Push { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.AbsoluteUri;
            var json = url switch
            {
                "https://github.com/login/oauth/access_token" => "{\"access_token\":\"ghu_x\",\"expires_in\":28800,\"refresh_token\":\"ghr_x\"}",
                "https://api.github.com/user" => "{\"login\":\"octo\"}",
                "https://api.github.com/repos/noice912/RigControl" => "{\"permissions\":{\"admin\":false,\"push\":" + (Push ? "true" : "false") + ",\"pull\":true}}",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoKey : IDeviceKey
    {
        public Task<bool> SupportedAsync() => Task.FromResult(true);
        public Task<byte[]> CreateAsync() => throw new InvalidOperationException();
        public Task<byte[]?> OpenAsync() => Task.FromResult<byte[]?>(null);
        public Task<byte[]> SignAsync(byte[] data) => throw new InvalidOperationException();
        public Task DeleteAsync() => Task.CompletedTask;
    }

    private (GitHubServices GitHub, DeveloperModule Dev, Fake Net) Make()
    {
        var log = new FileLog(Path.Combine(_dir, "logs"));
        var env = new AppEnvironment(_dir, _dir, false, "1.1.2", "x.exe");
        var services = new AppServices(env, new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance), new EventBus(),
            new RpcDispatcher(NullLog.Instance), new ModuleHost(NullLog.Instance), log);
        var net = new Fake();
        var github = new GitHubServices(services, net);
        return (github, new DeveloperModule(services, github, new NoKey()), net);
    }

    private static Task SignInAsync(GitHubServices github)
        => github.Account.CompleteSignInAsync(new DeviceCode("dc", "X", "https://github.com/login/device", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMilliseconds(1)), default);

    [Fact]
    public async Task Signed_out_the_dev_calls_answer_like_calls_that_do_not_exist()
    {
        var (github, dev, _) = Make();
        using var _g = github;
        foreach (var method in new[] { "getStatus", "registerPc", "unlock", "publishUpdate", "listReviews" })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => dev.InvokeAsync(method, null, default));
            Assert.Equal(RpcErrorCodes.UnknownMethod, ex.Code);
        }
    }

    [Fact]
    public async Task Signed_in_without_push_access_still_refused()
    {
        var (github, dev, _) = Make();
        using var _g = github;
        await SignInAsync(github);
        Assert.False(await github.Maintainer.RefreshAsync(default));
        var ex = await Assert.ThrowsAsync<RpcException>(() => dev.InvokeAsync("getStatus", null, default));
        Assert.Equal(RpcErrorCodes.UnknownMethod, ex.Code);
    }

    [Fact]
    public async Task With_push_access_dev_mode_appears_and_goes_again_on_sign_out()
    {
        var (github, dev, net) = Make();
        using var _g = github;
        net.Push = true;
        await SignInAsync(github);
        Assert.True(await github.Maintainer.RefreshAsync(default));

        var status = await dev.InvokeAsync("getStatus", null, default);
        var json = JsonSerializer.Serialize(status);
        Assert.Contains("\"enrolled\":false", json);
        Assert.Contains("\"unlocked\":false", json);

        github.Account.SignOut();
        var ex = await Assert.ThrowsAsync<RpcException>(() => dev.InvokeAsync("getStatus", null, default));
        Assert.Equal(RpcErrorCodes.UnknownMethod, ex.Code);
    }
}
