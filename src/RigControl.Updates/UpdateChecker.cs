using System.Security.Cryptography;

namespace RigControl.Updates;

/// <summary>Which releases this copy follows. Test is only ever used for a maintainer account (the app decides that).</summary>
public enum UpdateChannel
{
    /// <summary>GitHub's latest release, which never is a draft or a pre-release.</summary>
    Public,

    /// <summary>The newest signed release of all, pre-releases (test builds) included.</summary>
    Test,
}

/// <summary>
/// A correctly signed release to install. <see cref="IsDowngrade"/>: it is OLDER than the running copy (offered only as
/// "switch to the public version" right after the user left the test channel).
/// </summary>
public sealed record UpdateOffer(UpdateManifest Manifest, string ExeUrl, string ReleaseUrl, string? Notes, bool IsDowngrade = false);

/// <summary>What a check found: <see cref="Offer"/> is null when this copy is up to date (or there is nothing signed to offer).</summary>
public sealed record UpdateCheckResult(UpdateOffer? Offer, string? LatestVersion, string? Note);

/// <summary>
/// Looks at RigControl's releases on GitHub and offers one only when its update.json is signed with RigControl's update
/// key, its tag is its signed version, its version is newer than this one (or, for a switch back, the public one), and
/// its setup file is on the same release with the size the signed text says.
/// </summary>
public sealed class UpdateChecker
{
    private const int MaxManifestBytes = 16 * 1024;
    private const int MaxSignatureBytes = 1024;

    private readonly GitHubApi _api;
    private readonly string _publicKey;

    public UpdateChecker(GitHubApi api, string publicKey = TrustedKeys.UpdatePublicKey)
    {
        _api = api;
        _publicKey = publicKey;
    }

    /// <summary>The public channel, newer versions only.</summary>
    public Task<UpdateCheckResult> CheckAsync(AppVersion current, CancellationToken ct)
        => CheckAsync(current, UpdateChannel.Public, switchBack: false, ct);

    /// <param name="switchBack">
    /// Public channel only: the user has just left the test channel, so when the newest public release is OLDER than this
    /// copy (a test build), it is offered as a downgrade. Never set otherwise.
    /// </param>
    public Task<UpdateCheckResult> CheckAsync(AppVersion current, UpdateChannel channel, bool switchBack, CancellationToken ct)
        => channel == UpdateChannel.Test ? CheckTestAsync(current, ct) : CheckPublicAsync(current, switchBack, ct);

