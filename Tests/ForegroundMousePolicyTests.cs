using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
public sealed class ForegroundMousePolicyTests
{
    [TestMethod]
    public void DesktopAndTaskSwitchSurfacesCannotInheritBackgroundGameCapture()
    {
        foreach (string name in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "workerw", "" })
            Assert.IsFalse(ForegroundMousePolicy.Eligible(name, true, false, false), name);
        var desktop = new Rectangle(0, 0, 1920, 1080);
        // The previous geometry-only heuristic accepts these stale game values.
        Assert.IsTrue(GameCaptureTracker.IsCaptured(true, desktop, desktop, desktop));
        Assert.IsFalse(ForegroundMousePolicy.Eligible("WorkerW", true, false, false)
            && GameCaptureTracker.IsCaptured(true, desktop, desktop, desktop));
    }

    [TestMethod]
    public void MinimizedHiddenAndOtherVirtualDesktopGamesDoNotBlockCrossing()
    {
        Assert.IsTrue(ForegroundMousePolicy.Eligible("UnityWndClass", true, false, false));
        Assert.IsFalse(ForegroundMousePolicy.Eligible("UnityWndClass", true, true, false));
        Assert.IsFalse(ForegroundMousePolicy.Eligible("UnityWndClass", false, false, false));
        Assert.IsFalse(ForegroundMousePolicy.Eligible("UnityWndClass", true, false, true));
        var tracker = new GameCaptureTracker();
        tracker.Observe((IntPtr)1, true, 0);
        Assert.IsTrue(tracker.Observe((IntPtr)1, true, 100));
        Assert.IsFalse(tracker.Observe((IntPtr)2, false, 101), "Win+D must release immediately, even if the old global clip/hidden state remains.");
    }

    [TestMethod]
    public void BackgroundFullscreenNotificationCannotTurnWindowedForegroundIntoFullscreen()
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        Assert.IsTrue(ForegroundMousePolicy.CoversMonitor(monitor, monitor));
        Assert.IsFalse(ForegroundMousePolicy.CoversMonitor(new(-1900, 40, 1200, 800), monitor));
        Assert.IsFalse(ForegroundMousePolicy.CoversMonitor(new(-1920, 30, 1920, 1010), monitor), "A maximized window with a titlebar is not fullscreen.");
        Assert.IsFalse(ForegroundMousePolicy.CoversMonitor(new(0, 0, 1920, 1080), monitor));
        Assert.IsFalse(ForegroundMousePolicy.CoversMonitor(Rectangle.Empty, monitor));
    }
}
