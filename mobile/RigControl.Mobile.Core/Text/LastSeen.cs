namespace RigControl.Mobile.Core.Text;

/// <summary>"Online", "Seen 5 min ago", "Seen yesterday"... for the PC list.</summary>
public static class LastSeen
{
    public static string Describe(bool online, DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (online)
            return "Online";
        if (lastSeen is null)
            return "Not seen yet";
        var ago = now - lastSeen.Value;
        if (ago < TimeSpan.FromMinutes(1))
            return "Seen just now";
        if (ago < TimeSpan.FromHours(1))
            return $"Seen {(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromHours(24))
            return $"Seen {(int)ago.TotalHours} h ago";
        if (ago < TimeSpan.FromHours(48))
            return "Seen yesterday";
        return $"Seen {(int)ago.TotalDays} days ago";
    }
}
