// Managed equivalent of the upstream MWB GPO reads; machine policy takes precedence.
using System;
using Microsoft.Win32;

namespace PowerToys.GPOWrapper;
public enum GpoRuleConfigured { Unavailable = -3, WrongValue = -2, NotConfigured = -1, Disabled = 0, Enabled = 1 }
public static class GPOWrapper
{
    private const string PolicyPath = @"SOFTWARE\Policies\PowerToys";
    private static object Read(string name)
    {
        using var machine = Registry.LocalMachine.OpenSubKey(PolicyPath);
        var value = machine?.GetValue(name);
        if (value != null) return value;
        using var user = Registry.CurrentUser.OpenSubKey(PolicyPath);
        return user?.GetValue(name);
    }
    private static GpoRuleConfigured Policy(string name)
    {
        try { return Read(name) switch { null => GpoRuleConfigured.NotConfigured, 0 => GpoRuleConfigured.Disabled, 1 => GpoRuleConfigured.Enabled, _ => GpoRuleConfigured.WrongValue }; }
        catch (System.Security.SecurityException) { return GpoRuleConfigured.Unavailable; }
        catch (UnauthorizedAccessException) { return GpoRuleConfigured.Unavailable; }
    }
    public static GpoRuleConfigured GetConfiguredMouseWithoutBordersEnabledValue()
    {
        var value = Policy("ConfigureEnabledUtilityMouseWithoutBorders");
        return value is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled ? value : Policy("ConfigureGlobalUtilityEnabledState");
    }
    public static GpoRuleConfigured GetConfiguredMwbClipboardSharingEnabledValue() => Policy("MwbClipboardSharingEnabled");
    public static GpoRuleConfigured GetConfiguredMwbFileTransferEnabledValue() => Policy("MwbFileTransferEnabled");
    public static GpoRuleConfigured GetConfiguredMwbUseOriginalUserInterfaceValue() => Policy("MwbUseOriginalUserInterface");
    public static GpoRuleConfigured GetConfiguredMwbDisallowBlockingScreensaverValue() => Policy("MwbDisallowBlockingScreensaver");
    public static GpoRuleConfigured GetConfiguredMwbAllowServiceModeValue() => Policy("MwbAllowServiceMode");
    public static GpoRuleConfigured GetConfiguredMwbSameSubnetOnlyValue() => Policy("MwbSameSubnetOnly");
    public static GpoRuleConfigured GetConfiguredMwbValidateRemoteIpValue() => Policy("MwbValidateRemoteIp");
    public static GpoRuleConfigured GetConfiguredMwbDisableUserDefinedIpMappingRulesValue() => Policy("MwbDisableUserDefinedIpMappingRules");
    public static string GetConfiguredMwbPolicyDefinedIpMappingRules()
    {
        try { return Read("MwbPolicyDefinedIpMappingRules") is string[] lines ? string.Join("\r\n", lines) : string.Empty; }
        catch (System.Security.SecurityException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }
}
