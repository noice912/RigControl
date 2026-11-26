using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RigControl.iPhone.Device;

/// <summary>Turns a plain device connection into a TLS one. <see cref="DeviceTls.WrapAsync"/> unless a test swaps it.</summary>
public delegate Task<Stream> TlsWrapper(Stream inner, PairRecord pair, CancellationToken ct);

/// <summary>
/// TLS the way lockdown and SSL services expect it: this PC shows the pair record's host certificate, and the iPhone's
/// certificate is accepted only when it is exactly the one in the pair record (no certificate authorities involved).
/// </summary>
public static class DeviceTls
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    public static async Task<Stream> WrapAsync(Stream inner, PairRecord pair, CancellationToken ct)
    {
        byte[] expected;
        X509Certificate2 host;
        try
        {
            expected = pair.DeviceCertificateDer();
            host = pair.CreateHostCertificate();
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new DeviceException(DeviceErrors.SecureConnectionFailed, e);
        }
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "",
            ClientCertificates = new X509CertificateCollection { host },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => host,
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(expected),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HandshakeTimeout);
        try
        {
            await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            host.Dispose();
            throw new DeviceException(DeviceErrors.SecureConnectionFailed, e);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            host.Dispose();
            throw;
        }
        return new OwnerStream(ssl, host);
    }

    /// <summary>The TLS stream plus the certificate it uses, disposed together.</summary>
    private sealed class OwnerStream(Stream inner, IDisposable owned) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owned.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            owned.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
