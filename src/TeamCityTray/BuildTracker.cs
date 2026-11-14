using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCityTray;

public sealed record TrackerOptions(bool OnlyMine, bool LinkByNumber, int KeepFinishedSeconds);

// Pure logic: "mine" filter, parent/child grouping and finish detection. No I/O.
public sealed class BuildTracker
{
    private static readonly TimeSpan RootMemory = TimeSpan.FromHours(6);

    private sealed record KnownRoot(long RootId, DateTime? Queued, DateTime LastSeen);

    private sealed record Entry(
        BuildInfo Info,
        RowState State,
        int Percent,
        int Elapsed,
        long GroupId,
        DateTime GroupQueued,
        int Depth,
        DateTime? FinishedAt = null);

    private readonly Dictionary<(string, string), KnownRoot> _roots = new();
    private readonly HashSet<long> _rootIds = new();
    private Dictionary<long, Entry> _active = new();
    private readonly Dictionary<long, Entry> _pending = new();
    private readonly Dictionary<long, Entry> _finished = new();

    // Builds that left the active list and still need their final status.
    public IReadOnlyList<long> Pending => _pending.Keys.ToList();

    public void Update(IReadOnlyList<BuildInfo> builds, string currentUser, TrackerOptions options, DateTime now)
    {
        var byId = builds.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var seeds = new HashSet<long>();
        var mine = new HashSet<long>();

        if (options.OnlyMine)
        {
            foreach (var b in byId.Values.Where(b => string.Equals(b.TriggerUser, currentUser, StringComparison.OrdinalIgnoreCase)))
            {
                seeds.Add(b.Id);
            }
            _rootIds.IntersectWith(byId.Keys);
            _rootIds.UnionWith(seeds);
            mine.UnionWith(_rootIds);
            mine.UnionWith(_active.Keys.Where(byId.ContainsKey));
        }
        else
        {
            _rootIds.Clear();
            _rootIds.UnionWith(byId.Keys);
            mine.UnionWith(byId.Keys);
        }

        foreach (var id in _rootIds.Where(byId.ContainsKey))
        {
            var root = byId[id];
            if (root.Number.Length > 0)
            {
                _roots[RunKey(root)] = new KnownRoot(id, root.Queued, now);
            }
        }
        foreach (var stale in _roots.Where(r => now - r.Value.LastSeen > RootMemory).Select(r => r.Key).ToList())
        {
            _roots.Remove(stale);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var b in byId.Values.Where(b => !mine.Contains(b.Id)))
            {
                if (IsLinked(b, byId, mine, options))
                {
                    mine.Add(b.Id);
                    changed = true;
                }
            }
        }
        while (changed);

        foreach (var (id, entry) in _active.Where(a => !mine.Contains(a.Key)))
        {
            _pending[id] = entry;
        }
        foreach (var id in mine)
        {
            _pending.Remove(id);
        }

        var next = new Dictionary<long, Entry>();
        foreach (var id in mine)
        {
            var b = byId[id];
            var (group, groupQueued, depth) = Place(b, byId, mine, options);
            if (depth == 0 && group == b.Id && !_rootIds.Contains(b.Id)
                && _active.TryGetValue(b.Id, out var prior) && prior.Depth > 0)
            {
                (group, groupQueued, depth) = (prior.GroupId, prior.GroupQueued, prior.Depth);
            }
            var elapsed = b.ElapsedSeconds ?? (b.Started.HasValue ? (int)Math.Max(0, (now - b.Started.Value).TotalSeconds) : 0);
            next[id] = new Entry(b, b.IsQueued ? RowState.Queued : RowState.Running, b.Percent, elapsed, group, groupQueued, depth);
        }
        _active = next;
    }

    public void MarkFinished(long id, bool success, DateTime now)
    {
        if (_pending.Remove(id, out var entry))
        {
            _finished[id] = entry with
            {
                State = success ? RowState.Success : RowState.Failure,
                Percent = 100,
                FinishedAt = now,
            };
        }
    }

    public void Discard(long id) => _pending.Remove(id);

    public IReadOnlyList<RowData> Snapshot(DateTime now, int keepFinishedSeconds)
    {
        foreach (var id in _finished.Where(f => (now - f.Value.FinishedAt!.Value).TotalSeconds > keepFinishedSeconds).Select(f => f.Key).ToList())
        {
            _finished.Remove(id);
        }

        return _active.Values.Concat(_finished.Values)
            .OrderBy(e => e.GroupQueued)
            .ThenBy(e => e.GroupId)
            .ThenBy(e => e.Depth)
            .ThenBy(e => e.Info.Queued ?? DateTime.MaxValue)
            .ThenBy(e => e.Info.Id)
            .Select(e => new RowData(
                e.Info.Id,
                e.Info.BuildTypeName,
                e.Info.Number,
                e.State,
                e.Percent,
                e.Elapsed,
                e.Info.WebUrl,
                e.Depth,
                string.IsNullOrEmpty(e.Info.ProjectName) ? e.Info.BuildTypeName : e.Info.ProjectName + " / " + e.Info.BuildTypeName))
            .ToList();
    }

    private static (string, string) RunKey(BuildInfo b) => (b.ProjectId.ToLowerInvariant(), b.Number);

    private bool QueuedAfterRoot(BuildInfo b, KnownRoot root) =>
        b.Queued is null || root.Queued is null || b.Queued >= root.Queued;

    private bool IsLinked(BuildInfo b, IReadOnlyDictionary<long, BuildInfo> byId, HashSet<long> mine, TrackerOptions options)
    {
        if (b.TriggerBuildId is { } parent && mine.Contains(parent))
        {
            return true;
        }
        if (b.Dependencies.Any(mine.Contains))
        {
            return true;
        }
        if (byId.Values.Any(m => mine.Contains(m.Id) && m.Dependencies.Contains(b.Id)))
        {
            return true;
        }
        return options.LinkByNumber && b.Number.Length > 0
            && _roots.TryGetValue(RunKey(b), out var root) && QueuedAfterRoot(b, root);
    }

    private (long Group, DateTime GroupQueued, int Depth) Place(
        BuildInfo b, IReadOnlyDictionary<long, BuildInfo> byId, HashSet<long> mine, TrackerOptions options)
    {
        var self = (b.Id, b.Queued ?? DateTime.MaxValue, 0);
        if (_rootIds.Contains(b.Id))
        {
            return self;
        }
        if (b.TriggerBuildId is { } parent && mine.Contains(parent))
        {
            return GroupOf(parent, byId) ?? self;
        }
        if (options.LinkByNumber && b.Number.Length > 0
            && _roots.TryGetValue(RunKey(b), out var root) && QueuedAfterRoot(b, root))
        {
            return (root.RootId, root.Queued ?? DateTime.MaxValue, 1);
        }
        foreach (var rootId in _rootIds.Where(byId.ContainsKey))
        {
            var r = byId[rootId];
            if (r.Dependencies.Contains(b.Id) || b.Dependencies.Contains(rootId))
            {
                return (rootId, r.Queued ?? DateTime.MaxValue, 1);
            }
        }
        return self;
    }

    private (long, DateTime, int)? GroupOf(long id, IReadOnlyDictionary<long, BuildInfo> byId)
    {
        if (_active.TryGetValue(id, out var e))
        {
            return (e.GroupId, e.GroupQueued, 1);
        }
        return byId.TryGetValue(id, out var b) ? (b.Id, b.Queued ?? DateTime.MaxValue, 1) : null;
    }
}
