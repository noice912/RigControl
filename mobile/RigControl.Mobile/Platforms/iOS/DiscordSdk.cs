#if DISCORD_SDK
using System.Runtime.InteropServices;
using System.Text;

namespace RigControl.Mobile.Platforms.iOS;

/// <summary>
/// The few functions of Discord's Social SDK (C API, discord_partner_sdk.framework, cdiscord.h 1.11) RigControl Mobile
/// uses: sign in with Discord (PKCE, public client), keep the sign-in fresh, connect, and set or clear the status.
/// Every SDK object is a struct holding one pointer; the SDK fills it in (Init / return values) and it is given back
/// with its Drop function. Strings the SDK hands out are freed with Discord_Free. Callbacks run inside
/// Discord_RunCallbacks, which RigControl calls on the main thread.
/// </summary>
internal static unsafe class DiscordSdk
{
    private const string Lib = "__Internal"; // the framework is linked into the app

    [StructLayout(LayoutKind.Sequential)]
    public struct Handle { public IntPtr Opaque; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DString { public byte* Ptr; public nuint Size; }

    public const int TokenBearer = 1;
    public const int ActivityListening = 2;
    public const int DisplayDetails = 2;
    public const int StatusReady = 3;
    public const int StatusDisconnected = 0;

    [DllImport(Lib)] public static extern void Discord_Free(void* ptr);
    [DllImport(Lib)] public static extern void Discord_RunCallbacks();

    [DllImport(Lib)] public static extern void Discord_Client_Init(Handle* self);
    [DllImport(Lib)] public static extern void Discord_Client_Drop(Handle* self);
    [DllImport(Lib)] public static extern void Discord_Client_SetApplicationId(Handle* self, ulong applicationId);
    [DllImport(Lib)] public static extern void Discord_Client_GetDefaultPresenceScopes(DString* returnValue);
    [DllImport(Lib)] public static extern void Discord_Client_CreateAuthorizationCodeVerifier(Handle* self, Handle* returnValue);
    [DllImport(Lib)] public static extern void Discord_Client_Authorize(Handle* self, Handle* args,
        delegate* unmanaged<Handle*, DString, DString, IntPtr, void> callback, delegate* unmanaged<IntPtr, void> free, IntPtr userData);
    [DllImport(Lib)] public static extern void Discord_Client_GetToken(Handle* self, ulong applicationId, DString code, DString codeVerifier,
        DString redirectUri, delegate* unmanaged<Handle*, DString, DString, int, int, DString, IntPtr, void> callback,
        delegate* unmanaged<IntPtr, void> free, IntPtr userData);
    [DllImport(Lib)] public static extern void Discord_Client_RefreshToken(Handle* self, ulong applicationId, DString refreshToken,
        delegate* unmanaged<Handle*, DString, DString, int, int, DString, IntPtr, void> callback,
        delegate* unmanaged<IntPtr, void> free, IntPtr userData);
    [DllImport(Lib)] public static extern void Discord_Client_UpdateToken(Handle* self, int tokenType, DString token,
        delegate* unmanaged<Handle*, IntPtr, void> callback, delegate* unmanaged<IntPtr, void> free, IntPtr userData);
    [DllImport(Lib)] public static extern void Discord_Client_SetStatusChangedCallback(Handle* self,
        delegate* unmanaged<int, int, int, IntPtr, void> callback, delegate* unmanaged<IntPtr, void> free, IntPtr userData);
    [DllImport(Lib)] public static extern void Discord_Client_Connect(Handle* self);
    [DllImport(Lib)] public static extern void Discord_Client_Disconnect(Handle* self);
    [DllImport(Lib)] public static extern void Discord_Client_UpdateRichPresence(Handle* self, Handle* activity,
        delegate* unmanaged<Handle*, IntPtr, void> callback, delegate* unmanaged<IntPtr, void> free, IntPtr userData);
    [DllImport(Lib)] public static extern void Discord_Client_ClearRichPresence(Handle* self);

