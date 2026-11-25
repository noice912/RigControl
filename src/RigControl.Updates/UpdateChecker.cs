using System.Security.Cryptography;

namespace RigControl.Updates;

/// <summary>A newer, correctly signed release.</summary>
public sealed record UpdateOffer(UpdateManifest Manifest, string ExeUrl, string ReleaseUrl, string? Notes);

/// <summary>What a check found: <see cref="Offer"/> is null when this copy is up to date (or there is nothing signed to offer).</summary>
public sealed record UpdateCheckResult(UpdateOffer? Offer, string? LatestVersion, string? Note);

/// <summary>
/// Looks at GitHub's newest release and offers it only when its update.json is signed with RigControl's update key,
/// its version is newer than this one, and its setup file is on the same release with the size the signed text says.
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

    public async Task<UpdateCheckResult> CheckAsync(AppVersion current, CancellationToken ct)
    {
        var release = await _api.GetLatestReleaseAsync(ct).ConfigureAwait(false);
        if (release is null) return new UpdateCheckResult(null, null, "There is no release on GitHub yet.");

        var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.FileName);
        var sigAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateManifest.SignatureFileName);
        if (manifestAsset is null || sigAsset is null)
            return new UpdateCheckResult(null, Clean(release.Tag), "The newest release has no signed update information, so it is not offered.");

        var json = await DownloadSmallAsync(manifestAsset, MaxManifestBytes, ct).ConfigureAwait(false);
        var sig = await DownloadSmallAsync(sigAsset, MaxSignatureBytes, ct).ConfigureAwait(false);
        // The signature is checked on the exact bytes before anything in them is read.
        var manifest = UpdateManifest.ParseSigned(json, System.Text.Encoding.ASCII.GetString(sig), _publicKey);

        if (!string.Equals(release.Tag, manifest.Version.Tag, StringComparison.Ordinal))
            throw new UpdateException("The newest release's tag (" + release.Tag + ") does not match its signed version (" + manifest.Version + "), so it was ignored.");
        if (manifest.Version <= current)
            return new UpdateCheckResult(null, manifest.Version.ToString(), null);

        var exe = release.Assets.FirstOrDefault(a => a.Name == manifest.File)
                  ?? throw new UpdateException("The newest release does not carry " + manifest.File + ".");
        if (exe.Size != manifest.Size)
            throw new UpdateException("The setup file on the newest release is not the size its signed information says.");
        RequireReleaseUrl(exe.DownloadUrl);
        return new UpdateCheckResult(new UpdateOffer(manifest, exe.DownloadUrl, release.HtmlUrl, release.Body), manifest.Version.ToString(), null);
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
        if (asset.Size > maxBytes) throw new UpdateException(asset.Name + " on the newest release is larger than expected, so it was ignored.");
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
            throw new UpdateException("A file on the newest release points somewhere other than RigControl's releases, so it was ignored.");
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
