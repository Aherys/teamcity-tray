using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCityTray;

public sealed record TrackerOptions(bool OnlyMine, bool LinkByNumber, int KeepFinishedSeconds, IReadOnlySet<string> BotAccounts);

// Pure logic: "mine" filter, parent/child grouping and finish detection. No I/O.
public sealed class BuildTracker
{
    private static readonly TimeSpan RootMemory = TimeSpan.FromHours(6);
    private static readonly TimeSpan BotGrace = TimeSpan.FromSeconds(120);

    private sealed record KnownRoot(BuildInfo Info, DateTime LastSeen);

    private sealed record Entry(
        BuildInfo Info,
        RowState State,
        int Percent,
        int Elapsed,
        long GroupId,
        DateTime GroupQueued,
        int Depth,
        DateTime? FinishedAt = null);

    private readonly object _gate = new();
    private readonly Dictionary<long, KnownRoot> _roots = new();
    private readonly HashSet<long> _dismissed = new();
    private bool _allRoots;
    private Dictionary<long, Entry> _active = new();
    private readonly Dictionary<long, Entry> _pending = new();
    private readonly Dictionary<long, Entry> _finished = new();

    // Builds that left the active list and still need their final status.
    public IReadOnlyList<long> Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Keys.ToList();
            }
        }
    }

    public void Update(IReadOnlyList<BuildInfo> builds, IReadOnlyList<BuildInfo> seeds, string currentUser, TrackerOptions options, DateTime now)
    {
        lock (_gate)
        {
            UpdateLocked(builds, seeds, currentUser, options, now);
        }
    }

    private void UpdateLocked(IReadOnlyList<BuildInfo> builds, IReadOnlyList<BuildInfo> seeds, string currentUser, TrackerOptions options, DateTime now)
    {
        var byId = builds.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var mine = new HashSet<long>();
        _allRoots = !options.OnlyMine;

        if (options.OnlyMine)
        {
            var seeded = new HashSet<long>();
            foreach (var b in byId.Values.Where(b => string.Equals(b.TriggerUser, currentUser, StringComparison.OrdinalIgnoreCase)))
            {
                _roots[b.Id] = new KnownRoot(b, now);
                seeded.Add(b.Id);
            }
            foreach (var s in seeds)
            {
                _roots[s.Id] = new KnownRoot(byId.TryGetValue(s.Id, out var active) ? active : s, now);
                seeded.Add(s.Id);
            }
            // A root that left the active lists and was not seen again is finished.
            foreach (var (id, root) in _roots.Where(r => !seeded.Contains(r.Key) && !r.Value.Info.IsFinished).ToList())
            {
                _roots[id] = root with { Info = root.Info with { IsFinished = true, IsQueued = false, Finished = now } };
            }
            foreach (var stale in _roots.Where(r => now - r.Value.LastSeen > RootMemory).Select(r => r.Key).ToList())
            {
                _roots.Remove(stale);
            }
            mine.UnionWith(_roots.Keys.Where(byId.ContainsKey));
            mine.UnionWith(_active.Keys.Where(byId.ContainsKey));
        }
        else
        {
            _roots.Clear();
            mine.UnionWith(byId.Keys);
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
            if (depth == 0 && group == b.Id && !_roots.ContainsKey(b.Id)
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
        lock (_gate)
        {
            if (_roots.TryGetValue(id, out var root))
            {
                _roots[id] = root with
                {
                    Info = root.Info with
                    {
                        IsFinished = true,
                        IsQueued = false,
                        Finished = root.Info.Finished ?? now,
                        Status = success ? "SUCCESS" : "FAILURE",
                    },
                };
            }
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
    }

    public void Discard(long id)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }
    }

    public void Dismiss(long id)
    {
        lock (_gate)
        {
            _dismissed.Add(id);
            _finished.Remove(id);
        }
    }

    public void DismissFailures()
    {
        lock (_gate)
        {
            foreach (var id in _finished.Where(f => f.Value.State == RowState.Failure).Select(f => f.Key).ToList())
            {
                _dismissed.Add(id);
                _finished.Remove(id);
            }
            foreach (var r in _roots.Values.Where(r => r.Info.IsFinished && !IsSuccess(r.Info)))
            {
                _dismissed.Add(r.Info.Id);
            }
        }
    }

    public IReadOnlyList<RowData> Snapshot(DateTime now, int keepFinishedSeconds)
    {
        lock (_gate)
        {
            var activeGroups = _active.Values.Select(e => e.GroupId).ToHashSet();
            var lastFinish = _finished.Values.GroupBy(e => e.GroupId).ToDictionary(g => g.Key, g => g.Max(e => e.FinishedAt!.Value));
            foreach (var (id, entry) in _finished.ToList())
            {
                // Failures stay until dismissed. Other rows stay while their group runs, then expire together.
                if (entry.State != RowState.Failure && !activeGroups.Contains(entry.GroupId)
                    && (now - lastFinish[entry.GroupId]).TotalSeconds > keepFinishedSeconds)
                {
                    _finished.Remove(id);
                }
            }

            var entries = _active.Values.Concat(_finished.Values).Where(e => !_dismissed.Contains(e.Info.Id)).ToList();
            var ids = entries.Select(e => e.Info.Id).ToHashSet();
            foreach (var group in entries.Select(e => e.GroupId).Distinct().ToList())
            {
                if (!ids.Contains(group) && !_dismissed.Contains(group)
                    && _roots.TryGetValue(group, out var root) && root.Info.IsFinished)
                {
                    entries.Add(FinishedRootEntry(root.Info));
                }
            }

            return entries
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
    }

    private static bool IsSuccess(BuildInfo b) => string.Equals(b.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

    private static Entry FinishedRootEntry(BuildInfo b)
    {
        var elapsed = b.Started is { } s && b.Finished is { } f ? (int)Math.Max(0, (f - s).TotalSeconds) : 0;
        return new Entry(b, IsSuccess(b) ? RowState.Success : RowState.Failure, 100, elapsed, b.Id, b.Queued ?? DateTime.MaxValue, 0, b.Finished);
    }

    private bool IsKnown(long id, HashSet<long> mine) => mine.Contains(id) || _roots.ContainsKey(id);

    private bool IsLinked(BuildInfo b, IReadOnlyDictionary<long, BuildInfo> byId, HashSet<long> mine, TrackerOptions options)
    {
        if (b.TriggerBuildId is { } parent && IsKnown(parent, mine))
        {
            return true;
        }
        if (b.Dependencies.Any(d => IsKnown(d, mine)))
        {
            return true;
        }
        if (byId.Values.Any(m => mine.Contains(m.Id) && m.Dependencies.Contains(b.Id)))
        {
            return true;
        }
        return MatchRoot(b, options) is not null;
    }

    // The most recent root of the same project that the build can belong to.
    private KnownRoot? MatchRoot(BuildInfo b, TrackerOptions options)
    {
        KnownRoot? best = null;
        foreach (var root in _roots.Values)
        {
            var r = root.Info;
            if (r.Id == b.Id || !string.Equals(r.ProjectId, b.ProjectId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (b.Queued is { } bq && r.Queued is { } rq && bq < rq)
            {
                continue;
            }
            var match = (options.LinkByNumber && b.Number.Length > 0 && b.Number == r.Number)
                || SameComment(b, r)
                || InBotWindow(b, r, options);
            if (match && (best is null || (r.Queued ?? DateTime.MinValue) > (best.Info.Queued ?? DateTime.MinValue)))
            {
                best = root;
            }
        }
        return best;
    }

    private static bool SameComment(BuildInfo b, BuildInfo r) =>
        !string.IsNullOrWhiteSpace(b.Comment) && string.Equals(b.Comment.Trim(), r.Comment?.Trim(), StringComparison.Ordinal);

    private static bool InBotWindow(BuildInfo b, BuildInfo r, TrackerOptions options)
    {
        if (b.TriggerUser is null || !options.BotAccounts.Contains(b.TriggerUser)
            || b.Queued is not { } queued || r.Started is not { } started || queued < started)
        {
            return false;
        }
        if (!r.IsFinished)
        {
            return !r.IsQueued;
        }
        return r.Finished is { } finished && queued <= finished + BotGrace;
    }

    private (long Group, DateTime GroupQueued, int Depth) Place(
        BuildInfo b, IReadOnlyDictionary<long, BuildInfo> byId, HashSet<long> mine, TrackerOptions options)
    {
        var self = (b.Id, b.Queued ?? DateTime.MaxValue, 0);
        if (_allRoots || _roots.ContainsKey(b.Id))
        {
            return self;
        }
        if (b.TriggerBuildId is { } parent && IsKnown(parent, mine))
        {
            return GroupOf(parent, byId) ?? self;
        }
        if (MatchRoot(b, options) is { } match)
        {
            return (match.Info.Id, match.Info.Queued ?? DateTime.MaxValue, 1);
        }
        foreach (var root in _roots.Values)
        {
            var r = root.Info;
            if (r.Dependencies.Contains(b.Id) || b.Dependencies.Contains(r.Id))
            {
                return (r.Id, r.Queued ?? DateTime.MaxValue, 1);
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
        if (_roots.TryGetValue(id, out var root))
        {
            return (id, root.Info.Queued ?? DateTime.MaxValue, 1);
        }
        return byId.TryGetValue(id, out var b) ? (b.Id, b.Queued ?? DateTime.MaxValue, 1) : null;
    }
}
