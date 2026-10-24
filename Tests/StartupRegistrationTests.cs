using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.UnitTests;

[TestClass][DoNotParallelize]
public sealed class StartupRegistrationTests
{
    [TestMethod]
    public void FirstEnableHandlesBothNativeAndMappedMissingTaskErrors()
    {
        Assert.IsNull(StartupRegistration.ReadOrMissing(() => throw new FileNotFoundException()));
        Assert.IsNull(StartupRegistration.ReadOrMissing(() => throw new System.Runtime.InteropServices.COMException("missing", unchecked((int)0x80070002))));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => StartupRegistration.ReadOrMissing(() => throw new UnauthorizedAccessException()));
        var task = new object();
        Assert.AreSame(task, StartupRegistration.ReadOrMissing(() => task));
    }
    [TestMethod]
    public void LogonTaskUsesCurrentInteractiveUserAndSurvivesBatteryAndLongSessions()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        string executable = @"C:\用户文件 & 工具\无界鼠标\MouseWithoutBorders.exe";
        string xml = StartupRegistration.BuildXml(executable, sid);
        var root = XDocument.Parse(xml).Root!;
        XNamespace ns = StartupRegistration.Schema;
        Assert.AreEqual(sid, root.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!.Element(ns + "UserId")!.Value);
        var principal = root.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.AreEqual(sid, principal.Element(ns + "UserId")!.Value);
        Assert.AreEqual("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        Assert.AreEqual("HighestAvailable", principal.Element(ns + "RunLevel")!.Value);
        Assert.AreEqual(executable, root.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value);
        Assert.AreEqual("PT0S", root.Element(ns + "Settings")!.Element(ns + "ExecutionTimeLimit")!.Value);
        Assert.AreEqual("false", root.Element(ns + "Settings")!.Element(ns + "StopIfGoingOnBatteries")!.Value);
        Assert.IsNull(root.Element(ns + "Triggers")!.Element(ns + "BootTrigger"));
        StartupRegistration.CheckOwned(xml, sid);
        Assert.ThrowsExactly<IOException>(() => StartupRegistration.CheckOwned(xml.Replace("urn:MouseWithoutBordersStandalone:Logon:", "urn:Foreign:"), sid));
        // Ask Windows to validate the actual schema without creating any persistent task.
        StartupRegistration.ValidateXml(xml);
    }
}
