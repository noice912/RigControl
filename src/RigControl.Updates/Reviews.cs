using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.Updates;

/// <summary>A review as listed on the Developer page.</summary>
public sealed record Review(long Number, string Url, string? Author, int Stars, string Version, string Text, DateTimeOffset? CreatedAt, string State);

/// <summary>
/// Reviews are GitHub issues on noice912/RigControl: the user's stars and words, the RigControl version, and a hidden
/// marker that says it is a review. Nothing about the PC is added.
/// </summary>
public static class Reviews
{
    public const string Label = "review";
    public const string MarkerPhrase = "rigcontrol-review v1";
    public const int MaxTextLength = 4000;

    private const string Footer = "\n\n---\nRigControl ";
    private static readonly Regex Marker = new(@"<!--\s*rigcontrol-review v1 stars=([1-5]) version=([0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6})\s*-->",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The issue's title and text. Title: "Review: ★★★★☆ &lt;first words&gt;". Throws <see cref="ArgumentException"/> with a plain
    /// message for stars outside 1-5 or an empty or overly long text.
    /// </summary>
    public static (string Title, string Body) Compose(int stars, string? text, AppVersion version)
    {
        if (stars is < 1 or > 5) throw new ArgumentException("Choose 1 to 5 stars.");
        var words = (text ?? "").Replace("\r\n", "\n").Trim();
        if (words.Length == 0) throw new ArgumentException("Write a few words for your review.");
        if (words.Length > MaxTextLength) throw new ArgumentException($"A review can be at most {MaxTextLength} characters.");
        // The user's text can never carry a marker of its own (or hide anything else in an HTML comment).
        words = words.Replace("<!--", "&lt;!--", StringComparison.Ordinal);

        var title = "Review: " + Stars(stars) + " " + FirstWords(words, 50);
        var body = words + Footer + version + "\n" + $"<!-- {MarkerPhrase} stars={stars} version={version} -->";
        return (title, body);
    }

    public static string Stars(int stars) => new string('★', stars) + new string('☆', 5 - stars);

    /// <summary>The review in an issue, or null when the issue carries no valid marker (the last one counts).</summary>
    public static Review? Read(IssueInfo issue)
    {
        var body = issue.Body ?? "";
        var matches = Marker.Matches(body);
        if (matches.Count == 0) return null;
        var m = matches[^1];
        var stars = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var footer = body.LastIndexOf(Footer, StringComparison.Ordinal);
        var text = (footer >= 0 ? body[..footer] : body[..m.Index]).Trim();
        return new Review(issue.Number, issue.HtmlUrl, issue.Author, stars, m.Groups[2].Value, text, issue.CreatedAt, issue.State);
    }

    private static string FirstWords(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= max) return line;
        var cut = line.LastIndexOf(' ', max);
        return (cut > 10 ? line[..cut] : line[..max]).TrimEnd() + "…";
    }
}

/// <summary>What publishing an update did.</summary>
public sealed record PublishResult(string ReleaseUrl, AppVersion Version);

/// <summary>
/// "Publish an update" on the Developer page: signs the setup exe, makes a draft release v&lt;version&gt;, uploads the four
/// files, then publishes it. GitHub decides whether the signed-in account may do this. If anything fails after the draft
/// was made, the draft is deleted again so nothing half-made is ever public.
/// </summary>
public sealed class ReleasePublisher
{
    private readonly GitHubApi _api;
    private readonly string _updatePublicKey;
    private readonly Func<DateTimeOffset> _now;

    public ReleasePublisher(GitHubApi api, string updatePublicKey = TrustedKeys.UpdatePublicKey, Func<DateTimeOffset>? now = null)
    {
        _api = api;
        _updatePublicKey = updatePublicKey;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<PublishResult> PublishAsync(string token, string exePath, AppVersion version, string? notes, string keyPem,
        IProgress<string>? progress, CancellationToken ct)
    {
        using var key = EcSignature.ImportPrivateKeyPem(keyPem);
        // A key that the app does not trust would make an update nobody can install: caught here, before GitHub.
        if (EcSignature.PublicKeyBase64(key) != _updatePublicKey)
            throw new UpdateException("That signing key is not the one RigControl checks updates with, so nothing was published.");

        var folder = Path.Combine(Path.GetTempPath(), "RigControl-publish-" + Convert.ToHexString(RandomBytes()).ToLowerInvariant());
        try
        {
            progress?.Report("Signing");
            var files = ReleaseFiles.Make(exePath, version, key, folder, _now());

            progress?.Report("Making a draft release");
            var body = string.IsNullOrWhiteSpace(notes) ? "RigControl " + version : notes.Trim();
            var draft = await _api.CreateDraftReleaseAsync(token, version.Tag, "RigControl " + version.Tag, body, ct).ConfigureAwait(false);
            try
            {
                foreach (var file in files.All)
                {
                    progress?.Report("Uploading " + Path.GetFileName(file));
                    var type = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "application/vnd.microsoft.portable-executable"
                        : file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json"
                        : "text/plain";
                    await _api.UploadAssetAsync(token, draft, file, type, ct).ConfigureAwait(false);
                }
                progress?.Report("Publishing");
                var url = await _api.PublishReleaseAsync(token, draft.Id, ct).ConfigureAwait(false);
                return new PublishResult(string.IsNullOrEmpty(url) ? draft.HtmlUrl : url, version);
            }
            catch (Exception ex) when (ex is GitHubException or IOException or OperationCanceledException)
            {
                var removed = false;
                try
                {
                    await _api.DeleteReleaseAsync(token, draft.Id, CancellationToken.None).ConfigureAwait(false);
                    removed = true;
                }
                catch
                {
                    // said below
                }
                var what = removed ? " The draft release was deleted again." : " A draft release v" + version + " may be left on GitHub; delete it there before trying again.";
                throw new GitHubException(ex.Message + what, (ex as GitHubException)?.Status, ex);
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* the temp folder is cleaned by Windows in time */ }
        }
    }

    private static byte[] RandomBytes() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
}
