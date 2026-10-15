// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Monitor.Logic;

/// <summary>Logical processors of one efficiency class ("Performance ×8").</summary>
public sealed record CoreGroup(string Name, IReadOnlyList<int> Indices);

/// <summary>Groups logical processors for the per-core matrix (upstream CPUCoreTopology).</summary>
public static class CoreGrouping
{
    /// <param name="efficiencyClasses">Efficiency class per logical processor; higher is faster.</param>
    public static IReadOnlyList<CoreGroup> Group(IReadOnlyList<int> efficiencyClasses)
    {
        if (efficiencyClasses.Count == 0) return [];
        var distinct = efficiencyClasses.Distinct().OrderByDescending(c => c).ToList();
        if (distinct.Count == 1)
            return [new CoreGroup("Cores", Enumerable.Range(0, efficiencyClasses.Count).ToList())];
        var groups = new List<CoreGroup>();
        for (var rank = 0; rank < distinct.Count; rank++)
        {
            var cls = distinct[rank];
            var name = rank == 0 ? "Performance" : rank == distinct.Count - 1 ? "Efficiency" : "Mid";
            var indices = efficiencyClasses.Select((c, i) => (c, i)).Where(x => x.c == cls).Select(x => x.i).ToList();
            groups.Add(new CoreGroup(name, indices));
        }
        return groups;
    }
}
