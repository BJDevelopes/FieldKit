// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;

namespace Fieldkit.Features.Sound;

/// <summary>An active audio endpoint (output or input).</summary>
/// <param name="Id">The MMDevice endpoint id ("{0.0.0.00000000}.{guid}"), stable across reboots.</param>
/// <param name="IsDefault">Default for the multimedia role (what Windows calls "the" default device).</param>
internal sealed record AudioDevice(string Id, string Name, EDataFlow Flow, bool IsDefault, bool IsHeadphones, bool IsVirtual);

/// <summary>
/// Endpoint enumeration and the few writes the sound features need: the default device
/// (through the undocumented but long-stable IPolicyConfig used by the Sound control panel),
/// endpoint volume and endpoint mute. Call from the UI thread.
/// </summary>
internal static class AudioEndpoints
{
    [ThreadStatic] private static IMMDeviceEnumerator? _enumerator;

    private static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IID_IPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    /// <summary>The thread's device enumerator (created once, kept for the app's lifetime).</summary>
    public static IMMDeviceEnumerator Enumerator => _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>Active endpoints of one direction, in Windows' order, the default flagged.</summary>
    public static List<AudioDevice> List(EDataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            var defaultId = DefaultId(flow);
            if (Enumerator.EnumAudioEndpoints(flow, CoreAudioNative.DEVICE_STATE_ACTIVE, out var collection) != 0) return result;
            try
            {
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;
                    try
                    {
                        if (Describe(device, flow, defaultId) is { } info) result.Add(info);
                    }
                    finally
                    {
                        CoreAudioNative.Release(device);
                    }
                }
            }
            finally
            {
                CoreAudioNative.Release(collection);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Listing {flow} endpoints failed", ex);
        }
        return result;
    }

    private static AudioDevice? Describe(IMMDevice device, EDataFlow flow, string? defaultId)
    {
        if (device.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) return null;
        string name = id;
        uint formFactor = 10; // UnknownFormFactor
        if (device.OpenPropertyStore(CoreAudioNative.STGM_READ, out var store) == 0)
        {
            try
            {
                name = ReadString(store, CoreAudioNative.PKEY_Device_FriendlyName) ?? id;
                formFactor = ReadUInt(store, CoreAudioNative.PKEY_AudioEndpoint_FormFactor) ?? formFactor;
            }
            finally
            {
                CoreAudioNative.Release(store);
            }
        }
        return new AudioDevice(id, name, flow, id == defaultId,
            SoundLogic.LooksLikeHeadphones(name, formFactor), SoundLogic.LooksVirtual(name));
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            return value.VarType == CoreAudioNative.VT_LPWSTR ? Marshal.PtrToStringUni(value.Value) : null;
        }
        finally
        {
            CoreAudioNative.PropVariantClear(ref value);
        }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            return value.VarType == CoreAudioNative.VT_UI4 ? unchecked((uint)value.Value.ToInt64()) : null;
        }
        finally
        {
            CoreAudioNative.PropVariantClear(ref value);
        }
    }

    /// <summary>The default endpoint id for a role, or null when there is none.</summary>
    public static string? DefaultId(EDataFlow flow, ERole role = ERole.Multimedia)
    {
        try
        {
            if (Enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0) return null;
            try
            {
                return device.GetId(out var id) == 0 ? id : null;
            }
            finally
            {
                CoreAudioNative.Release(device);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Makes <paramref name="deviceId"/> the default for every role (console, multimedia, communications).</summary>
    public static unsafe bool SetDefault(string deviceId)
    {
        var clsid = CLSID_PolicyConfigClient;
        var iid = IID_IPolicyConfig;
        if (CoreAudioNative.CoCreateInstance(ref clsid, IntPtr.Zero, CoreAudioNative.CLSCTX_ALL, ref iid, out var config) != 0 ||
            config == IntPtr.Zero)
        {
            Log.Warn("IPolicyConfig is unavailable; cannot change the default audio device");
            return false;
        }
        try
        {
            // IPolicyConfig vtable: IUnknown (3), 10 methods, then SetDefaultEndpoint at slot 13.
            var vtable = *(IntPtr**)config;
            var setDefault = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)vtable[13];
            var text = Marshal.StringToCoTaskMemUni(deviceId);
            try
            {
                var ok = true;
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                    ok &= setDefault(config, text, (int)role) == 0;
                return ok;
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Setting the default audio device failed", ex);
            return false;
        }
        finally
        {
            Marshal.Release(config);
        }
    }

    /// <summary>Master volume (0–1) of an endpoint, or null if unavailable.</summary>
    public static float? GetVolume(string deviceId) =>
        WithEndpointVolume(deviceId, v => v.GetMasterVolumeLevelScalar(out var level) == 0 ? level : (float?)null);

    public static bool SetVolume(string deviceId, float level) =>
        WithEndpointVolume(deviceId, v => v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), IntPtr.Zero) == 0);

    public static bool? GetMute(string deviceId) =>
        WithEndpointVolume(deviceId, v => v.GetMute(out var mute) == 0 ? mute : (bool?)null);

    public static bool SetMute(string deviceId, bool mute) =>
        WithEndpointVolume(deviceId, v => v.SetMute(mute, IntPtr.Zero) == 0);

    private static T? WithEndpointVolume<T>(string deviceId, Func<IAudioEndpointVolume, T> action)
    {
        try
        {
            if (Enumerator.GetDevice(deviceId, out var device) != 0) return default;
            try
            {
                var iid = CoreAudioNative.IID_IAudioEndpointVolume;
                if (device.Activate(ref iid, CoreAudioNative.CLSCTX_ALL, IntPtr.Zero, out var instance) != 0) return default;
                try
                {
                    return action((IAudioEndpointVolume)instance);
                }
                finally
                {
                    CoreAudioNative.Release(instance);
                }
            }
            finally
            {
                CoreAudioNative.Release(device);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Endpoint volume call failed", ex);
            return default;
        }
    }
}

/// <summary>
/// Per-app output device through the undocumented <c>Windows.Media.Internal.AudioPolicyConfig</c>
/// activation factory (the same API Settings → Sound → Volume mixer uses). The interface id
/// changed in build 21390; the vtable layout (IInspectable + 19 methods, then
/// Set/GetPersistedDefaultAudioEndpoint) matches EarTrumpet's long-standing declaration.
/// Unavailable before Windows 10 1803, in which case the mixer disables its pickers.
/// </summary>
internal static class AppAudioRouting
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const string MmDevApiToken = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureInterface = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const int SetSlot = 25;
    private const int GetSlot = 26;

    private static IntPtr _factory;
    private static bool _attempted;

    /// <summary>Whether per-app routing can be used on this PC.</summary>
    public static bool IsSupported => EnsureFactory() != IntPtr.Zero;

    private static IntPtr EnsureFactory()
    {
        if (_attempted) return _factory;
        _attempted = true;
        var build = Environment.OSVersion.Version.Build;
        if (build < 17134) return IntPtr.Zero;
        var iid = build >= 21390
            ? new Guid("AB3D4648-E242-459F-B02F-541C70306324")
            : new Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258");
        IntPtr name = IntPtr.Zero;
        try
        {
            if (CoreAudioNative.WindowsCreateString(ClassName, (uint)ClassName.Length, out name) != 0) return IntPtr.Zero;
            if (CoreAudioNative.RoGetActivationFactory(name, ref iid, out var factory) == 0) _factory = factory;
        }
        catch (Exception ex)
        {
            Log.Warn($"Per-app audio routing is unavailable: {ex.Message}");
        }
        finally
        {
            if (name != IntPtr.Zero) CoreAudioNative.WindowsDeleteString(name);
        }
        return _factory;
    }

    /// <summary>The output a process was sent to, or null for "Default".</summary>
    public static unsafe string? GetOutput(uint pid)
    {
        var factory = EnsureFactory();
        if (factory == IntPtr.Zero || pid == 0) return null;
        try
        {
            var vtable = *(IntPtr**)factory;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)vtable[GetSlot];
            IntPtr hstring = IntPtr.Zero;
            if (get(factory, pid, (int)EDataFlow.Render, (int)ERole.Multimedia, &hstring) != 0) return null;
            if (hstring == IntPtr.Zero) return null;
            try
            {
                var buffer = CoreAudioNative.WindowsGetStringRawBuffer(hstring, out var length);
                var packed = Marshal.PtrToStringUni(buffer, (int)length);
                return SoundLogic.UnpackPolicyDeviceId(packed, MmDevApiToken, RenderInterface, CaptureInterface);
            }
            finally
            {
                CoreAudioNative.WindowsDeleteString(hstring);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading a per-app output failed", ex);
            return null;
        }
    }

    /// <summary>Sends a process to <paramref name="deviceId"/>, or back to the default when null.</summary>
    public static unsafe bool SetOutput(uint pid, string? deviceId)
    {
        var factory = EnsureFactory();
        if (factory == IntPtr.Zero || pid == 0) return false;
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                var packed = MmDevApiToken + deviceId + RenderInterface;
                if (CoreAudioNative.WindowsCreateString(packed, (uint)packed.Length, out hstring) != 0) return false;
            }
            var vtable = *(IntPtr**)factory;
            var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)vtable[SetSlot];
            var ok = set(factory, pid, (int)EDataFlow.Render, (int)ERole.Multimedia, hstring) == 0;
            ok &= set(factory, pid, (int)EDataFlow.Render, (int)ERole.Console, hstring) == 0;
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("Setting a per-app output failed", ex);
            return false;
        }
        finally
        {
            if (hstring != IntPtr.Zero) CoreAudioNative.WindowsDeleteString(hstring);
        }
    }
}
