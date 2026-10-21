using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Flags Live TV recordings that came out incomplete and, when asked, schedules a complete showing
/// of the same episode. It measures the minutes a recording was actually being written, so a
/// post-processing script that moves, cuts or transcodes the file afterwards can't affect the
/// verdict. It never deletes anything.
///
/// All state lives in one JSON file and every read and write goes through one lock, so concurrent
/// API calls and server events can't corrupt it or schedule the same showing twice.
/// </summary>
public sealed class IncompleteRecordingService : IDisposable
{
    private const string StoreFileName = "livetv-incomplete-recordings.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILiveTvRecordingHost _host;
    private readonly ILogger<IncompleteRecordingService> _logger;
    private readonly TimeProvider _time;
    private readonly string _storePath;
    private readonly Func<IncompleteRecordingSettings> _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Requests opened or reopened since the last run, searched without waiting for a guide refresh.
    private readonly HashSet<string> _searchNow = new(StringComparer.Ordinal);

    private readonly DateTime _watchingSince;
    private IncompleteRecordingStoreData? _data;
    private IReadOnlyList<ScheduledTimer> _timers = Array.Empty<ScheduledTimer>();
    private DateTime? _timersLoadedAt;
    private bool _timersStale = true;
    private bool _searchAll;

    public IncompleteRecordingService(ILiveTvRecordingHost host, ILogger<IncompleteRecordingService> logger)
        : this(
            host,
            logger,
            TimeProvider.System,
            Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), StoreFileName),
            ReadPluginSettings)
    {
    }

    internal IncompleteRecordingService(
        ILiveTvRecordingHost host,
        ILogger<IncompleteRecordingService> logger,
        TimeProvider time,
        string storePath,
        Func<IncompleteRecordingSettings> settings)
    {
        _host = host;
        _logger = logger;
        _time = time;
        _storePath = storePath;
        _settings = settings;
        _watchingSince = Now;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public void Dispose() => _lock.Dispose();

    /// <summary>The settings as stored in the plugin configuration.</summary>
    public static IncompleteRecordingSettings ReadPluginSettings()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        return new IncompleteRecordingSettings
        {
            AutoRecordComplete = config?.LiveTvAutoRecordCompleteShowing ?? false,
            ThresholdPercent = IncompleteRecordingRules.ClampThreshold(
                config?.LiveTvIncompleteThresholdPercent ?? IncompleteRecordingRules.DefaultThresholdPercent),
        };
    }

    /// <summary>The schedule changed, so the cache reloads on the next run.</summary>
    public void MarkScheduleChanged() => _timersStale = true;

    /// <summary>The guide was refreshed, so the cache reloads and every waiting request searches again.</summary>
    public void MarkGuideRefreshed()
    {
        _timersStale = true;
        _searchAll = true;
    }

    /// <summary>
    /// When the next run is due: every sample interval while a recording window is open, otherwise
    /// the next window's start, the safety-net check or the daily request search, whichever is first.
    /// </summary>
    public DateTime NextRunAt()
    {
        var now = Now;
        if (_timersStale)
        {
            return now;
        }

        var data = _data;
        if (data != null && data.Snapshots.Any(s => s.JudgedAt == null))
        {
            return now + IncompleteRecordingRules.SampleInterval;
        }

        var candidates = new List<DateTime>
        {
            (_timersLoadedAt ?? now) + IncompleteRecordingRules.SafetyNetInterval,
        };

        foreach (var timer in _timers.Where(IsLive))
        {
            if (timer.WindowStart <= now && now < timer.WindowEnd)
            {
                return now + IncompleteRecordingRules.SampleInterval;
            }

            if (timer.WindowStart > now)
            {
                candidates.Add(timer.WindowStart);
            }
        }

        if (data != null && data.Requests.Any(r => CompleteShowingState.IsOpen(r.State)))
        {
            candidates.Add((data.LastRequestSearchAt ?? now) + IncompleteRecordingRules.RequestSearchFallback);
        }

        return candidates.Min();
    }

    /// <summary>One pass: reload the schedule if needed, sample open windows, judge closed ones and move requests along.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var now = Now;

            if (_timersStale || _timersLoadedAt == null || now - _timersLoadedAt >= IncompleteRecordingRules.SafetyNetInterval)
            {
                await ReloadTimersAsync(cancellationToken).ConfigureAwait(false);
            }

            var changed = StartSnapshots(data, now);
            changed |= Sample(data, now);
            changed |= await JudgeClosedAsync(data, now, cancellationToken).ConfigureAwait(false);
            changed |= TrackScheduledRequests(data, now);
            changed |= ApplySettings(data);

            var searchAll = _searchAll
                || data.LastRequestSearchAt == null
                || now - data.LastRequestSearchAt >= IncompleteRecordingRules.RequestSearchFallback;
            changed |= await SearchAsync(data, now, searchAll, cancellationToken).ConfigureAwait(false);
            if (searchAll)
            {
                _searchAll = false;
                data.LastRequestSearchAt = now;
                changed = true;
            }

            changed |= Prune(data, now);

            if (changed)
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Attaches an incomplete record to a newly added library item, by path or by episode.</summary>
    public async Task OnItemAddedAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            if (await TryAttachAsync(data, item, Now, cancellationToken).ConfigureAwait(false))
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A tag gone from an item, for any reason, dismisses its record. The tag is never put back.</summary>
    public async Task OnItemUpdatedAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            if (item.Tags.Contains(IncompleteRecordingRules.Tag, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var changed = false;
            foreach (var record in data.Records.Where(r => !r.Dismissed && r.TaggedItemIds.Contains(item.Id)))
            {
                record.Dismissed = true;
                changed = true;
            }

            if (changed)
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A removed item detaches from its record. Within a day of the recording the record waits for
    /// a replacement (a post-processing script swapping the file); after that it is dropped. An open
    /// request stays either way.
    /// </summary>
    public async Task OnItemRemovedAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var now = Now;
            var changed = false;
            foreach (var record in data.Records.Where(r => r.ItemIds.Contains(itemId)).ToList())
            {
                record.ItemIds.Remove(itemId);
                record.TaggedItemIds.Remove(itemId);
                if (record.ItemIds.Count == 0 && now - record.JudgedAt >= IncompleteRecordingRules.AttachWindow)
                {
                    data.Records.Remove(record);
                }

                changed = true;
            }

            if (changed)
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The display state of each incomplete item among the given ones, or of every incomplete item
    /// when none are given. Complete items are left out.
    /// </summary>
    public async Task<IReadOnlyList<IncompleteRecordingStateDto>> GetStatesAsync(IEnumerable<Guid>? itemIds, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var result = new List<IncompleteRecordingStateDto>();
            var ids = itemIds?.ToList() is { Count: > 0 } given
                ? given
                : data.Records.Where(r => !r.Dismissed).SelectMany(r => r.ItemIds).ToList();
            foreach (var itemId in ids.Distinct())
            {
                var record = data.Records.FirstOrDefault(r => !r.Dismissed && r.ItemIds.Contains(itemId));
                if (record == null)
                {
                    continue;
                }

                var request = record.RequestId == null ? null : data.Requests.FirstOrDefault(r => r.Id == record.RequestId);
                var state = IncompleteRecordingRules.DisplayState(request);
                result.Add(new IncompleteRecordingStateDto
                {
                    ItemId = itemId.ToString("N"),
                    RecordedMinutes = Math.Round(record.RecordedMinutes),
                    ExpectedMinutes = Math.Round(record.ExpectedMinutes),
                    State = state,
                    ScheduledStart = state == "scheduled" ? request!.ScheduledStart : null,
                    ChannelName = state == "scheduled" ? request!.ScheduledChannelName : null,
                    RecordedAt = state == "recorded" ? request!.RecordedAt : null,
                });
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Keep as Complete: dismisses the item's record for everyone and removes the tag.</summary>
    public async Task<bool> DismissAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var record = data.Records.FirstOrDefault(r => r.ItemIds.Contains(itemId));
            if (record == null)
            {
                return false;
            }

            record.Dismissed = true;
            Save(data);

            foreach (var id in record.TaggedItemIds)
            {
                await SetTagSafeAsync(id, false, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Opens an explicit request for a program's episode, or joins the open one, and searches for a
    /// showing at once. Returns null when the program has no season and episode numbers.
    /// </summary>
    public async Task<CompleteShowingRequest?> RequestCompleteShowingAsync(string programId, CancellationToken cancellationToken)
    {
        var key = _host.GetProgram(programId)?.Key;
        if (key == null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var now = Now;
            var request = FindOpenRequest(data, key);
            if (request == null)
            {
                request = new CompleteShowingRequest { Episode = key, OpenedAt = now };
                data.Requests.Add(request);
                _searchNow.Add(request.Id);
            }

            request.Explicit = true;
            if (_timersStale || _timersLoadedAt == null)
            {
                await ReloadTimersAsync(cancellationToken).ConfigureAwait(false);
            }

            await SearchAsync(data, now, false, cancellationToken).ConfigureAwait(false);
            Save(data);
            return request;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool IsLive(ScheduledTimer timer) =>
        timer.Status is "New" or "InProgress";

    private async Task ReloadTimersAsync(CancellationToken cancellationToken)
    {
        _timersStale = false;
        try
        {
            _timers = await _host.GetTimersAsync(cancellationToken).ConfigureAwait(false);
            _timersLoadedAt = Now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load Live TV timers");
            _timersLoadedAt = Now;
        }
    }

    // A snapshot is taken the first minute a timer in its window is actually recording.
    private bool StartSnapshots(IncompleteRecordingStoreData data, DateTime now)
    {
        var changed = false;
        foreach (var timer in _timers.Where(IsLive))
        {
            if (timer.WindowStart > now || now >= timer.WindowEnd + IncompleteRecordingRules.OverrunLimit
                || data.Snapshots.Any(s => s.TimerId == timer.TimerId))
            {
                continue;
            }

            var path = _host.GetActiveRecordingPath(timer.TimerId);
            if (path == null)
            {
                continue;
            }

            var details = _host.GetActiveRecordingTimer(path);
            if (details == null)
            {
                continue;
            }

            data.Snapshots.Add(CreateSnapshot(timer, details, now));
            changed = true;
        }

        return changed;
    }

    private RecordingSnapshot CreateSnapshot(ScheduledTimer timer, ActiveRecordingTimer details, DateTime now)
    {
        var snapshot = new RecordingSnapshot
        {
            TimerId = timer.TimerId,
            ProgramId = timer.ProgramId,
            ChannelName = timer.ChannelName,
            WindowEnd = details.EndDate.AddSeconds(details.PostPaddingSeconds),
            Episode = EpisodeKey.Create(details.SeriesId, details.Name, details.SeasonNumber, details.EpisodeNumber, details.EpisodeTitle),
        };

        if (details.RetryCount == 0)
        {
            snapshot.RuntimeMinutes = (details.EndDate - details.StartDate).TotalMinutes;
            snapshot.PrePaddingMinutes = details.PrePaddingSeconds / 60.0;
            snapshot.PostPaddingMinutes = details.PostPaddingSeconds / 60.0;
            snapshot.WindowStart = details.StartDate.AddSeconds(-details.PrePaddingSeconds);
        }
        else
        {
            // A retry has rewritten the timer's start and dropped its pre-padding, so the runtime
            // comes from the guide and the padding isn't known.
            var program = timer.ProgramId == null ? null : _host.GetProgram(timer.ProgramId);
            var start = program?.StartDate ?? details.StartDate;
            var end = program?.EndDate ?? details.EndDate;
            snapshot.RuntimeMinutes = Math.Max(0, (end - start).TotalMinutes);
            snapshot.WindowStart = start;
        }

        // The plugin wasn't running when this window opened (it was just installed or updated), so
        // what was written before can't be known. Giving it the benefit of the doubt errs toward
        // not flagging.
        if (snapshot.WindowStart < _watchingSince)
        {
            var unseen = (Min(now, _watchingSince) - snapshot.WindowStart).TotalMinutes;
            snapshot.RecordedMinutes = Math.Clamp(unseen, 0, snapshot.ExpectedMinutes);
        }

        return snapshot;
    }

    // A minute counts only when the recording is active and its file grew since the last sample,
    // so a tuner stall with the recorder still running doesn't count.
    private bool Sample(IncompleteRecordingStoreData data, DateTime now)
    {
        var changed = false;
        var minimumGap = IncompleteRecordingRules.SampleInterval * 0.9;
        foreach (var snapshot in data.Snapshots.Where(s => s.JudgedAt == null))
        {
            if (snapshot.LastSampleAt != null && now - snapshot.LastSampleAt < minimumGap)
            {
                continue;
            }

            var path = _host.GetActiveRecordingPath(snapshot.TimerId);
            if (path == null)
            {
                continue;
            }

            snapshot.LastSampleAt = now;
            changed = true;

            var fragment = snapshot.Fragments.FirstOrDefault(f => f.Path == path);
            if (fragment == null)
            {
                fragment = new RecordingFragment { Path = path };
                snapshot.Fragments.Add(fragment);
            }

            var size = _host.GetFileLength(path) ?? 0;
            if (size > fragment.LastSize)
            {
                snapshot.RecordedMinutes += IncompleteRecordingRules.SampleInterval.TotalMinutes;
                fragment.LastSize = size;
            }
        }

        return changed;
    }

    private async Task<bool> JudgeClosedAsync(IncompleteRecordingStoreData data, DateTime now, CancellationToken cancellationToken)
    {
        var changed = false;
        var threshold = _settings().ThresholdPercent;
        foreach (var snapshot in data.Snapshots.Where(s => s.JudgedAt == null).ToList())
        {
            if (now < snapshot.WindowEnd)
            {
                continue;
            }

            if (now < snapshot.WindowEnd + IncompleteRecordingRules.OverrunLimit
                && _host.GetActiveRecordingPath(snapshot.TimerId) != null)
            {
                continue;
            }

            snapshot.JudgedAt = now;
            snapshot.Complete = !IncompleteRecordingRules.IsIncomplete(snapshot.RecordedMinutes, snapshot.ExpectedMinutes, threshold);
            changed = true;

            _logger.LogInformation(
                "Live TV recording {TimerId} wrote {Recorded:0.#} of {Expected:0.#} minutes, {Verdict}",
                snapshot.TimerId,
                snapshot.RecordedMinutes,
                snapshot.ExpectedMinutes,
                snapshot.Complete == true ? "complete" : "incomplete");

            if (snapshot.Complete == true)
            {
                OnCompleteRecording(data, snapshot, now);
            }
            else
            {
                await OnIncompleteRecordingAsync(data, snapshot, now, cancellationToken).ConfigureAwait(false);
            }
        }

        return changed;
    }

    private static void OnCompleteRecording(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now)
    {
        if (snapshot.Episode == null)
        {
            return;
        }

        var request = FindOpenRequest(data, snapshot.Episode);
        if (request != null)
        {
            request.State = CompleteShowingState.Done;
            request.RecordedAt = now;
            request.ClosedAt = now;
        }
    }

    private async Task OnIncompleteRecordingAsync(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now, CancellationToken cancellationToken)
    {
        var record = new IncompleteRecord
        {
            TimerId = snapshot.TimerId,
            RecordedMinutes = snapshot.RecordedMinutes,
            ExpectedMinutes = snapshot.ExpectedMinutes,
            Episode = snapshot.Episode,
            FragmentPaths = snapshot.Fragments.Select(f => f.Path).ToList(),
            JudgedAt = now,
        };
        data.Records.Add(record);

        if (snapshot.Episode != null)
        {
            record.RequestId = LinkRequest(data, snapshot, now)?.Id;
        }

        // The library may already hold the item: the server queues its scan when the recording stops.
        foreach (var path in record.FragmentPaths)
        {
            if (_host.FindItemByPath(path) is { } item)
            {
                await AttachAsync(record, item, cancellationToken).ConfigureAwait(false);
            }
        }

        if (record.ItemIds.Count == 0 && record.Episode != null)
        {
            var completePaths = CompleteFragmentPaths(data);
            var candidate = _host.FindEpisodes(record.Episode)
                .Where(i => i.DateCreated >= snapshot.WindowStart && IsFreeForEpisodeAttach(data, i, completePaths))
                .OrderBy(i => i.DateCreated)
                .FirstOrDefault();
            if (candidate != null)
            {
                await AttachAsync(record, candidate, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private CompleteShowingRequest? LinkRequest(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now)
    {
        var episode = snapshot.Episode!;
        var open = FindOpenRequest(data, episode);
        if (open != null)
        {
            var wasScheduledShowing = open.State == CompleteShowingState.Scheduled
                && string.Equals(open.ScheduledProgramId, snapshot.ProgramId, StringComparison.OrdinalIgnoreCase);
            if (wasScheduledShowing)
            {
                RecordFailedAttempt(open, now);
            }

            return open;
        }

        // A request that gave up or that the user cancelled isn't reopened by a later recording.
        var ended = data.Requests
            .Where(r => r.State is CompleteShowingState.GaveUp or CompleteShowingState.CancelledByUser)
            .LastOrDefault(r => r.Episode.MatchesAiring(episode));
        if (ended != null)
        {
            return ended;
        }

        if (!_settings().AutoRecordComplete)
        {
            return null;
        }

        var request = new CompleteShowingRequest { Episode = episode, OpenedAt = now };
        data.Requests.Add(request);
        _searchNow.Add(request.Id);
        return request;
    }

    private void RecordFailedAttempt(CompleteShowingRequest request, DateTime now)
    {
        request.FailedAttempts++;
        ClearScheduled(request);
        if (request.FailedAttempts >= IncompleteRecordingRules.MaxFailedAttempts)
        {
            request.State = CompleteShowingState.GaveUp;
            request.ClosedAt = now;
        }
        else
        {
            request.State = CompleteShowingState.Waiting;
            _searchNow.Add(request.Id);
        }
    }

    private static void ClearScheduled(CompleteShowingRequest request)
    {
        request.ScheduledProgramId = null;
        request.ScheduledStart = null;
        request.ScheduledEnd = null;
        request.ScheduledChannelName = null;
    }

    // Follows each scheduled showing's timer: cancelled means the user cancelled it, gone before it
    // aired means a guide change removed it, and still nothing long after it ended means it never
    // recorded.
    private bool TrackScheduledRequests(IncompleteRecordingStoreData data, DateTime now)
    {
        if (_timersLoadedAt == null)
        {
            return false;
        }

        var changed = false;
        foreach (var request in data.Requests.Where(r => r.State == CompleteShowingState.Scheduled))
        {
            var timer = _timers.FirstOrDefault(t =>
                string.Equals(t.ProgramId, request.ScheduledProgramId, StringComparison.OrdinalIgnoreCase));

            if (timer?.Status == "Cancelled")
            {
                request.State = CompleteShowingState.CancelledByUser;
                request.ClosedAt = now;
                changed = true;
            }
            else if (timer == null && request.ScheduledStart > now)
            {
                ClearScheduled(request);
                request.State = CompleteShowingState.Waiting;
                _searchNow.Add(request.Id);
                changed = true;
            }
            else if (request.ScheduledEnd + IncompleteRecordingRules.OverrunLimit + TimeSpan.FromHours(1) < now
                && !data.Snapshots.Any(s => s.JudgedAt == null
                    && string.Equals(s.ProgramId, request.ScheduledProgramId, StringComparison.OrdinalIgnoreCase)))
            {
                RecordFailedAttempt(request, now);
                changed = true;
            }
        }

        return changed;
    }

    // Turning the automatic setting off drops the automatic requests still waiting; explicit ones stay.
    private bool ApplySettings(IncompleteRecordingStoreData data)
    {
        if (_settings().AutoRecordComplete)
        {
            return false;
        }

        var dropped = data.Requests
            .Where(r => r.State == CompleteShowingState.Waiting && !r.Explicit)
            .Select(r => r.Id)
            .ToHashSet();
        if (dropped.Count == 0)
        {
            return false;
        }

        data.Requests.RemoveAll(r => dropped.Contains(r.Id));
        foreach (var record in data.Records.Where(r => r.RequestId != null && dropped.Contains(r.RequestId)))
        {
            record.RequestId = null;
        }

        return true;
    }

    private async Task<bool> SearchAsync(IncompleteRecordingStoreData data, DateTime now, bool searchAll, CancellationToken cancellationToken)
    {
        var changed = false;
        var scheduledAny = false;
        foreach (var request in data.Requests.Where(r => r.State == CompleteShowingState.Waiting).ToList())
        {
            if (!searchAll && !_searchNow.Contains(request.Id))
            {
                continue;
            }

            _searchNow.Remove(request.Id);
            if (now - request.OpenedAt >= IncompleteRecordingRules.RequestLifetime)
            {
                request.State = CompleteShowingState.NotFound;
                request.ClosedAt = now;
                changed = true;
                continue;
            }

            GuideProgram? showing;
            try
            {
                showing = IncompleteRecordingRules.ChooseShowing(request.Episode, _host.FindAirings(request.Episode, now), now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not search the guide for {Series} S{Season}E{Episode}", request.Episode.SeriesName, request.Episode.Season, request.Episode.Episode);
                continue;
            }

            if (showing == null)
            {
                continue;
            }

            // A showing someone already scheduled is followed as it is; no second timer is made.
            var existing = _timers.Any(t => IsLive(t)
                && string.Equals(t.ProgramId, showing.Id, StringComparison.OrdinalIgnoreCase));
            if (!existing)
            {
                var result = await _host.ScheduleProgramAsync(showing.Id, cancellationToken).ConfigureAwait(false);
                if (result == ScheduleResult.Failed)
                {
                    continue;
                }

                scheduledAny = true;
            }

            request.State = CompleteShowingState.Scheduled;
            request.ScheduledProgramId = showing.Id;
            request.ScheduledStart = showing.StartDate;
            request.ScheduledEnd = showing.EndDate;
            request.ScheduledChannelName = showing.ChannelName;
            changed = true;

            _logger.LogInformation(
                "Scheduled a complete showing of {Series} S{Season}E{Episode} at {Start} on {Channel}",
                request.Episode.SeriesName,
                request.Episode.Season,
                request.Episode.Episode,
                showing.StartDate,
                showing.ChannelName);
        }

        // Flipping a cancelled showing back to New raises no timer event, and a showing the server
        // says is already scheduled may be missing from the cache, so reload here.
        if (scheduledAny)
        {
            await ReloadTimersAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private static bool Prune(IncompleteRecordingStoreData data, DateTime now)
    {
        var removed = data.Snapshots.RemoveAll(s =>
            s.JudgedAt != null && now - s.JudgedAt >= IncompleteRecordingRules.SnapshotRetention);

        removed += data.Records.RemoveAll(r =>
            r.ItemIds.Count == 0
            && now - r.JudgedAt >= IncompleteRecordingRules.AttachWindow + IncompleteRecordingRules.SnapshotRetention);

        var referenced = data.Records.Where(r => r.RequestId != null).Select(r => r.RequestId!).ToHashSet();
        removed += data.Requests.RemoveAll(r =>
            !CompleteShowingState.IsOpen(r.State)
            && r.ClosedAt != null
            && now - r.ClosedAt >= IncompleteRecordingRules.ClosedRequestRetention
            && !referenced.Contains(r.Id));

        return removed > 0;
    }

    private async Task<bool> TryAttachAsync(IncompleteRecordingStoreData data, LibraryItem item, DateTime now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var byPath = data.Records.FirstOrDefault(r =>
            !r.Dismissed && !r.ItemIds.Contains(item.Id) && r.FragmentPaths.Contains(item.Path, PathComparer));
        if (byPath != null)
        {
            await AttachAsync(byPath, item, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var completePaths = CompleteFragmentPaths(data);
        if (!IsFreeForEpisodeAttach(data, item, completePaths))
        {
            return false;
        }

        var byEpisode = data.Records
            .Where(r => !r.Dismissed
                && r.ItemIds.Count == 0
                && r.Episode != null
                && now - r.JudgedAt < IncompleteRecordingRules.AttachWindow
                && r.Episode.MatchesLibraryEpisode(item.SeriesName, item.Season, item.Episode, item.EpisodeTitle))
            .OrderBy(r => r.JudgedAt)
            .FirstOrDefault();
        if (byEpisode == null)
        {
            return false;
        }

        await AttachAsync(byEpisode, item, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    // Files of recordings judged complete, which must never be badged by an episode match.
    private static HashSet<string> CompleteFragmentPaths(IncompleteRecordingStoreData data) =>
        data.Snapshots
            .Where(s => s.Complete == true)
            .SelectMany(s => s.Fragments.Select(f => f.Path))
            .ToHashSet(PathComparer);

    private static bool IsFreeForEpisodeAttach(IncompleteRecordingStoreData data, LibraryItem item, HashSet<string> completePaths) =>
        !string.IsNullOrEmpty(item.Path)
        && !completePaths.Contains(item.Path)
        && !data.Records.Any(r => r.ItemIds.Contains(item.Id));

    private async Task AttachAsync(IncompleteRecord record, LibraryItem item, CancellationToken cancellationToken)
    {
        if (record.ItemIds.Contains(item.Id))
        {
            return;
        }

        record.ItemIds.Add(item.Id);
        if (record.Dismissed || record.TaggedItemIds.Contains(item.Id))
        {
            return;
        }

        if (await SetTagSafeAsync(item.Id, true, cancellationToken).ConfigureAwait(false))
        {
            record.TaggedItemIds.Add(item.Id);
        }
    }

    private async Task<bool> SetTagSafeAsync(Guid itemId, bool present, CancellationToken cancellationToken)
    {
        try
        {
            await _host.SetTagAsync(itemId, IncompleteRecordingRules.Tag, present, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not update the incomplete tag on {ItemId}", itemId);
            return false;
        }
    }

    private static CompleteShowingRequest? FindOpenRequest(IncompleteRecordingStoreData data, EpisodeKey episode) =>
        data.Requests.FirstOrDefault(r => CompleteShowingState.IsOpen(r.State) && r.Episode.MatchesAiring(episode));

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private IncompleteRecordingStoreData Load()
    {
        if (_data != null)
        {
            return _data;
        }

        _data = AtomicFile.ReadWithRecovery(_storePath, json => JsonSerializer.Deserialize<IncompleteRecordingStoreData>(json, JsonOptions))
            ?? new IncompleteRecordingStoreData();
        return _data;
    }

    private void Save(IncompleteRecordingStoreData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            AtomicFile.WriteAllText(_storePath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save incomplete recordings to {Path}", _storePath);
        }
    }
}