    private async Task<UpdateCheckResult> CheckPublicAsync(AppVersion current, bool switchBack, CancellationToken ct)
    {
        var release = await _api.GetLatestReleaseAsync(ct).ConfigureAwait(false);
        if (release is null) return new UpdateCheckResult(null, null, "There is no release on GitHub yet.");
        if (release.Draft || release.Prerelease)
            return new UpdateCheckResult(null, null, "GitHub's newest release is a development build, so it is not offered.");

        var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.FileName);
        var sigAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.SignatureFileName);
        if (manifestAsset is null || sigAsset is null)
        {
            // An older release from before signed updates (v0.1.x) is simply not newer: nothing to say about it.
            if (!switchBack && AppVersion.TryParse(release.Tag, out var tagged) && tagged <= current)
                return new UpdateCheckResult(null, Clean(release.Tag), null);
            return new UpdateCheckResult(null, Clean(release.Tag), "The newest release has no signed update information, so it is not offered.");
        }

        var manifest = await ReadManifestAsync(release, manifestAsset, sigAsset, ct).ConfigureAwait(false);
        if (manifest.Version.IsPreRelease)
            return new UpdateCheckResult(null, null, "GitHub's newest release is a development build (" + manifest.Version + "), so it is not offered.");

        var downgrade = false;
        if (manifest.Version <= current)
        {
            // Only right after leaving the test channel, and only when this copy is newer than the public release.
            if (!switchBack || manifest.Version == current) return new UpdateCheckResult(null, manifest.Version.ToString(), null);
            downgrade = true;
        }
        return new UpdateCheckResult(Offer(release, manifest, downgrade), manifest.Version.ToString(), null);
    }

    /// <summary>
    /// The test channel: the newest correctly signed release of the first page (30), pre-releases included, drafts
    /// skipped. A release that is unsigned, signed with another key or otherwise not right is skipped, never offered.
    /// </summary>
    private async Task<UpdateCheckResult> CheckTestAsync(AppVersion current, CancellationToken ct)
    {
        var releases = await _api.ListReleasesAsync(ct).ConfigureAwait(false);
        // The tag must equal the signed version, so trying them newest tag first finds the newest signed one first.
        var candidates = new List<(ReleaseInfo Release, AppVersion Version)>();
        foreach (var r in releases)
        {
            if (!r.Draft && AppVersion.TryParse(r.Tag, out var v)) candidates.Add((r, v));
        }
        candidates.Sort((a, b) => b.Version.CompareTo(a.Version));
        if (candidates.Count == 0) return new UpdateCheckResult(null, null, "There is no release on GitHub yet.");

        string? skipped = null;
        foreach (var (release, tagged) in candidates)
        {
            // Newest first: once the tags are no newer than this copy, there is nothing newer to look at. The tag is only
            // used to stop looking here; anything offered is still checked against its signed version.
            if (tagged <= current) return new UpdateCheckResult(null, Clean(release.Tag), null);
            var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.FileName);
            var sigAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.SignatureFileName);
            if (manifestAsset is null || sigAsset is null)
            {
                skipped ??= release.Tag + " has no signed update information, so it was skipped.";
                continue;
            }
            UpdateOffer offer;
            try
            {
                var manifest = await ReadManifestAsync(release, manifestAsset, sigAsset, ct).ConfigureAwait(false);
                if (manifest.Version <= current) return new UpdateCheckResult(null, manifest.Version.ToString(), null);
                offer = Offer(release, manifest, downgrade: false);
            }
            catch (UpdateException ex)
            {
                skipped ??= release.Tag + " was skipped: " + ex.Message;
                continue;
            }
            return new UpdateCheckResult(offer, offer.Manifest.Version.ToString(), null);
        }
        return new UpdateCheckResult(null, null, skipped ?? "No release carries signed update information.");
    }

    /// <summary>Downloads update.json and its signature, checks the signature BEFORE reading, then the tag against the signed version.</summary>
    private async Task<UpdateManifest> ReadManifestAsync(ReleaseInfo release, ReleaseAsset manifestAsset, ReleaseAsset sigAsset, CancellationToken ct)
    {
        var json = await DownloadSmallAsync(manifestAsset, MaxManifestBytes, ct).ConfigureAwait(false);
        var sig = await DownloadSmallAsync(sigAsset, MaxSignatureBytes, ct).ConfigureAwait(false);
        // The signature is checked on the exact bytes before anything in them is read.
        var manifest = UpdateManifest.ParseSigned(json, System.Text.Encoding.ASCII.GetString(sig), _publicKey);
        if (!string.Equals(release.Tag, manifest.Version.Tag, StringComparison.Ordinal))
            throw new UpdateException("The release's tag (" + release.Tag + ") does not match its signed version (" + manifest.Version + "), so it was ignored.");
        return manifest;
    }

    /// <summary>The setup file must be on the same release, at RigControl's own release address, with the signed size.</summary>
    private static UpdateOffer Offer(ReleaseInfo release, UpdateManifest manifest, bool downgrade)
    {
        var exe = release.Assets.FirstOrDefault(a => a.Name == manifest.File)
                  ?? throw new UpdateException("The release " + release.Tag + " does not carry " + manifest.File + ".");
        if (exe.Size != manifest.Size)
            throw new UpdateException("The setup file on the release " + release.Tag + " is not the size its signed information says.");
        RequireReleaseUrl(exe.DownloadUrl);
        return new UpdateOffer(manifest, exe.DownloadUrl, release.HtmlUrl, release.Body, downgrade);
    }

    /// <summary>
    /// Downloads the setup file into <paramref name="folder"/>, checking size and SHA-256 against the signed information.
    /// A file that does not match is deleted. Returns the full path of the checked file.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        RequireReleaseUrl(offer.ExeUrl);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, offer.Manifest.File);
        var partial = target + ".download";
        RemoveOldFiles(folder, offer.Manifest.File);

        using var request = new HttpRequestMessage(HttpMethod.Get, offer.ExeUrl);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        HttpResponseMessage response;
        try
        {
            response = await _api.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException("The update could not be downloaded: " + ex.Message, null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GitHubException("The update could not be downloaded (HTTP " + (int)response.StatusCode + ").", response.StatusCode);
            var expected = offer.Manifest.Size;
            if (response.Content.Headers.ContentLength is { } len && len != expected)
                throw new UpdateException("The update GitHub sent is not the size its signed information says.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > expected) throw new UpdateException("The update GitHub sent is larger than its signed information says.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        progress?.Report((double)total / expected);
                    }
                }
                if (total != expected) throw new UpdateException("The update download ended early.");
                var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (sha != offer.Manifest.Sha256)
                    throw new UpdateException("The downloaded update does not match its signed SHA-256, so it was deleted.");
                File.Move(partial, target, overwrite: true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
        return target;
    }

    /// <summary>True when the file still has exactly the signed size and SHA-256 (checked again just before it is started).</summary>
    public static bool StillMatches(string path, UpdateManifest manifest)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == manifest.Size && UpdateManifest.HashFile(path) == manifest.Sha256;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task<byte[]> DownloadSmallAsync(ReleaseAsset asset, int maxBytes, CancellationToken ct)
    {
        RequireReleaseUrl(asset.DownloadUrl);
        if (asset.Size > maxBytes) throw new UpdateException(asset.Name + " on the release is larger than expected, so it was ignored.");
        using var limit = GitHubHttp.Limit(ct, TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        HttpResponseMessage response;
        try
        {
            response = await _api.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException("GitHub could not be reached: " + ex.Message, null, ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GitHubException(asset.Name + " could not be downloaded (HTTP " + (int)response.StatusCode + ").", response.StatusCode);
            return await GitHubHttp.ReadLimitedAsync(response, maxBytes, limit.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Release files are only ever fetched from RigControl's own releases.</summary>
    private static void RequireReleaseUrl(string url)
    {
        if (!url.StartsWith(GitHubHosts.ReleaseDownloadPrefix, StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new UpdateException("A file on the release points somewhere other than RigControl's releases, so it was ignored.");
    }

    /// <summary>Earlier setup files and half-finished downloads are not kept.</summary>
    private static void RemoveOldFiles(string folder, string keep)
    {
        foreach (var pattern in new[] { "RigControl-Setup-v*.exe", "*.download" })
        {
            foreach (var file in Directory.EnumerateFiles(folder, pattern))
            {
                if (string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* next time */ }
    }

    private static string? Clean(string tag) => AppVersion.TryParse(tag, out var v) ? v.ToString() : null;
}
