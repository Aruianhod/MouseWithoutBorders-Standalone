using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using MouseWithoutBorders.Updates;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string job = Path.GetDirectoryName(Path.GetFullPath(args[0]));
        string install = null;
        bool parentExited = false;
        using var updateMutex = new Mutex(false, "Local\\MouseWithoutBorders.Standalone.Update");
        bool ownsMutex = false;
        try
        {
            try { ownsMutex = updateMutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new IOException("已有更新任务正在运行。");
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllBytes(args[0])) ?? throw new IOException("更新计划为空。");
            install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.InstallDirectory));
            UpdatePackage.ValidateInstall(install, plan.DataDirectory);
            if (UpdatePackage.IsInside(job, install)) throw new IOException("更新助手需要在程序目录外运行。");
            UpdatePackage.NoReparseAncestors(job);
            using var parent = Process.GetProcessById(plan.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartUtcTicks
                || !string.Equals(parent.MainModule.FileName, Path.Combine(install, "MouseWithoutBorders.exe"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("原程序进程与更新计划不符。");
            var release = UpdatePackage.Verify(File.ReadAllBytes(Path.Combine(job, "release.json")));
            var current = Version.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(install, "MouseWithoutBorders.dll")).FileVersion);
            if (!UpdatePackage.IsNewer(release, current)) throw new IOException("只允许升级到更高版本。");
            var archive = Path.Combine(job, "package.zip");
            UpdatePackage.VerifyArchive(archive, release);
            string token = Guid.NewGuid().ToString("N");
            string stage = install + ".new-" + token;
            string backup = install + ".previous-" + token;
            // Extraction beside the installation proves parent-folder write access before the app exits.
            UpdatePackage.Extract(archive, stage);
            UpdatePackage.ValidatePayload(stage, release);
            File.WriteAllText(Path.Combine(job, "recovery.txt"), "程序目录：" + install + "\r\n旧版备份：" + backup + "\r\n新版暂存：" + stage);
            File.WriteAllText(Path.Combine(job, "ready"), "ready");
            if (!parent.WaitForExit(120000)) throw new IOException("等待程序退出超时，未替换现有程序。");
            parentExited = true;
            // The clipboard helper exits with its parent; give its file handles time to close.
            Thread.Sleep(1000);
            UpdatePackage.Swap(install, stage, backup, () => StartApp(install));
            File.WriteAllText(Path.Combine(job, "result.txt"), "更新成功：" + release.Version + "\r\n旧版目录：" + backup);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(job, "error.txt"), ex.Message); } catch { }
            if (parentExited && install != null)
            {
                try { StartApp(install); } catch { }
                MessageBoxW(IntPtr.Zero, "更新未完成：" + ex.Message + "\r\n恢复信息保存在：" + job, "无界鼠标更新", 0x10);
            }
            return 1;
        }
        finally { if (ownsMutex) updateMutex.ReleaseMutex(); }
    }

    private static void StartApp(string install)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(install, "MouseWithoutBorders.exe"))
            { WorkingDirectory = install, UseShellExecute = false });
        if (process == null) throw new IOException("无法启动程序。");
    }
}
