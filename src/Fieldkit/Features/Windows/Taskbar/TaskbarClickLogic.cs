// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.WindowTools;

public enum TaskbarClickAction { PassThrough, Minimize, Restore, CycleWindows }

/// <summary>The taskbar click decision (port of upstream DockClickSupport.action). Pure.</summary>
public static class TaskbarClickLogic
{
    /// <param name="appIsFrontmost">The clicked button's app owns the foreground window.</param>
    /// <param name="cycleCandidateCount">Unminimized windows of the app on the current desktop.</param>
    /// <param name="ownsMinimize">The app's windows were minimized by a previous taskbar click.</param>
    public static TaskbarClickAction Action(bool appIsFrontmost, bool hasUnminimizedWindows, bool hasMinimizedWindows,
        bool hasFullScreenWindows, bool hasModifiers, bool minimizeEnabled, bool cycleEnabled, int cycleCandidateCount,
        bool ownsMinimize)
    {
        if (hasModifiers) return TaskbarClickAction.PassThrough;
        if (cycleEnabled && appIsFrontmost && !hasFullScreenWindows && cycleCandidateCount > 1) return TaskbarClickAction.CycleWindows;
        if (hasFullScreenWindows) return TaskbarClickAction.PassThrough;
        if (minimizeEnabled && appIsFrontmost && hasUnminimizedWindows) return TaskbarClickAction.Minimize;
        if (minimizeEnabled && ownsMinimize && !hasUnminimizedWindows && hasMinimizedWindows) return TaskbarClickAction.Restore;
        return TaskbarClickAction.PassThrough;
    }
}
