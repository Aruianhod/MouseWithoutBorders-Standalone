using System.Diagnostics;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;
using MouseWithoutBorders.Class;
using Forms = System.Windows.Forms;

namespace MouseWithoutBorders.UnitTests;

[TestClass][DoNotParallelize]
public sealed class WindowAndElevationTests
{
    private sealed class CursorProbe : FrmMouseCursor
    {
        internal bool DoesNotActivate => ShowWithoutActivation;
        internal int ExtendedStyle => CreateParams.ExStyle;
        internal IntPtr MouseActivateResult()
        {
            var message = Forms.Message.Create(IntPtr.Zero, 0x21, IntPtr.Zero, IntPtr.Zero);
            WndProc(ref message);
            return message.Result;
        }
    }

    [TestMethod]
    public void SharedCursorCannotTakeGameFocusOnShowOrMouseActivation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var cursor = new CursorProbe();
                Assert.IsTrue(cursor.DoesNotActivate, "Showing the shared cursor must not activate a window above the game.");
                Assert.AreEqual(0x08000000, cursor.ExtendedStyle & 0x08000000, "WS_EX_NOACTIVATE");
                Assert.AreEqual(0x20, cursor.ExtendedStyle & 0x20, "The overlay must let mouse input through.");
                Assert.AreEqual((IntPtr)3, cursor.MouseActivateResult(), "Hover/click activation must also be rejected.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [TestMethod]
    public void ElevationArgumentsRoundTripWithoutShellQuotingOrAccountChanges()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var request = new ElevationRestart.Request(42, DateTime.UtcNow.Ticks, identity.User!.Value, Guid.NewGuid());
        var start = ElevationRestart.CreateStartInfo(@"C:\中文 空格\MouseWithoutBorders.exe", request);
        Assert.IsTrue(start.UseShellExecute);
        Assert.AreEqual("runas", start.Verb);
        Assert.AreEqual(request, ElevationRestart.Parse(new[] { start.FileName }.Concat(start.ArgumentList).ToArray()));
        foreach (var args in new[] {
            new[] { "app", ElevationRestart.Switch },
            new[] { "app", ElevationRestart.Switch, "0", "1", request.UserSid, request.Token.ToString("N") },
            new[] { "app", ElevationRestart.Switch, "42", "1", request.UserSid, Guid.Empty.ToString("N") },
            new[] { "app", "winlogon", "42", "1", request.UserSid, request.Token.ToString("N") } })
            Assert.ThrowsExactly<ArgumentException>(() => ElevationRestart.Parse(args));
    }

    [TestMethod]
    public async Task ElevationHandoffRequiresReadyAndLiveChild()
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
        await Assert.ThrowsExactlyAsync<IOException>(() => ElevationRestart.WaitForReadyAsync(ready, () => true, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => ElevationRestart.WaitForReadyAsync(ready, () => false, TimeSpan.Zero));
        var waiting = ElevationRestart.WaitForReadyAsync(ready, () => false, TimeSpan.FromSeconds(3));
        Assert.IsFalse(waiting.IsCompleted);
        ready.Set();
        await waiting;
        await Assert.ThrowsExactlyAsync<IOException>(() => ElevationRestart.WaitForReadyAsync(ready, () => true, TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public void AudioDialogFromTrayOwnsVisibleSettingsAndKeepsNestedDialogInFront()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var settings = new FrmMatrix();
                Assert.IsFalse(settings.TopMost, "Settings must not stay above its configuration dialogs.");
                var tabs = settings.Controls.Find("tabControlSetting", true).OfType<Forms.TabControl>().Single();
                tabs.SelectedTab = tabs.TabPages.Cast<Forms.TabPage>().Single(page => page.Text == "软件更新");
                Directory.CreateDirectory("test-results");
                // Preview the real tab controls without the settings form's network startup handlers.
                using (var preview = new Forms.Form { ClientSize = settings.ClientSize, Opacity = 0, ShowInTaskbar = false })
                {
                    preview.Controls.Add(tabs);
                    preview.Show();
                    tabs.SelectedTab = tabs.TabPages.Cast<Forms.TabPage>().Single(page => page.Text == "软件更新");
                    preview.PerformLayout();
                    using var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height);
                    preview.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, preview.Width, preview.Height));
                    bitmap.Save("test-results/settings-elevation.png");
                    preview.Controls.Remove(tabs);
                    settings.Controls.Add(tabs);
                }
                using var wizard = new SettingsForm();
                Assert.IsFalse(wizard.TopMost);
                // Use an isolated visible owner without starting the real input/clipboard engine.
                using var owner = new Forms.Form { Opacity = 0, ShowInTaskbar = false };
                owner.Show();
                using var audio = new AudioSettingsForm(new AudioOptions()) { Opacity = 0, ShowInTaskbar = false };
                using var timeout = new Forms.Timer { Interval = 5000 };
                timeout.Tick += (_, _) => audio.Close();
                bool shown = false;
                audio.Shown += (_, _) => audio.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Assert.AreSame(owner, audio.Owner);
                        Assert.IsFalse(IsWindowEnabled(owner.Handle));
                        Assert.AreSame(audio, DialogWindows.FindOwner());
                        using var nested = new Forms.Form { Opacity = 0, ShowInTaskbar = false };
                        nested.Shown += (_, _) => nested.BeginInvoke(new Action(() =>
                        {
                            try { Assert.AreSame(audio, nested.Owner); Assert.IsFalse(IsWindowEnabled(audio.Handle)); }
                            catch (Exception ex) { failure = ex; }
                            finally { nested.Close(); }
                        }));
                        DialogWindows.Show(nested);
                        shown = true;
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { audio.Close(); }
                }));
                timeout.Start();
                DialogWindows.Show(audio);
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.IsTrue(shown);
                Assert.IsTrue(IsWindowEnabled(owner.Handle));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
