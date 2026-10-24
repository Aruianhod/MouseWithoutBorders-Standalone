using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Class;

internal static class ElevationRestart
{
    internal const string Switch = "--restart-elevated";
    private static bool restarting;
    internal static bool IsElevated
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    internal sealed record Request(int ParentId, long ParentStartTicks, string UserSid, Guid Token)
    {
        internal string EventName => @"Local\MouseWithoutBorders-Elevation-" + Token.ToString("N");
    }

    internal static Request Parse(string[] args)
    {
        if (args.Length != 6 || args[1] != Switch ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(args[5], "N", out var token) || token == Guid.Empty)
            throw new ArgumentException("无效的管理员重启请求。");
        _ = new SecurityIdentifier(args[4]);
        return new Request(pid, ticks, args[4], token);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, Request request)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable) };
        foreach (string arg in new[] { Switch, request.ParentId.ToString(CultureInfo.InvariantCulture), request.ParentStartTicks.ToString(CultureInfo.InvariantCulture), request.UserSid, request.Token.ToString("N") })
            start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task WaitForReadyAsync(WaitHandle ready, Func<bool> hasExited, TimeSpan limit)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready.WaitOne(0))
        {
            if (hasExited()) throw new IOException("管理员实例未能接管，当前连接保持运行。");
            if (timeout.Elapsed >= limit) throw new TimeoutException("等待管理员实例超时，当前程序保持运行。");
            await Task.Delay(100);
        }
        if (hasExited()) throw new IOException("管理员实例已经退出，当前连接保持运行。");
    }

    // Runs before settings, sockets and the single-instance check are initialized.
    // The new process never terminates the old one: it signals readiness, then waits.
    internal static void PrepareElevatedStartup(string[] args)
    {
        var request = Parse(args);
        using var identity = WindowsIdentity.GetCurrent();
        if (!IsElevated || identity.User.Value != request.UserSid)
            throw new InvalidOperationException("请使用当前 Windows 账户提升权限。切换到其他管理员账户会使用不同的配对设置，原程序已保留。");
        using var parent = Process.GetProcessById(request.ParentId);
        if (parent.Id == Environment.ProcessId || parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks ||
            !string.Equals(parent.MainModule.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原程序身份校验失败，已取消管理员重启。");
        using var ready = EventWaitHandle.OpenExisting(request.EventName);
        ready.Set();
        if (!parent.WaitForExit(30000)) throw new TimeoutException("原程序未退出，管理员实例已取消启动。");
    }

    internal static async Task RestartAsync(IWin32Window owner = null)
    {
        if (restarting || IsElevated) return;
        restarting = true;
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MWB_STANDALONE_DATA")))
                throw new InvalidOperationException("当前使用自定义配置目录，请从原启动环境手动以管理员身份运行。");
            Setting.Values.SaveSettings();
            using var current = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            var request = new Request(current.Id, current.StartTime.ToUniversalTime().Ticks, identity.User.Value, Guid.NewGuid());
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, request.EventName);
            using var child = Process.Start(CreateStartInfo(Application.ExecutablePath, request)) ?? throw new IOException("无法启动管理员实例。");
            await WaitForReadyAsync(ready, () => child.HasExited, TimeSpan.FromSeconds(15));
            Common.MainForm.Quit(true, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Cancelling the Windows consent dialog leaves the current app connected.
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "管理员重启未完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { restarting = false; }
    }
}
