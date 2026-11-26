namespace RigControl.iPhone.Apple;

/// <summary>Plain text for the error codes Apple's sign-in server gives most often. Apple's own text otherwise.</summary>
internal static class AppleErrors
{
    public static string Describe(long code, string? appleText)
    {
        var plain = code switch
        {
            -20101 or -22406 when IsRepair(appleText) =>
                "Your Apple ID needs attention before it can be used here. Sign in at https://appleid.apple.com " +
                "and accept any new terms or fix what it asks for, then try again.",
            -20101 => "The Apple ID or password is wrong.",
            -22406 =>
                "Your Apple ID needs attention before it can be used here. Sign in at https://appleid.apple.com " +
                "and accept any new terms or fix what it asks for, then try again.",
            -20209 or -20210 =>
                "This Apple ID is locked for security reasons. Unlock it at https://iforgot.apple.com, then try again.",
            -80045 or -80046 =>
                "Apple didn't accept this PC's device identification. Restart RigControl and try again; if it " +
                "keeps happening, this PC's Apple device identification has to be set up again.",
            -21669 => "That verification code is wrong.",
            -22979 or -22981 =>
                "Too many verification codes were requested. Wait a while, then try again.",
            -28248 => "Apple couldn't send a code that way. Choose another way to get the code.",
            -36607 =>
                "Apple's sign-in is unavailable right now. Try again later.",
            _ => null,
        };
        if (plain is not null)
            return plain;
        if (!string.IsNullOrWhiteSpace(appleText))
            return $"Apple refused the sign-in: {Trim(appleText)} (code {code}).";
        return $"Apple refused the sign-in (code {code}).";
    }

    private static bool IsRepair(string? text) =>
        text is not null && (text.Contains("repair", StringComparison.OrdinalIgnoreCase)
            || text.Contains("agreement", StringComparison.OrdinalIgnoreCase)
            || text.Contains("terms", StringComparison.OrdinalIgnoreCase));

    internal static string Trim(string text)
    {
        text = text.Trim();
        return text.Length > 300 ? text[..300] + "..." : text;
    }
}
