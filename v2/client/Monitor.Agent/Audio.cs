using System.Runtime.InteropServices;

namespace Monitor.Agent;

/// <summary>Which processes are playing sound right now (Core Audio session meters). Carried over from the original app.</summary>
internal static class Audio
{
    public static List<int> ProcessesPlayingAudio()
    {
        var pids = new List<int>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? speakers = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out speakers) != 0 || speakers is null) return pids;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (speakers.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var obj) != 0) return pids;
            manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null) return pids;
            sessions.GetCount(out var count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0 || session is null) continue;
                try
                {
                    if (session is IAudioMeterInformation meter && meter.GetPeakValue(out var peak) == 0 && peak > 0
                        && session.GetProcessId(out var pid) == 0 && pid > 0 && !pids.Contains((int)pid))
                        pids.Add((int)pid);
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            }
        }
        catch
        {
            // No audio device, or the audio service is restarting.
        }
        finally
        {
            foreach (var o in new object?[] { sessions, manager, speakers, enumerator })
                if (o is not null) Marshal.ReleaseComObject(o);
        }
        return pids;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl1();
        int NotImpl2();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int NotImpl0();
        int NotImpl1();
        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
        int NotImpl5();
        int NotImpl6();
        int NotImpl7();
        int NotImpl8();
        int NotImpl9();
        int NotImpl10();
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
