// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// One shared IMMNotificationClient for every sound feature. Registered while at least one
/// subscriber exists; device arrivals, removals, state and default changes are coalesced
/// (Windows sends bursts while a headset connects) and delivered on the UI thread.
/// </summary>
internal static class AudioDeviceWatcher
{
    private static readonly List<Action> Subscribers = [];
    private static Client? _client;
    private static DispatcherTimer? _debounce;

    /// <summary>Calls <paramref name="onChanged"/> (UI thread) after endpoint changes settle. Dispose to stop.</summary>
    public static IDisposable Subscribe(Action onChanged)
    {
        Subscribers.Add(onChanged);
        if (_client is null) Register();
        return new ActionDisposable(() =>
        {
            Subscribers.Remove(onChanged);
            if (Subscribers.Count == 0) Unregister();
        });
    }

    private static void Register()
    {
        try
        {
            var client = new Client();
            if (AudioEndpoints.Enumerator.RegisterEndpointNotificationCallback(client) == 0) _client = client;
            else Log.Warn("Registering for audio device notifications failed");
        }
        catch (Exception ex)
        {
            Log.Error("Registering for audio device notifications failed", ex);
        }
    }

    private static void Unregister()
    {
        _debounce?.Stop();
        _debounce = null;
        if (_client is null) return;
        try
        {
            AudioEndpoints.Enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            Log.Error("Unregistering audio device notifications failed", ex);
        }
        _client = null;
    }

    private static void Kick()
    {
        if (_client is null) return;
        if (_debounce is null)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) =>
            {
                _debounce?.Stop();
                foreach (var subscriber in Subscribers.ToArray()) Ui.Guard(subscriber);
            };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Called by Windows on its own threads: only hands off to the UI thread.</summary>
    [ComVisible(true)]
    private sealed class Client : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) => Post();
        public int OnDeviceAdded(string deviceId) => Post();
        public int OnDeviceRemoved(string deviceId) => Post();
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) => Post();
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

        private static int Post()
        {
            Ui.OnUi(Kick);
            return 0;
        }
    }
}
