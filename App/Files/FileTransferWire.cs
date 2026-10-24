using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace MouseWithoutBorders.Files;

internal sealed class FileOffer
{
    public string Protocol { get; set; } = "MWB-FILE-1";
    public string Name { get; set; }
    public long Length { get; set; }
}

// Frames and padding are multiples of the existing encrypted clipboard stream's block size.
// This channel uses its established pairing handshake, never a separate unauthenticated port.
internal static class FileTransferWire
{
    internal const long MaximumFileSize = 10L * 1024 * 1024 * 1024;
    internal const int FrameSize = 1024;

    internal static void Validate(FileOffer offer)
    {
        if (offer == null || offer.Protocol != "MWB-FILE-1" || string.IsNullOrWhiteSpace(offer.Name) || offer.Name.Length > 200
            || offer.Name != Path.GetFileName(offer.Name) || offer.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || offer.Name.EndsWith('.') || offer.Name.EndsWith(' ') || offer.Name is "." or ".."
            || Regex.IsMatch(offer.Name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase)
            || offer.Length < 0 || offer.Length > MaximumFileSize)
            throw new InvalidDataException("文件名无效或文件超过 10 GB。文件夹请先压缩为 ZIP。");
    }

    internal static void WriteFrame<T>(Stream output, T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length > FrameSize - 4) throw new InvalidDataException("文件名过长，请缩短文件名。");
        byte[] frame = new byte[FrameSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4); output.Write(frame); output.Flush();
    }

    internal static T ReadFrame<T>(Stream input)
    {
        byte[] frame = new byte[FrameSize]; input.ReadExactly(frame);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length <= 0 || length > FrameSize - 4) throw new InvalidDataException("文件传输响应无效，请确认两端均为 .7 或更新版本。");
        return JsonSerializer.Deserialize<T>(frame.AsSpan(4, length));
    }

    internal static void Send(Stream input, string name, Stream encrypted, Stream decrypted, CancellationToken token, Action<long, long> progress)
    {
        var offer = new FileOffer { Name = name, Length = input.Length }; Validate(offer);
        WriteFrame(encrypted, offer);
        if (ReadFrame<string>(decrypted) != "READY") throw new IOException("对方未接受文件。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyExact(input, encrypted, offer.Length, hash, token, progress);
        int padding = (int)((16 - offer.Length % 16) % 16);
        if (padding > 0) encrypted.Write(new byte[padding]);
        WriteFrame(encrypted, Convert.ToHexString(hash.GetHashAndReset()));
        string reply = ReadFrame<string>(decrypted);
        if (!reply.StartsWith("SAVED:", StringComparison.Ordinal)) throw new IOException("对方没有确认保存完成。");
    }

    internal static string Receive(Stream encrypted, Stream decrypted, string folder, CancellationToken token, Action<long, long> progress)
    {
        var offer = ReadFrame<FileOffer>(decrypted); Validate(offer);
        Directory.CreateDirectory(folder);
        Updates.UpdatePackage.NoReparseAncestors(folder);
        string final = null;
        using var destination = new Core.ReceivedDestinationFile(Path.Combine(folder, offer.Name), File.Delete,
            (temporary, _) => final = CommitWithoutOverwrite(temporary, folder, offer.Name));
        WriteFrame(encrypted, "READY");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyExact(decrypted, destination.Stream, offer.Length, hash, token, progress);
        int padding = (int)((16 - offer.Length % 16) % 16);
        if (padding > 0) decrypted.ReadExactly(new byte[padding]);
        string expected = ReadFrame<string>(decrypted);
        if (!string.Equals(expected, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件完整性校验失败，未保留不完整文件。");
        token.ThrowIfCancellationRequested();
        destination.Complete();
        WriteFrame(encrypted, "SAVED:" + Path.GetFileName(final));
        return final;
    }

    internal static string CommitWithoutOverwrite(string temporary, string folder, string name)
    {
        Validate(new FileOffer { Name = name });
        for (int index = 0; index < 10000; index++)
        {
            string fileName = index == 0 ? name : Path.GetFileNameWithoutExtension(name) + " (" + index + ")" + Path.GetExtension(name);
            string destination = Path.Combine(folder, fileName);
            try { File.Move(temporary, destination, false); return destination; }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
        }
        throw new IOException("同名文件过多，请整理接收文件夹。");
    }

    private static void CopyExact(Stream input, Stream output, long size, IncrementalHash hash, CancellationToken token, Action<long, long> progress)
    {
        byte[] buffer = new byte[65536]; long total = 0;
        while (total < size)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size - total));
            if (read == 0) throw new EndOfStreamException("连接中断或源文件变短，文件未传完。");
            hash.AppendData(buffer, 0, read); output.Write(buffer, 0, read); total += read;
            progress?.Invoke(total, size);
        }
        progress?.Invoke(total, size);
    }
}
