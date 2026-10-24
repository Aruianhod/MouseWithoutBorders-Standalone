using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MouseWithoutBorders.Updates;

internal sealed class ReleaseInfo
{
    public string Product { get; set; } = "MouseWithoutBordersStandalone";
    public string Platform { get; set; } = "win-x64";
    public string Version { get; set; }
    public string File { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; }
    public string Notes { get; set; }
}

internal sealed class SignedRelease
{
    public string Payload { get; set; }
    public string Signature { get; set; }
}

internal static class UpdatePackage
{
    internal const string Marker = "mwb-standalone-install.txt";
    internal const long MaxPackageBytes = 512L * 1024 * 1024;
    internal static ReleaseInfo Verify(byte[] envelope, string publicKey = UpdateTrust.PublicKey)
    {
        if (envelope.Length > 65536) throw new InvalidDataException("更新清单过大。");
        var signed = JsonSerializer.Deserialize<SignedRelease>(envelope) ?? throw new InvalidDataException("更新清单为空。");
        byte[] payload = Convert.FromBase64String(signed.Payload ?? "");
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!rsa.VerifyData(payload, Convert.FromBase64String(signed.Signature ?? ""), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新签名不正确，已拒绝更新。");
        var release = JsonSerializer.Deserialize<ReleaseInfo>(payload) ?? throw new InvalidDataException("更新信息为空。");
        if (release.Product != "MouseWithoutBordersStandalone" || release.Platform != "win-x64"
            || !Regex.IsMatch(release.Version ?? "", @"^\d+\.\d+\.\d+\.\d+$") || !Version.TryParse(release.Version, out _)
            || !Regex.IsMatch(release.File ?? "", @"^[A-Za-z0-9][A-Za-z0-9._-]{0,180}\.zip$")
            || !Regex.IsMatch(release.Sha256 ?? "", "^[A-Fa-f0-9]{64}$")
            || release.Bytes <= 0 || release.Bytes > MaxPackageBytes || (release.Notes?.Length ?? 0) > 8000)
            throw new InvalidDataException("更新清单格式或目标平台不正确。");
        return release;
    }

    internal static bool IsNewer(ReleaseInfo release, Version current) => Version.Parse(release.Version) > current;

    internal static void VerifyArchive(string archive, ReleaseInfo release)
    {
        using var stream = System.IO.File.OpenRead(archive);
        if (stream.Length != release.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不完整或校验失败，请重新下载。");
    }

    internal static void Extract(string archive, string destination)
    {
        if (Directory.Exists(destination) || System.IO.File.Exists(destination)) throw new IOException("更新暂存目录已存在。");
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 12000) throw new InvalidDataException("更新包文件过多。");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            string[] parts = name.TrimEnd('/').Split('/');
            if (name.Length == 0 || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(' ') || p.EndsWith('.')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase))
                || !names.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包包含不安全的文件路径。");
            if (parts.Any(p => Regex.IsMatch(p, @"^(settings|audio|audio-quick-setup|updates)\.json(\.bak)?$", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("更新包不得包含用户配置。");
            total = checked(total + entry.Length);
            if (total > 1500L * 1024 * 1024) throw new InvalidDataException("解压后的更新包过大。");
            string target = Path.GetFullPath(Path.Combine(destination, name));
            if (!IsInside(target, destination)) throw new InvalidDataException("更新路径越界。");
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            entry.ExtractToFile(target, false);
        }
    }

    internal static bool IsInside(string path, string parent) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void NoReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新目录不能是符号链接或目录联接。");
    }

    internal static void ValidateInstall(string install, string dataDirectory)
    {
        install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install));
        if (install.StartsWith(@"\\", StringComparison.Ordinal) || install == Path.GetPathRoot(install)
            || install.Equals(Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase) || IsInside(dataDirectory, install))
            throw new IOException("请将程序单独解压到本机文件夹，配置目录需在程序目录之外。");
        NoReparseAncestors(install);
        if (new DriveInfo(Path.GetPathRoot(install)).DriveType == DriveType.Network)
            throw new IOException("请先把程序解压到本机磁盘，再更新。");
        if (!System.IO.File.Exists(Path.Combine(install, "MouseWithoutBorders.exe"))
            || System.IO.File.ReadAllText(Path.Combine(install, Marker)).Trim() != "MouseWithoutBordersStandalone")
            throw new IOException("该目录不是支持自动更新的独立版程序目录。");
    }

    internal static void ValidatePayload(string stage, ReleaseInfo release)
    {
        foreach (string file in new[] { "MouseWithoutBorders.exe", "MouseWithoutBorders.dll", "MouseWithoutBordersHelper.exe", "MouseWithoutBorders.Updater.exe", Marker })
            if (!System.IO.File.Exists(Path.Combine(stage, file))) throw new InvalidDataException("更新包缺少文件：" + file);
        if (System.IO.File.ReadAllText(Path.Combine(stage, Marker)).Trim() != "MouseWithoutBordersStandalone"
            || FileVersionInfo.GetVersionInfo(Path.Combine(stage, "MouseWithoutBorders.dll")).FileVersion != release.Version)
            throw new InvalidDataException("更新包内部版本不符。");
    }

    // Keep the complete previous directory. No recursive deletion is necessary for update or rollback.
    internal static void Swap(string install, string stage, string backup, Action launch, Action beforeActivate = null)
    {
        NoReparseAncestors(install); NoReparseAncestors(stage); NoReparseAncestors(backup);
        if (Directory.Exists(backup)) throw new IOException("备份目录已存在。");
        Directory.Move(install, backup);
        try
        {
            beforeActivate?.Invoke();
            Directory.Move(stage, install);
            launch();
        }
        catch
        {
            if (Directory.Exists(install)) Directory.Move(install, stage);
            Directory.Move(backup, install);
            throw;
        }
    }
}
