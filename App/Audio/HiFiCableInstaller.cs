using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerToys.Settings.UI.Library;

namespace MouseWithoutBorders.Audio;

// Download the original vendor package on demand; do not redistribute or modify it.
internal static class HiFiCableInstaller
{
    internal const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip";
    internal const string ArchiveHash = "3ECF204BFD8579D36BB918F9856EB1EADDD49C75146F5E8A8F59DCB8375AE89A";
    internal const string SetupHash = "8AD78652B0AC89CC34CE5CEE05F66C9CA8A751FEB4ED06AF34532C996B4DBADD";

    internal static string Prepare(string archive, string destination)
    {
        CableInstaller.Verify(archive, ArchiveHash);
        if (Directory.Exists(destination)) throw new IOException("驱动临时目录已存在。");
        ZipFile.ExtractToDirectory(archive, destination);
        string exe = Path.Combine(destination, "HiFiCableAsioBridgeSetup.exe");
        CableInstaller.Verify(exe, SetupHash);
        return exe;
    }

    internal static ProcessStartInfo StartInfo(string exe) => new(exe)
    {
        UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe),
        // The user completes the original installer. Never guess silent switches or reboot.
        WindowStyle = ProcessWindowStyle.Normal,
    };

    internal static async Task InstallAsync(Action<string> progress)
    {
        string root = Path.Combine(SettingsUtils.DataDirectory, "DriverSetup", "HiFi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "HiFiCableAsioBridgeSetup_v1007.zip");
        progress("正在从 VB-Audio 官网下载 Hi-Fi Cable（约 4 MB，捐赠软件），用于独立的系统声音通道……");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        using (var client = new HttpClient())
        using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = File.Create(archive);
            byte[] buffer = new byte[65536];
            int total = 0, count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                total += count;
                if (total > 10 * 1024 * 1024) throw new IOException("官方驱动下载超过预期大小，已停止。");
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
        }
        string exe = await Task.Run(() => Prepare(archive, Path.Combine(root, "setup")));
        progress("请在 Hi-Fi Cable 官方窗口点击 Install 完成安装。若 Windows 要求重启，请完成后重启；程序会继续配置。不要启动 ASIO Bridge 转发到扬声器。");
        Process process;
        try { process = Process.Start(StartInfo(exe)) ?? throw new IOException("无法启动 Hi-Fi Cable 官方安装程序。"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("你取消了管理员授权，尚未继续配置。"); }
        using (process)
        {
            await process.WaitForExitAsync();
            progress("Hi-Fi Cable 安装程序已结束（返回码 " + process.ExitCode + "），正在检查设备……");
        }
    }
}
