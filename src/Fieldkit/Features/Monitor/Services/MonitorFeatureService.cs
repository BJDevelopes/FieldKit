// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;

namespace Fieldkit.Features.Monitor.Services;

/// <summary>
/// The background part of one monitor feature: its notification-area readouts and alerts. Runs
/// only while one of them is switched on (and the feature is installed); stopping it removes the
/// feature's icons, alerts and sampler lease. Panel sections sample on their own while visible.
/// </summary>
public sealed class MonitorFeatureService(string featureId) : FeatureService
{
    private readonly string[] _keys = [.. TrayReadouts.KeysFor(featureId), .. MonitorAlerts.KeysFor(featureId)];

    public override string FeatureId => featureId;
    public override IReadOnlyList<string> EnableKeys => _keys;
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        TrayReadouts.Attach(featureId);
        MonitorAlerts.Attach(featureId);
    }

    protected override void OnStop()
    {
        TrayReadouts.Detach(featureId);
        MonitorAlerts.Detach(featureId);
    }
}
