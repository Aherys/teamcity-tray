using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCityTray;

public sealed class Poller : IDisposable
{
    private readonly Settings _settings;
    private readonly TeamCityClient _client = new();
    private readonly BuildTracker _tracker = new();
    private readonly SoundService _sound;
    private readonly CancellationTokenSource _cts = new();
    private string _userKey = "";
    private string _user = "";

    // Raised on a background thread.
    public event Action<IReadOnlyList<RowData>, bool>? Updated;

    public Poller(Settings settings, SoundService sound)
    {
        _settings = settings;
        _sound = sound;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, _settings.PollSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.ServerUrl) || string.IsNullOrEmpty(_settings.Token))
            {
                Updated?.Invoke(Array.Empty<RowData>(), false);
                return;
            }

            _client.Configure(_settings.ServerUrl, _settings.Token);
            var userKey = _settings.ServerUrl + "\n" + _settings.Token;
            if (userKey != _userKey)
            {
                _user = await _client.GetCurrentUserAsync(ct);
                _userKey = userKey;
            }

            var running = await _client.GetRunningAsync(ct);
            var queued = await _client.GetQueuedAsync(ct);
            var options = new TrackerOptions(_settings.OnlyMine, _settings.LinkChildrenByNumber, _settings.KeepFinishedSeconds);
            _tracker.Update(running.Concat(queued).ToList(), _user, options, now);

            var anySuccess = false;
            var anyFailure = false;
            foreach (var id in _tracker.Pending)
            {
                FinalInfo? final;
                try
                {
                    final = await _client.GetFinishedAsync(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }
                if (final is null)
                {
                    _tracker.Discard(id);
                }
                else if (string.Equals(final.State, "finished", StringComparison.OrdinalIgnoreCase))
                {
                    var success = string.Equals(final.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                    _tracker.MarkFinished(id, success, now);
                    anySuccess |= success;
                    anyFailure |= !success;
                }
            }

            if (anyFailure || anySuccess)
            {
                _sound.Play(anyFailure);
            }
            Updated?.Invoke(_tracker.Snapshot(now, _settings.KeepFinishedSeconds), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Updated?.Invoke(_tracker.Snapshot(now, _settings.KeepFinishedSeconds), true);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _cts.Dispose();
    }
}
