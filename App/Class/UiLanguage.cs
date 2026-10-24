// Standalone Chinese user interface. Stored option values stay compatible with upstream.
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders;

internal static class UiLanguage
{
    internal static string EasyMouseLabel(int value) => (EasyMouseOption)value switch
    {
        EasyMouseOption.Disable => "禁用鼠标跨屏",
        EasyMouseOption.Ctrl => "按住 Ctrl 跨屏",
        EasyMouseOption.Shift => "按住 Shift 跨屏",
        _ => "直接跨屏",
    };

    internal static int ParseEasyMouseLabel(string label) => label switch
    {
        "禁用鼠标跨屏" => (int)EasyMouseOption.Disable,
        "按住 Ctrl 跨屏" => (int)EasyMouseOption.Ctrl,
        "按住 Shift 跨屏" => (int)EasyMouseOption.Shift,
        _ => (int)EasyMouseOption.Enable,
    };

    internal static string ButtonCaption(string name) => name switch
    {
        "YesButton" => "是",
        "NoButton" => "否",
        "LinkButton" => "连接",
        "NextButton" => "下一步",
        "DoneButton" => "完成",
        _ => null,
    };
}
