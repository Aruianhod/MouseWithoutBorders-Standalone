using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerToys.Settings.UI.Library;

namespace MouseWithoutBorders.Audio;

internal static class CableInstaller
{
    internal const string ArchiveHash = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    internal const string SetupHash = "734C35DFA6D98F48782A451633CEB471166EC70D60482FD89A1123D0EE3C4F41";
    internal static void Verify(string path, string hash)
    {
        using var file = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("VB-CABLE 安装包校验失败，请重新解压完整程序包。");
    }

    internal static string Prepare(string archive, string destination)
    {
        Verify(archive, ArchiveHash);
        if (Directory.Exists(destination)) throw new IOException("驱动临时目录已存在。");
        ZipFile.ExtractToDirectory(archive, destination);
        string exe = Path.Combine(destination, "VBCABLE_Setup_x64.exe");
        Verify(exe, SetupHash);
        return exe;
    }

    internal static ProcessStartInfo StartInfo(string exe) => new(exe)
    {
        UseShellExecute = true,
        Verb = "runas",
        Arguments = "-i",
        // Keep the vendor UI visible if it needs an installation/reboot response.
        // Never approve UAC, install trust-store certificates or disable protection.
        WindowStyle = ProcessWindowStyle.Normal,
        WorkingDirectory = Path.GetDirectoryName(exe),
    };

    internal static async Task InstallAsync(Action<string> progress)
    {
        string archive = Path.Combine(AppContext.BaseDirectory, "Drivers", "VBCABLE_Driver_Pack45.zip");
        string root = Path.Combine(SettingsUtils.DataDirectory, "DriverSetup", Guid.NewGuid().ToString("N"));
        progress("正在校验并解压官方 VB-CABLE 安装包……");
        string exe = await Task.Run(() => Prepare(archive, root));
        progress("请确认 Windows 管理员提示；若出现 VB-CABLE 安装窗口，请完成安装。程序不会自动重启电脑。");
        Process process;
        try { process = Process.Start(StartInfo(exe)) ?? throw new IOException("无法启动驱动安装程序。"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("你取消了管理员授权，尚未继续配置。"); }
        using (process)
        {
            // Do not terminate a driver installer on timeout or guess that exit code 0
            // means working endpoints. The caller verifies the actual device pair.
            await process.WaitForExitAsync();
            progress("官方安装程序已结束（返回码 " + process.ExitCode + "），正在检查设备……");
        }
    }
}