    [DllImport(Lib)] public static extern void Discord_AuthorizationCodeVerifier_Challenge(Handle* self, Handle* returnValue);
    [DllImport(Lib)] public static extern void Discord_AuthorizationCodeVerifier_Verifier(Handle* self, DString* returnValue);
    [DllImport(Lib)] public static extern void Discord_AuthorizationCodeVerifier_Drop(Handle* self);
    [DllImport(Lib)] public static extern void Discord_AuthorizationCodeChallenge_Drop(Handle* self);
    [DllImport(Lib)] public static extern void Discord_AuthorizationArgs_Init(Handle* self);
    [DllImport(Lib)] public static extern void Discord_AuthorizationArgs_Drop(Handle* self);
    [DllImport(Lib)] public static extern void Discord_AuthorizationArgs_SetClientId(Handle* self, ulong value);
    [DllImport(Lib)] public static extern void Discord_AuthorizationArgs_SetScopes(Handle* self, DString value);
    [DllImport(Lib)] public static extern void Discord_AuthorizationArgs_SetCodeChallenge(Handle* self, Handle* value);

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool Discord_ClientResult_Successful(Handle* self);
    [DllImport(Lib)] public static extern void Discord_ClientResult_Error(Handle* self, DString* returnValue);
    [DllImport(Lib)] public static extern void Discord_ClientResult_Drop(Handle* self);
    [DllImport(Lib)] public static extern int Discord_ClientResult_Type(Handle* self);
    [DllImport(Lib)] public static extern int Discord_ClientResult_Status(Handle* self);

    public const int ErrorHttp = 2, ErrorAuthorizationFailed = 8;

    /// <summary>What went wrong; <see cref="Refused"/>: Discord itself said no (not a network problem).</summary>
    public sealed record Problem(string Message, bool Refused);

    [DllImport(Lib)] public static extern void Discord_Activity_Init(Handle* self);
    [DllImport(Lib)] public static extern void Discord_Activity_Drop(Handle* self);
    [DllImport(Lib)] public static extern void Discord_Activity_SetType(Handle* self, int value);
    [DllImport(Lib)] public static extern void Discord_Activity_SetName(Handle* self, DString value);
    [DllImport(Lib)] public static extern void Discord_Activity_SetDetails(Handle* self, DString* value);
    [DllImport(Lib)] public static extern void Discord_Activity_SetState(Handle* self, DString* value);
    [DllImport(Lib)] public static extern void Discord_Activity_SetStatusDisplayType(Handle* self, int* value);
    [DllImport(Lib)] public static extern void Discord_Activity_SetTimestamps(Handle* self, Handle* value);
    [DllImport(Lib)] public static extern void Discord_ActivityTimestamps_Init(Handle* self);
    [DllImport(Lib)] public static extern void Discord_ActivityTimestamps_Drop(Handle* self);
    [DllImport(Lib)] public static extern void Discord_ActivityTimestamps_SetStart(Handle* self, ulong value);
    [DllImport(Lib)] public static extern void Discord_ActivityTimestamps_SetEnd(Handle* self, ulong value);

    /// <summary>Copies an SDK string and frees it.</summary>
    public static string TakeString(DString s)
    {
        var text = s.Ptr is null ? "" : Encoding.UTF8.GetString(s.Ptr, checked((int)s.Size));
        if (s.Ptr is not null)
            Discord_Free(s.Ptr);
        return text;
    }

    /// <summary>A string the SDK only borrows: UTF-8 in native memory, freed on Dispose.</summary>
    public sealed class Utf8 : IDisposable
    {
        private byte* _ptr;
        public DString Value { get; }

        public Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _ptr = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
            bytes.CopyTo(new Span<byte>(_ptr, bytes.Length));
            Value = new DString { Ptr = _ptr, Size = (nuint)bytes.Length };
        }

        public void Dispose()
        {
            if (_ptr is null)
                return;
            NativeMemory.Free(_ptr);
            _ptr = null;
        }
    }

    /// <summary>The problem in a callback's result (null: it worked), and gives the result back (the callback owns it).</summary>
    public static Problem? TakeResult(Handle* result)
    {
        if (result is null)
            return new Problem("Discord gave no answer.", false);
        Problem? problem = null;
        if (!Discord_ClientResult_Successful(result))
        {
            DString e;
            Discord_ClientResult_Error(result, &e);
            var text = TakeString(e);
            var type = Discord_ClientResult_Type(result);
            var status = type == ErrorHttp ? Discord_ClientResult_Status(result) : 0;
            var refused = type == ErrorAuthorizationFailed || status is 400 or 401;
            problem = new Problem(text.Length > 0 ? text : "Discord said no.", refused);
        }
        Discord_ClientResult_Drop(result);
        return problem;
    }
}
#endif
