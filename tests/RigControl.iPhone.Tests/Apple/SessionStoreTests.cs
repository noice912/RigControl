using System.Text;
using RigControl.iPhone.Apple;

namespace RigControl.iPhone.Tests.Apple;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rc-iphone-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static StoredAppleAccount Sample() => new(
        new AppleSession("someone@example.com", "000777-adsid", "1234", "XCODE-SECRET-TOKEN", new DateTimeOffset(2026, 12, 3, 8, 0, 0, TimeSpan.Zero)),
        "PET-SECRET", new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Round_trip_through_the_protector()
    {
        var protector = new FakeProtector();
        var store = new SessionStore(_folder, protector);
        store.Save(Sample());
        Assert.Equal(1, protector.Protects);

        var back = new SessionStore(_folder, protector).Load();
        // The PET is never handed back (it is not used, and should not linger).
        var expected = Sample() with { PasswordEquivalentToken = null, PasswordEquivalentTokenExpires = null };
        Assert.Equal(expected, back);
        Assert.Equal(expected, store.Load("SOMEONE@example.com "));
        Assert.Null(store.Load("other@example.com"));

        // On disk: only protected bytes, no token text.
        var files = Directory.GetFiles(_folder);
        Assert.Single(files);
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(files[0]));
        Assert.DoesNotContain("XCODE-SECRET-TOKEN", raw);
        Assert.DoesNotContain("PET-SECRET", raw);
        Assert.StartsWith("FAKEPROT", raw);
    }

    [Fact]
    public void Damaged_or_foreign_file_reads_as_absent_and_delete_forgets()
    {
        var store = new SessionStore(_folder, new FakeProtector());
        Assert.Null(store.Load());
        store.Save(Sample());
        File.WriteAllBytes(Path.Combine(_folder, "apple-session.bin"), [1, 2, 3]);
        Assert.Null(store.Load());
        File.WriteAllBytes(Path.Combine(_folder, "apple-session.bin"), new FakeProtector().Protect("{not json"u8.ToArray()));
        Assert.Null(store.Load());

        store.Save(Sample());
        Assert.NotNull(store.Load());
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Usable_checks_the_token_expiry()
    {
        var soon = Sample() with { Session = Sample().Session with { Expires = DateTimeOffset.UtcNow.AddMinutes(30) } };
        Assert.True(soon.SessionUsable(TimeSpan.FromMinutes(5)));
        Assert.False(soon.SessionUsable(TimeSpan.FromHours(1)));
    }
}
