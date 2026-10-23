using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarDock
{
    internal sealed class AppAudioSession
    {
        public int ProcessId;
        public string Name;
        public float Volume;
        public bool Muted;
    }

    /// <summary>
    /// Per-app volume, like the Windows Volume Mixer: enumerates the audio sessions of the
    /// default output device and changes the volume of every session of a process.
    /// </summary>
    internal static class AudioMixer
    {
        // Created from its CLSID rather than a [ComImport] class: two such classes for the
        // same CLSID make the runtime reuse one wrapper and fail to cast it to the other.
        private static readonly Guid DeviceEnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int context, IntPtr parameters,
                [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
            int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out int processId);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        private const int AudioSessionStateExpired = 2;

        /// <summary>Visits every live audio session with its process id.</summary>
        private static void ForEachSession(Action<int, IAudioSessionControl2> visit)
        {
            const int eRender = 0;
            const int eMultimedia = 1;
            const int CLSCTX_ALL = 23;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(DeviceEnumeratorClsid));
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device) != 0 || device == null)
                return;
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object managerObject;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObject) != 0)
                return;
            var manager = (IAudioSessionManager2)managerObject;
            IAudioSessionEnumerator sessions;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null)
                return;
            int count;
            sessions.GetCount(out count);
            for (int index = 0; index < count; index++)
            {
                IAudioSessionControl2 session;
                if (sessions.GetSession(index, out session) != 0 || session == null)
                    continue;
                int state, processId;
                session.GetState(out state);
                session.GetProcessId(out processId);
                if (state == AudioSessionStateExpired || processId == 0 || session.IsSystemSoundsSession() == 0)
                    continue;
                visit(processId, session);
            }
        }

        public static List<AppAudioSession> List()
        {
            var byProcess = new Dictionary<int, AppAudioSession>();
            try
            {
                ForEachSession((processId, session) =>
                {
                    if (byProcess.ContainsKey(processId))
                        return;
                    var volume = (ISimpleAudioVolume)session;
                    float level;
                    bool muted;
                    volume.GetMasterVolume(out level);
                    volume.GetMute(out muted);
                    byProcess[processId] = new AppAudioSession
                    {
                        ProcessId = processId,
                        Name = AppName(processId, session),
                        Volume = level,
                        Muted = muted
                    };
                });
            }
            catch (Exception exception)
            {
                AppTrace.Log("Listing audio sessions failed: " + exception.Message);
            }
            var result = new List<AppAudioSession>(byProcess.Values);
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        public static float? GetVolume(int processId)
        {
            float? result = null;
            try
            {
                ForEachSession((id, session) =>
                {
                    float level;
                    if (id == processId && result == null && ((ISimpleAudioVolume)session).GetMasterVolume(out level) == 0)
                        result = level;
                });
            }
            catch (Exception) { }
            return result;
        }

        public static void SetVolume(int processId, float level)
        {
            try
            {
                Guid context = Guid.Empty;
                ForEachSession((id, session) =>
                {
                    if (id != processId)
                        return;
                    var volume = (ISimpleAudioVolume)session;
                    volume.SetMasterVolume(Math.Max(0f, Math.Min(1f, level)), ref context);
                    if (level > 0.001f)
                        volume.SetMute(false, ref context);
                });
            }
            catch (Exception exception)
            {
                AppTrace.Log("Setting app volume failed: " + exception.Message);
            }
        }

        public static void ToggleMute(int processId)
        {
            try
            {
                Guid context = Guid.Empty;
                bool? target = null;
                ForEachSession((id, session) =>
                {
                    if (id != processId)
                        return;
                    var volume = (ISimpleAudioVolume)session;
                    if (target == null)
                    {
                        bool muted;
                        volume.GetMute(out muted);
                        target = !muted;
                    }
                    volume.SetMute(target.Value, ref context);
                });
            }
            catch (Exception) { }
        }

        private static string AppName(int processId, IAudioSessionControl2 session)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    string description = null;
                    try { description = process.MainModule.FileVersionInfo.FileDescription; }
                    catch (Exception) { }
                    if (!string.IsNullOrWhiteSpace(description))
                        return description.Trim();
                    string display;
                    if (session.GetDisplayName(out display) == 0 && !string.IsNullOrWhiteSpace(display) && !display.StartsWith("@"))
                        return display;
                    return process.ProcessName;
                }
            }
            catch (Exception)
            {
                return "Proceso " + processId;
            }
        }
    }
}
