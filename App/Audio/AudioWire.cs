using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Audio;

internal sealed class AudioHello
{
    public int Version { get; set; } = 1;
    public string Sender { get; set; } = "";
    public string Receiver { get; set; } = "";
    public AudioKind Kind { get; set; }
    public string Nonce { get; set; } = "";
    public void Validate()
    {
        if (Version != 1 || Kind is not (AudioKind.System or AudioKind.Microphone)
            || string.IsNullOrWhiteSpace(Sender) || Sender.Length > 256
            || string.IsNullOrWhiteSpace(Receiver) || Receiver.Length > 256
            || Nonce == null || Nonce.Length != 64)
            throw new InvalidDataException("不支持或无效的音频握手。");
        if (Convert.FromHexString(Nonce).Length != 32) throw new InvalidDataException("无效的握手随机数。");
    }
}

// TLS protects PCM. The existing pairing secret authenticates BOTH parties and is
// bound to the TLS server certificate, nonces, endpoint names and stream kind.
// Accepting the temporary certificate alone NEVER authorizes capture or playback.
internal static class AudioWire
{
    internal const int MaxFrame = 7680; // 20 ms of 48 kHz float32 stereo
    internal static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MWB standalone audio", key, HashAlgorithmName.SHA256);
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        // Windows Schannel cannot use an EphemeralKeySet credential. UserKeySet
        // supplies a temporary user-scoped key container; do not use PersistKeySet
        // and do not install this certificate into any certificate/trust store.
        byte[] pfx = temporary.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    internal static byte[] Proof(string secret, string role, byte[] certificate, byte[] challenge, byte[] hello)
    {
        using var data = new MemoryStream();
        data.Write(Encoding.UTF8.GetBytes("MWB-AUDIO-1/" + role + "\0"));
        data.Write(SHA256.HashData(certificate));
        data.Write(challenge);
        data.Write(hello);
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        try { return HMACSHA256.HashData(key, data.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static async Task AuthenticateSender(SslStream tls, AudioHello hello, string key, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new AuthenticationException("尚未配对。");
        hello.Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        hello.Validate();
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "mwb-audio",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            // The certificate is authenticated by the pairing-secret proof below.
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate != null,
        }, token).ConfigureAwait(false);
        byte[] challenge = new byte[32];
        await tls.ReadExactlyAsync(challenge, token).ConfigureAwait(false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(hello);
        byte[] cert = tls.RemoteCertificate.GetRawCertData();
        await WritePacket(tls, bytes, token).ConfigureAwait(false);
        await tls.WriteAsync(Proof(key, "sender", cert, challenge, bytes), token).ConfigureAwait(false);
        byte[] response = new byte[32];
        await tls.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(response, Proof(key, "receiver", cert, challenge, bytes)))
            throw new AuthenticationException("音频配对验证失败。");
    }

    internal static async Task<AudioHello> AuthenticateReceiver(SslStream tls, X509Certificate2 certificate,
        string key, Func<AudioHello, bool> allow, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new AuthenticationException("尚未配对。");
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, token).ConfigureAwait(false);
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        await tls.WriteAsync(challenge, token).ConfigureAwait(false);
        byte[] bytes = await ReadPacket(tls, 4096, token).ConfigureAwait(false);
        var hello = JsonSerializer.Deserialize<AudioHello>(bytes) ?? throw new InvalidDataException();
        hello.Validate();
        byte[] proof = new byte[32];
        await tls.ReadExactlyAsync(proof, token).ConfigureAwait(false);
        byte[] cert = certificate.RawData;
        if (!CryptographicOperations.FixedTimeEquals(proof, Proof(key, "sender", cert, challenge, bytes)) || !allow(hello))
            throw new AuthenticationException("音频来源未授权或配对码不匹配。");
        await tls.WriteAsync(Proof(key, "receiver", cert, challenge, bytes), token).ConfigureAwait(false);
        return hello;
    }

    internal static async Task WritePacket(Stream stream, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaxFrame) throw new InvalidDataException("音频数据帧过大。");
        // Preserve wire compatibility while emitting one TLS record instead of a tiny header record.
        byte[] packet = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, bytes.Length);
        bytes.CopyTo(packet, 4);
        await stream.WriteAsync(packet, token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadPacket(Stream stream, int maximum, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 0 || size > maximum) throw new InvalidDataException("音频数据帧长度无效。");
        byte[] bytes = new byte[size];
        if (size > 0) await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    internal static void ScaleSamples(byte[] bytes, int channels, int volume)
    {
        if (channels is not (1 or 2) || bytes.Length > MaxFrame || bytes.Length % (4 * channels) != 0 || volume < 0 || volume > 100)
            throw new InvalidDataException("音频样本长度或音量无效。");
        for (int i = 0; i < bytes.Length; i += 4)
        {
            float sample = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i));
            sample = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) * volume / 100f : 0f;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), sample);
        }
    }
}
