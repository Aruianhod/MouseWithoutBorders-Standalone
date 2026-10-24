using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MouseWithoutBorders.Sync;

internal sealed record SyncEnvelope(string Json, string Proof);
internal sealed record SyncReply(bool Applied, string Message);
internal static class SyncWire
{
    internal const int FrameSize = 16384;
    internal static void Write<T>(Stream stream, T data)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(data);
        if (json.Length > FrameSize - 4) throw new InvalidDataException("同步设置过大。");
        byte[] frame = new byte[FrameSize];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length); json.CopyTo(frame, 4);
        stream.Write(frame); stream.Flush();
    }
    internal static T Read<T>(Stream stream)
    {
        byte[] frame = new byte[FrameSize]; stream.ReadExactly(frame);
        int size = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (size <= 0 || size > FrameSize - 4) throw new InvalidDataException("同步响应无效。");
        return JsonSerializer.Deserialize<T>(frame.AsSpan(4, size)) ?? throw new InvalidDataException("同步内容为空。");
    }
    private static byte[] Proof(string key, string purpose, string from, string to, string nonce, string json)
    {
        if (string.IsNullOrEmpty(key)) throw new InvalidDataException("尚未配对。");
        byte[] secret = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        try { return HMACSHA256.HashData(secret, JsonSerializer.SerializeToUtf8Bytes(new[] { "MWB-SETTINGS-1", purpose, from.ToUpperInvariant(), to.ToUpperInvariant(), nonce, json })); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    internal static SyncEnvelope Seal<T>(T data, string key, string purpose, string from, string to, string nonce)
    {
        string json = JsonSerializer.Serialize(data);
        return new(json, Convert.ToBase64String(Proof(key, purpose, from, to, nonce, json)));
    }
    internal static T Open<T>(SyncEnvelope envelope, string key, string purpose, string from, string to, string nonce)
    {
        if (envelope == null || envelope.Json == null || envelope.Proof == null || envelope.Json.Length > FrameSize)
            throw new InvalidDataException("同步认证数据无效。");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(envelope.Proof), Proof(key, purpose, from, to, nonce, envelope.Json)))
            throw new InvalidDataException("设置同步认证失败。");
        return JsonSerializer.Deserialize<T>(envelope.Json) ?? throw new InvalidDataException("同步内容为空。");
    }
}
