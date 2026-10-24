using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.Audio;

internal interface IAudioDefaults
{
    string Get(DataFlow flow, Role role);
    void Set(string id, Role role);
}

internal sealed class WindowsAudioDefaults : IAudioDefaults
{
    internal static void CheckSupport()
    {
        object instance = new PolicyConfigClient();
        try { _ = (IPolicyConfig)instance; }
        finally { Marshal.ReleaseComObject(instance); }
    }
    public string Get(DataFlow flow, Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
        return device.ID;
    }

    public void Set(string id, Role role)
    {
        object instance = new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(((IPolicyConfig)instance).SetDefaultEndpoint(id, (int)role));
            // The interface is not a public Windows contract: report failure rather than
            // pretending an unsupported OS accepted the default-device change.
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Unused COM slots in their native order. Only SetDefaultEndpoint is called.
        [PreserveSig] int GetMixFormat(IntPtr device, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr device, int defaults, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr device);
        [PreserveSig] int SetDeviceFormat(IntPtr device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr device, int defaults, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr device, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr device, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr device, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr device, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr device, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr device, int visible);
    }
}
