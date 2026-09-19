using System.Runtime.InteropServices;

namespace LumiPad.App;

public readonly record struct SystemVolumeState(
    double Percent,
    bool IsMuted);

public static class SystemVolumeService
{
    public static bool TryGetState(out SystemVolumeState state)
    {
        SystemVolumeState localState = default;

        try
        {
            bool ok = WithEndpoint(endpoint =>
            {
                Marshal.ThrowExceptionForHR(
                    endpoint.GetMasterVolumeLevelScalar(out float scalar));
                Marshal.ThrowExceptionForHR(
                    endpoint.GetMute(out bool muted));

                localState = new SystemVolumeState(
                    Math.Clamp(scalar * 100.0, 0.0, 100.0),
                    muted);
                return true;
            });

            state = localState;
            return ok;
        }
        catch
        {
            state = default;
            return false;
        }
    }

    public static bool TrySetVolume(double percent)
    {
        try
        {
            percent = Math.Clamp(percent, 0.0, 100.0);
            return WithEndpoint(endpoint =>
            {
                Marshal.ThrowExceptionForHR(
                    endpoint.SetMasterVolumeLevelScalar(
                        (float)(percent / 100.0),
                        Guid.Empty));
                return true;
            });
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetMute(bool muted)
    {
        try
        {
            return WithEndpoint(endpoint =>
            {
                Marshal.ThrowExceptionForHR(
                    endpoint.SetMute(muted, Guid.Empty));
                return true;
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool WithEndpoint(
        Func<IAudioEndpointVolume, bool> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? endpointObject = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(
                enumerator.GetDefaultAudioEndpoint(
                    EDataFlow.Render,
                    ERole.Multimedia,
                    out device));

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(
                device.Activate(
                    ref iid,
                    CLSCTX.All,
                    IntPtr.Zero,
                    out endpointObject));

            return action((IAudioEndpointVolume)endpointObject);
        }
        finally
        {
            if (endpointObject is not null &&
                Marshal.IsComObject(endpointObject))
            {
                Marshal.FinalReleaseComObject(endpointObject);
            }

            if (device is not null &&
                Marshal.IsComObject(device))
            {
                Marshal.FinalReleaseComObject(device);
            }

            if (enumerator is not null &&
                Marshal.IsComObject(enumerator))
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [Flags]
    private enum CLSCTX : uint
    {
        InprocServer = 0x1,
        InprocHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InprocServer | InprocHandler | LocalServer | RemoteServer
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            EDataFlow dataFlow,
            uint stateMask,
            out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            EDataFlow dataFlow,
            ERole role,
            out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            CLSCTX clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(
            int access,
            out IntPtr properties);

        [PreserveSig]
        int GetId(
            [MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(
            float levelDb,
            Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(
            float level,
            Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(
            uint channel,
            float levelDb,
            Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(
            uint channel,
            float level,
            Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(
            uint channel,
            out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(
            uint channel,
            out float level);

        [PreserveSig]
        int SetMute(
            [MarshalAs(UnmanagedType.Bool)] bool muted,
            Guid eventContext);

        [PreserveSig]
        int GetMute(
            [MarshalAs(UnmanagedType.Bool)] out bool muted);

        [PreserveSig]
        int GetVolumeStepInfo(
            out uint step,
            out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint mask);

        [PreserveSig]
        int GetVolumeRange(
            out float minDb,
            out float maxDb,
            out float incrementDb);
    }
}
