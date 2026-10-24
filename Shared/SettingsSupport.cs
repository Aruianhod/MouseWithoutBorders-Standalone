// Standalone configuration adapter. Microsoft upstream models remain in MouseWithoutBordersProperties.cs.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public record StringProperty
    {
        [JsonPropertyName("value")] public string Value { get; set; } = "";
        public StringProperty() { }
        public StringProperty(string value) => Value = value;
        public static implicit operator StringProperty(string value) => new(value);
    }
    public record IntProperty
    {
        [JsonPropertyName("value")] public int Value { get; set; }
        public IntProperty() { }
        public IntProperty(int value) => Value = value;
    }
    public record GenericProperty<T>
    {
        [JsonPropertyName("value")] public T Value { get; set; }
        public GenericProperty() { }
        public GenericProperty(T value) => Value = value;
    }
    public class BoolPropertyJsonConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False) return reader.GetBoolean();
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.GetProperty("value").GetBoolean();
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteBoolean("value", value); writer.WriteEndObject();
        }
    }
    public record HotkeySettings
    {
        [JsonPropertyName("win")] public bool Win { get; set; }
        [JsonPropertyName("ctrl")] public bool Ctrl { get; set; }
        [JsonPropertyName("alt")] public bool Alt { get; set; }
        [JsonPropertyName("shift")] public bool Shift { get; set; }
        [JsonPropertyName("code")] public int Code { get; set; }
        public HotkeySettings() { }
        public HotkeySettings(bool win, bool ctrl, bool alt, bool shift, int code) => (Win, Ctrl, Alt, Shift, Code) = (win, ctrl, alt, shift, code);
        public bool IsEmpty() => Code == 0;
        public override string ToString() => string.Join("", Win ? "Win+" : "", Ctrl ? "Ctrl+" : "", Alt ? "Alt+" : "", Shift ? "Shift+" : "", Code == 0 ? "Disabled" : ((System.Windows.Forms.Keys)Code).ToString());
    }
    public class MouseWithoutBordersSettings
    {
        public const string ModuleName = "MouseWithoutBorders";
        [JsonPropertyName("name")] public string Name { get; set; } = ModuleName;
        [JsonPropertyName("version")] public string Version { get; set; } = "1.1";
        [JsonPropertyName("properties")] public MouseWithoutBordersProperties Properties { get; set; } = new();
        public void Save(SettingsUtils settingsUtils) => settingsUtils.SaveSettings(JsonSerializer.Serialize(this, SettingsUtils.JsonOptions), ModuleName);
    }
    public class SettingsUtils
    {
        public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true, PropertyNameCaseInsensitive = true };
        public static SettingsUtils Default { get; } = new();
        public static string DataDirectory => Environment.GetEnvironmentVariable("MWB_STANDALONE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseWithoutBordersStandalone");
        private readonly string root;
        public SettingsUtils() : this(DataDirectory) { }
        public SettingsUtils(string directory) => root = directory;
        public string GetSettingsFilePath(string module, string file = "settings.json") => Path.Combine(root, file);
        public bool SettingsExists(string module) => File.Exists(GetSettingsFilePath(module));
        public T GetSettingsOrDefault<T>(string module) where T : new()
        {
            var path = GetSettingsFilePath(module);
            if (!File.Exists(path)) return new T();
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T(); }
            catch (JsonException ex) { throw new IOException("Settings JSON is damaged. Restore settings.json.bak or reconfigure.", ex); }
        }
        public void SaveSettings(string json, string module)
        {
            Directory.CreateDirectory(root);
            string file = GetSettingsFilePath(module);
            // Atomic replacement prevents watchers and interrupted writes from seeing partial JSON.
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                if (File.Exists(file)) File.Replace(temp, file, file + ".bak");
                else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

namespace Microsoft.PowerToys.Settings.UI.Library.Utilities
{
    internal static class Helper
    {
        public static string UserLocalAppDataPath { get; set; }
        public static IFileSystemWatcher GetFileWatcher(string module, string file, Action callback)
        {
            var root = Microsoft.PowerToys.Settings.UI.Library.SettingsUtils.DataDirectory;
            Directory.CreateDirectory(root);
            var watcher = new FileSystem().FileSystemWatcher.New(root, file);
            Timer timer = new(_ => callback(), null, Timeout.Infinite, Timeout.Infinite);
            void Queue() { timer.Change(150, Timeout.Infinite); }
            watcher.Changed += (_, _) => Queue();
            watcher.Created += (_, _) => Queue();
            watcher.Renamed += (_, _) => Queue();
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
    }
    internal static class EventLogger
    {
        public static void LogEvent(string text, EventLogEntryType type) => ManagedCommon.Logger.LogInfo($"{type}: {text}");
    }
}

namespace ManagedCommon
{
    internal static class Logger
    {
        private static readonly object Gate = new();
        private static string logFile;
        public static void InitializeLogger(string unused)
        {
            var dir = Path.Combine(Microsoft.PowerToys.Settings.UI.Library.SettingsUtils.DataDirectory, "Logs");
            Directory.CreateDirectory(dir);
            logFile = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
        }
        public static void LogInfo(string text, string member = "", string file = "", int line = 0)
        {
            lock (Gate)
            {
                try
                {
                    if (logFile == null) InitializeLogger("");
                    if (new FileInfo(logFile).Exists && new FileInfo(logFile).Length > 5_000_000) File.Move(logFile, logFile + ".old", true);
                    File.AppendAllText(logFile, $"{DateTime.Now:O} {member}: {text}{Environment.NewLine}");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
