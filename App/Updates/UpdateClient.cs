using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.PowerToys.Settings.UI.Library;

namespace MouseWithoutBorders.Updates;

internal sealed class UpdateOptions
{
    public string Folder { get; set; } = string.Empty;
    public bool AutoCheck { get; set; } = false;
    internal static UpdateOptions Load()
    {
        string file = Path.Combine(SettingsUtils.DataDirectory, "updates.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<UpdateOptions>(File.ReadAllBytes(file)) ?? new() : new();
    }
    internal void Save()
    {
        Directory.CreateDirectory(SettingsUtils.DataDirectory);
        new SettingsUtils().SaveSettings(JsonSerializer.Serialize(this, SettingsUtils.JsonOptions), "", "updates.json");
    }
}

internal sealed record UpdateCandidate(ReleaseInfo Release, byte[] Envelope, string Folder);

internal static class UpdateClient
{
    internal static Version CurrentVersion => typeof(UpdateClient).Assembly.GetName().Version;
    private static System.Windows.Forms.Timer timer;
    private static bool checking;
    private static string notified;

    internal static async Task<UpdateCandidate> CheckAsync(string folder, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("请输入共享文件夹或本机文件夹的完整路径。");
        return await Task.Run(async () =>
        {
            using var stream = new FileStream(Path.Combine(folder, "latest.json"), FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (stream.Length > 65536) throw new InvalidDataException("更新清单过大。");
            using var buffer = new MemoryStream();
            await CopyBounded(stream, buffer, 65536, token);
            var bytes = buffer.ToArray();
            return new UpdateCandidate(UpdatePackage.Verify(bytes), bytes, folder);
        }, token).WaitAsync(token);
    }

    internal static async Task<string> DownloadAsync(UpdateCandidate candidate, IProgress<string> progress, CancellationToken token)
    {
        string job = Path.Combine(SettingsUtils.DataDirectory, "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            await Task.Run(async () =>
            {
                using var input = new FileStream(Path.Combine(candidate.Folder, candidate.Release.File), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (input.Length != candidate.Release.Bytes) throw new IOException("更新包大小与清单不符。");
                using (var output = new FileStream(Path.Combine(job, "package.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await CopyBounded(input, output, candidate.Release.Bytes, token, bytes => progress?.Report($"正在下载：{bytes * 100 / candidate.Release.Bytes}%"));
                progress?.Report("正在校验更新包…");
                UpdatePackage.VerifyArchive(Path.Combine(job, "package.zip"), candidate.Release);
                await File.WriteAllBytesAsync(Path.Combine(job, "release.json"), candidate.Envelope, token);
            }, token);
            return job;
        }
        catch
        {
            // Delete only the known incomplete download, never the user's installation or settings.
            try { File.Delete(Path.Combine(job, "package.zip")); } catch { }
            throw;
        }
    }

    internal static async Task CopyBounded(Stream input, Stream output, long limit, CancellationToken token, Action<long> progress = null)
    {
        byte[] buffer = new byte[81920];
        long total = 0, lastProgress = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > limit) throw new IOException("下载内容超过允许大小。");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (total - lastProgress >= 1024 * 1024) { progress?.Invoke(total); lastProgress = total; }
        }
        progress?.Invoke(total);
    }

    internal static async Task PrepareAndRestartAsync(string job, IProgress<string> progress)
    {
        string install = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        UpdatePackage.ValidateInstall(install, SettingsUtils.DataDirectory);
        using var current = Process.GetCurrentProcess();
        var plan = new UpdatePlan { InstallDirectory = install, DataDirectory = Path.GetFullPath(SettingsUtils.DataDirectory), ParentPid = current.Id, ParentStartUtcTicks = current.StartTime.ToUniversalTime().Ticks };
        string planFile = Path.Combine(job, "plan.json");
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        string helper = Path.Combine(job, "MouseWithoutBorders.Updater.exe");
        File.Copy(Path.Combine(install, "MouseWithoutBorders.Updater.exe"), helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job };
        start.ArgumentList.Add(planFile);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        progress?.Report("正在准备新版本，完成后将自动重启程序…");
        // Do not exit until the helper independently verifies the signature, archive and writable destination.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!File.Exists(Path.Combine(job, "ready")))
        {
            if (File.Exists(Path.Combine(job, "error.txt"))) throw new IOException(File.ReadAllText(Path.Combine(job, "error.txt")));
            if (process.HasExited) throw new IOException("更新助手提前退出，当前程序未受影响。");
            if (DateTime.UtcNow > deadline)
            {
                // This is our own helper, before it has been permitted to change the installation.
                process.Kill();
                throw new IOException("准备更新超时，当前程序保持运行。");
            }
            await Task.Delay(200);
        }
        Core.Common.MainForm.Quit(true, false);
    }

    internal static void StartNotifications(NotifyIcon icon)
    {
        timer = new System.Windows.Forms.Timer { Interval = 15000 };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = 6 * 60 * 60 * 1000;
            if (checking) return;
            checking = true;
            try
            {
                var options = UpdateOptions.Load();
                if (!options.AutoCheck) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var candidate = await CheckAsync(options.Folder, timeout.Token);
                if (UpdatePackage.IsNewer(candidate.Release, CurrentVersion) && notified != candidate.Release.Version)
                {
                    notified = candidate.Release.Version;
                    icon.ShowBalloonTip(8000, "无界鼠标有新版本 " + notified, "右键托盘图标 → 检查更新。确认后下载并重启，保留配对和音频设置。", ToolTipIcon.Info);
                }
            }
            catch { /* An offline share must never interrupt mouse/clipboard/audio sharing. */ }
            finally { checking = false; }
        };
        Application.ApplicationExit += (_, _) => timer.Dispose();
        timer.Start();
    }
}
