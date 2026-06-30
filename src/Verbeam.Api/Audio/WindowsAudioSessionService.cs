using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Verbeam.Api.Audio;

public sealed class WindowsAudioSessionService
{
    private const int ClsctxAll = 23;
    private static readonly Guid AudioSessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public AudioSessionSnapshot ListSessions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return AudioSessionSnapshot.Unsupported("Windows audio sessions are only available on Windows.");
        }

        object? enumeratorObject = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? managerObject = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumerator, throwOnError: true)!);
            enumerator = (IMMDeviceEnumerator)enumeratorObject!;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));

            var managerId = AudioSessionManager2Id;
            Marshal.ThrowExceptionForHR(device.Activate(ref managerId, ClsctxAll, IntPtr.Zero, out managerObject));
            manager = (IAudioSessionManager2)managerObject!;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));

            var values = new List<AudioAppSession>(Math.Max(0, count));
            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(index, out control));
                    if (control is null)
                    {
                        continue;
                    }

                    values.Add(ReadSession(control));
                }
                catch (COMException)
                {
                    // Sessions can disappear while enumerating; keep the snapshot best-effort.
                }
                finally
                {
                    ReleaseCom(control);
                }
            }

            return new AudioSessionSnapshot(
                Supported: true,
                ErrorMessage: string.Empty,
                CapturedAt: DateTimeOffset.UtcNow,
                EndpointRole: "multimedia",
                values
                    .OrderByDescending(item => item.IsAudible)
                    .ThenByDescending(item => item.Peak)
                    .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return AudioSessionSnapshot.Unsupported(ex.Message);
        }
        finally
        {
            ReleaseCom(sessions);
            ReleaseCom(managerObject);
            ReleaseCom(device);
            ReleaseCom(enumeratorObject);
        }
    }

    private static AudioAppSession ReadSession(IAudioSessionControl control)
    {
        _ = control.GetState(out var state);
        _ = control.GetDisplayName(out var displayName);

        var control2 = control as IAudioSessionControl2;
        var meter = control as IAudioMeterInformation;
        var volume = control as ISimpleAudioVolume;

        var processId = 0;
        var isSystemSounds = false;
        var sessionId = string.Empty;
        var instanceId = string.Empty;
        if (control2 is not null)
        {
            _ = control2.GetProcessId(out processId);
            isSystemSounds = control2.IsSystemSoundsSession() == 0;
            _ = control2.GetSessionIdentifier(out sessionId);
            _ = control2.GetSessionInstanceIdentifier(out instanceId);
        }

        var peak = 0f;
        if (meter is not null)
        {
            _ = meter.GetPeakValue(out peak);
        }

        var masterVolume = 1f;
        var muted = false;
        if (volume is not null)
        {
            _ = volume.GetMasterVolume(out masterVolume);
            _ = volume.GetMute(out muted);
        }

        var processInfo = ResolveProcess(processId);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? processInfo.DisplayName
            : displayName.Trim();

        return new AudioAppSession(
            Id: sessionId,
            InstanceId: instanceId,
            ProcessId: processId,
            ProcessName: processInfo.ProcessName,
            DisplayName: name,
            MainWindowTitle: processInfo.MainWindowTitle,
            State: state.ToString().ToLowerInvariant(),
            Peak: MathF.Round(Math.Clamp(peak, 0f, 1f), 4),
            Volume: MathF.Round(Math.Clamp(masterVolume, 0f, 1f), 4),
            Muted: muted,
            IsSystemSounds: isSystemSounds,
            IsAudible: state == AudioSessionState.Active && peak > 0.001f && !muted);
    }

    private static AudioProcessInfo ResolveProcess(int processId)
    {
        if (processId <= 0)
        {
            return new AudioProcessInfo("system", "System sounds", string.Empty);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = string.IsNullOrWhiteSpace(process.ProcessName)
                ? processId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : process.ProcessName;
            var title = process.MainWindowTitle ?? string.Empty;
            var displayName = string.IsNullOrWhiteSpace(title) ? processName : title;
            return new AudioProcessInfo(processName, displayName, title);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AudioProcessInfo($"pid-{processId}", $"PID {processId}", string.Empty);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static class ComIds
    {
        public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    }
}

public sealed record AudioSessionSnapshot(
    bool Supported,
    string ErrorMessage,
    DateTimeOffset CapturedAt,
    string EndpointRole,
    IReadOnlyList<AudioAppSession> Sessions)
{
    public static AudioSessionSnapshot Unsupported(string errorMessage)
        => new(false, errorMessage, DateTimeOffset.UtcNow, string.Empty, []);
}

public sealed record AudioAppSession(
    string Id,
    string InstanceId,
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string MainWindowTitle,
    string State,
    float Peak,
    float Volume,
    bool Muted,
    bool IsSystemSounds,
    bool IsAudible);

internal sealed record AudioProcessInfo(
    string ProcessName,
    string DisplayName,
    string MainWindowTitle);

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

internal enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint access, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionCount, out IAudioSessionControl session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr newNotifications);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr newNotifications);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

    [PreserveSig]
    int GetProcessId(out int retVal);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference(bool optOut);
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig]
    int GetPeakValue(out float peak);

    [PreserveSig]
    int GetMeteringChannelCount(out uint channelCount);

    [PreserveSig]
    int GetChannelsPeakValues(uint channelCount, [Out] float[] peaks);

    [PreserveSig]
    int QueryHardwareSupport(out uint hardwareSupportMask);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute(bool isMuted, ref Guid eventContext);

    [PreserveSig]
    int GetMute(out bool isMuted);
}
