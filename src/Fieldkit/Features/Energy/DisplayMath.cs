// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Energy;

/// <summary>How a display's brightness is driven.</summary>
public enum BrightnessRoute
{
    /// <summary>Laptop panel through WMI (WmiMonitorBrightnessMethods).</summary>
    System,
    /// <summary>External monitor through DDC/CI VCP 0x10.</summary>
    Ddc,
    /// <summary>A dimming overlay over the picture.</summary>
    Software,
}

/// <summary>Brightness key / shortcut step (upstream KeyStep).</summary>
public enum BrightnessKeyStep { Standard, Half, Quarter }

/// <summary>Pure brightness maths ported from upstream BrightnessSupport / ExtraBrightnessSupport.</summary>
public static partial class DisplayMath
{
    /// <summary>Width of the extra-dimming range below the monitor's own minimum.</summary>
    public const double ExtendedDimmingRange = 0.25;

    /// <summary>Lowest picture level the software overlay will reach (keeps the screen readable).</summary>
    public const double SoftwareDimFloor = 0.1;

    public const int PathListLimit = 16;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that mean "built into the device".
    private const uint TechLvds = 6;
    private const uint TechDisplayPortEmbedded = 11;
    private const uint TechUdiEmbedded = 13;
    private const uint TechInternal = 0x80000000;

    public static bool IsInternalTechnology(uint technology) =>
        technology is TechInternal or TechLvds or TechDisplayPortEmbedded or TechUdiEmbedded;

    /// <summary>A stable per-connection key: the monitor device path, else the GDI name.</summary>
    public static string ConnectionKey(string? devicePath, string gdiName) =>
        string.IsNullOrWhiteSpace(devicePath) ? gdiName.ToUpperInvariant() : devicePath.ToUpperInvariant();

    /// <summary>
    /// "\\?\DISPLAY#BOE0867#4&amp;2a8&amp;0&amp;UID8388688#{e6f07b5f-...}" → "DISPLAY\BOE0867\4&amp;2A8&amp;0&amp;UID8388688".
    /// </summary>
    public static string NormalizeDevicePath(string devicePath)
    {
        var s = devicePath.Trim();
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        var brace = s.IndexOf("#{", StringComparison.Ordinal);
        if (brace >= 0) s = s[..brace];
        return s.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>"DISPLAY\BOE0867\4&amp;2a8&amp;0&amp;UID8388688_0" → "DISPLAY\BOE0867\4&amp;2A8&amp;0&amp;UID8388688".</summary>
    public static string NormalizeWmiInstance(string instanceName) =>
        WmiSuffix().Replace(instanceName.Trim(), string.Empty).ToUpperInvariant();

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex WmiSuffix();

    /// <summary>The WMI brightness instance driving a display, or null.</summary>
    public static string? MatchWmiInstance(string devicePath, bool isInternal, IReadOnlyList<string> instances, int internalDisplayCount)
    {
        if (instances.Count == 0) return null;
        if (!string.IsNullOrEmpty(devicePath))
        {
            var normalized = NormalizeDevicePath(devicePath);
            foreach (var instance in instances)
            {
                if (NormalizeWmiInstance(instance) == normalized) return instance;
            }
        }
        // One laptop panel and one WMI instance: they are the same thing.
        return isInternal && instances.Count == 1 && internalDisplayCount == 1 ? instances[0] : null;
    }

    public static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    public static uint SanitizedMaximum(uint max) => max > 0 ? max : 100;

    public static double Normalized(uint current, uint max) => Clamp01(current / (double)SanitizedMaximum(max));

    public static uint DeviceValue(double level, uint max) => (uint)Math.Round(Clamp01(level) * SanitizedMaximum(max), MidpointRounding.AwayFromZero);

    /// <summary>Splits a slider value into (hardware, picture) when extra dimming is on.</summary>
    public static (double Hardware, double Picture) ExtendedDimmingComponents(double level)
    {
        var b = Clamp01(level);
        return b < ExtendedDimmingRange
            ? (0, b / ExtendedDimmingRange)
            : ((b - ExtendedDimmingRange) / (1 - ExtendedDimmingRange), 1);
    }

    /// <summary>Reverse of <see cref="ExtendedDimmingComponents"/> from a hardware reading.</summary>
    public static double ExtendedDimmingLevel(double hardware, double? remembered, bool pictureDimmed)
    {
        var physical = ExtendedDimmingRange + Clamp01(hardware) * (1 - ExtendedDimmingRange);
        if (pictureDimmed && hardware <= 0.001 && remembered is { } r && r < ExtendedDimmingRange)
            return Math.Clamp(r, 0, ExtendedDimmingRange);
        return physical;
    }

    /// <summary>Overlay opacity for a picture level (1 = no dimming).</summary>
    public static double OverlayOpacity(double picture) =>
        picture >= 0.999 ? 0 : 1 - Math.Max(SoftwareDimFloor, Clamp01(picture));

    public static double StepFraction(BrightnessKeyStep step) => step switch
    {
        BrightnessKeyStep.Half => 1.0 / 32,
        BrightnessKeyStep.Quarter => 1.0 / 64,
        _ => 1.0 / 16,
    };

    public static BrightnessKeyStep ParseStep(string? value) => value switch
    {
        "half" => BrightnessKeyStep.Half,
        "quarter" => BrightnessKeyStep.Quarter,
        _ => BrightnessKeyStep.Standard,
    };

    /// <summary>A press is never larger than the chosen step.</summary>
    public static double Limited(BrightnessKeyStep step, double delta)
    {
        var f = StepFraction(step);
        return Math.Abs(delta) <= f ? delta : Math.Sign(delta) * f;
    }

    public static double Stepped(double current, double delta) => Clamp01(current + delta);

    public static int WholePercent(double level) => double.IsFinite(level) ? (int)Math.Round(Clamp01(level) * 100, MidpointRounding.AwayFromZero) : 0;

    public static int FilledSegments(double level, int segments = 16) =>
        level <= 0 ? 0 : Math.Min((int)Math.Ceiling(level * segments), segments);

    /// <summary>Removes <paramref name="path"/> and empty entries, re-appends it when wanted, keeps the newest 16.</summary>
    public static List<string> UpdatedPathList(IEnumerable<string> stored, string path, bool include, int limit = PathListLimit)
    {
        var list = stored.Where(p => !string.IsNullOrWhiteSpace(p) && !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (include && !string.IsNullOrWhiteSpace(path)) list.Add(path);
        return list.Count > limit ? list.Skip(list.Count - limit).ToList() : list;
    }

    // ---- Extra brightness (SDR white level on HDR displays) -----------------

    /// <summary>SDR white level units: 1000 = 80 nits. Windows' slider spans 80–480 nits.</summary>
    public const uint SdrWhiteMinimum = 1000;
    public const uint SdrWhiteMaximum = 6000;

    /// <summary>White level for an intensity (10–100 %) above the user's own level.</summary>
    public static uint BoostedWhiteLevel(uint original, int intensityPercent)
    {
        var baseLevel = Math.Clamp(original == 0 ? SdrWhiteMinimum : original, SdrWhiteMinimum, SdrWhiteMaximum);
        var t = Math.Clamp(intensityPercent, 0, 100) / 100.0;
        return (uint)Math.Round(baseLevel + (SdrWhiteMaximum - baseLevel) * t);
    }

    public static double WhiteLevelNits(uint level) => level * 80.0 / 1000.0;
}
