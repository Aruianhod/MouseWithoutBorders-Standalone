using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace MouseWithoutBorders.Class;

internal static class StartupRegistration
{
    internal sealed record State(bool Enabled, string Executable);
    internal static readonly XNamespace Schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static string Sid { get { using var user = WindowsIdentity.GetCurrent(); return user.User.Value; } }
    private static string Name(string sid) => "MouseWithoutBordersStandalone.Logon." + sid;
    private static string Marker(string sid) => "urn:MouseWithoutBordersStandalone:Logon:" + sid;

    internal static string BuildXml(string executable, string sid)
    {
        _ = new SecurityIdentifier(sid);
        executable = Path.GetFullPath(executable);
        return new XDocument(new XElement(Schema + "Task", new XAttribute("version", "1.2"),
            new XElement(Schema + "RegistrationInfo",
                new XElement(Schema + "Description", Marker(sid) + "\n无界鼠标独立版：当前用户登录后自动启动，可在程序设置中关闭。"),
                new XElement(Schema + "URI", Marker(sid))),
            new XElement(Schema + "Triggers", new XElement(Schema + "LogonTrigger",
                new XElement(Schema + "Enabled", true), new XElement(Schema + "UserId", sid), new XElement(Schema + "Delay", "PT5S"))),
            new XElement(Schema + "Principals", new XElement(Schema + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(Schema + "UserId", sid), new XElement(Schema + "LogonType", "InteractiveToken"), new XElement(Schema + "RunLevel", "HighestAvailable"))),
            new XElement(Schema + "Settings",
                new XElement(Schema + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Schema + "DisallowStartIfOnBatteries", false), new XElement(Schema + "StopIfGoingOnBatteries", false),
                new XElement(Schema + "AllowHardTerminate", false), new XElement(Schema + "StartWhenAvailable", true),
                new XElement(Schema + "RunOnlyIfNetworkAvailable", false),
                new XElement(Schema + "Enabled", true), new XElement(Schema + "ExecutionTimeLimit", "PT0S")),
            new XElement(Schema + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(Schema + "Exec",
                new XElement(Schema + "Command", executable), new XElement(Schema + "WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();
    }

    internal static void CheckOwned(string xml, string sid)
    {
        var root = XDocument.Parse(xml).Root;
        if (!((string)root?.Element(Schema + "RegistrationInfo")?.Element(Schema + "Description") ?? "").StartsWith(Marker(sid) + "\n", StringComparison.Ordinal))
            throw new IOException("存在同名但不属于本程序的启动任务，未修改它。");
    }

    private static object Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true));
        try { service.Connect(); return service; }
        catch { Release(service); throw; }
    }

    internal static object ReadOrMissing(Func<object> read)
    {
        try { return read(); }
        // COM dynamic dispatch maps ERROR_FILE_NOT_FOUND to FileNotFoundException on .NET.
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }

    private static object Find(object folder, string sid) => ReadOrMissing(() => ((dynamic)folder).GetTask(Name(sid)));

    internal static State Read()
    {
        string sid = Sid;
        object service = null, folder = null, task = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder(@"\"); task = Find(folder, sid);
            if (task == null) return new(false, "");
            string xml = ((dynamic)task).Xml;
            CheckOwned(xml, sid);
            var root = XDocument.Parse(xml).Root;
            return new((bool)((dynamic)task).Enabled, (string)root.Element(Schema + "Actions")?.Element(Schema + "Exec")?.Element(Schema + "Command") ?? "");
        }
        finally { Release(task); Release(folder); Release(service); }
    }

    internal static void SetEnabled(bool enabled, string executable = null)
    {
        if (!ElevationRestart.IsElevated) throw new InvalidOperationException("请先以管理员身份运行程序，再设置自动启动。");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MWB_STANDALONE_DATA")))
            throw new InvalidOperationException("自定义配置目录模式不支持自动启动，以免登录后使用不同的配对设置。");
        executable = Path.GetFullPath(executable ?? Environment.ProcessPath);
        if (enabled && (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), "MouseWithoutBorders.exe", StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(Path.GetPathRoot(executable)).DriveType != DriveType.Fixed))
            throw new IOException("请将完整程序放在本机固定磁盘的独立文件夹中，再启用自动启动。");
        string sid = Sid;
        object service = null, folder = null, task = null, registered = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder(@"\"); task = Find(folder, sid);
            if (task != null) CheckOwned((string)((dynamic)task).Xml, sid);
            if (enabled)
            {
                // Interactive current-user logon; no stored password, SYSTEM account or boot trigger.
                registered = ((dynamic)folder).RegisterTask(Name(sid), BuildXml(executable, sid), 6, sid, null, 3, null);
            }
            else if (task != null) ((dynamic)folder).DeleteTask(Name(sid), 0);
        }
        finally { Release(registered); Release(task); Release(folder); Release(service); }
        var actual = Read();
        if (actual.Enabled != enabled || (enabled && !string.Equals(actual.Executable, executable, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Windows 未确认自动启动设置，请重新打开设置检查。");
    }

    internal static void ValidateXml(string xml)
    {
        object service = null, folder = null, result = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder(@"\");
            // TASK_VALIDATE_ONLY: checks the real Windows schema without registering or running a task.
            result = ((dynamic)folder).RegisterTask("MWB-Validate-" + Guid.NewGuid().ToString("N"), xml, 1, Sid, null, 3, null);
        }
        finally { Release(result); Release(folder); Release(service); }
    }

    private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
