// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;

namespace Fieldkit.Features.Input.Scrolling;

/// <summary>
/// A wheel feature: running means "take part in the shared wheel handler". The handler (and
/// its hook) exists only while at least one of these runs.
/// </summary>
internal abstract class WheelFeatureService(string featureId, params string[] enableKeys) : FeatureService
{
    public override string FeatureId => featureId;
    public override IReadOnlyList<string> EnableKeys => enableKeys;
    public override bool EnabledByDefault => false;
    protected override void OnStart() => WheelPipeline.Attach(featureId);
    protected override void OnStop() => WheelPipeline.Detach(featureId);
}

internal sealed class ScrollInverterService() : WheelFeatureService("scrollInverter", "scrollInverter.vertical", "scrollInverter.horizontal");

internal sealed class ScrollHorizontalService() : WheelFeatureService("scrollHorizontal", "scrollHorizontal.enabled");

internal sealed class LinearScrollService() : WheelFeatureService("linearScroll", "linearScroll.enabled");

internal sealed class SmoothScrollService() : WheelFeatureService("smoothScroll", "smoothScroll.enabled");
