using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>An in-memory server: timers, active recordings with growing files, a guide and a library.</summary>
internal sealed class FakeRecordingHost : ILiveTvRecordingHost
{
#pragma warning disable CS0067 // The service subscribes through the watcher, which these tests don't run.
    public event EventHandler? ScheduleChanged;
    public event EventHandler? GuideRefreshed;
    public event EventHandler<LibraryItem>? ItemAdded;
    public event EventHandler<LibraryItem>? ItemUpdated;
    public event EventHandler<Guid>? ItemRemoved;
#pragma warning restore CS0067

    public List<ScheduledTimer> Timers { get; } = new();

    /// <summary>Timer id to the path it is writing right now.</summary>
    public Dictionary<string, string> ActivePaths { get; } = new();

    public Dictionary<string, ActiveRecordingTimer> ActiveTimers { get; } = new();

    public Dictionary<string, long> FileSizes { get; } = new();

    public List<GuideProgram> Programs { get; } = new();

    public List<LibraryItem> Items { get; } = new();

    public List<string> Scheduled { get; } = new();

    public int GetTimersCalls { get; private set; }

    public Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken)
    {
        GetTimersCalls++;
        return Task.FromResult<IReadOnlyList<ScheduledTimer>>(Timers.ToList());
    }

    public string? GetActiveRecordingPath(string timerId) => ActivePaths.GetValueOrDefault(timerId);

    public ActiveRecordingTimer? GetActiveRecordingTimer(string path) => ActiveTimers.GetValueOrDefault(path);

    public long? GetFileLength(string path) => FileSizes.TryGetValue(path, out var size) ? size : null;

    public GuideProgram? GetProgram(string programId) => Programs.FirstOrDefault(p => p.Id == programId);

    public IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after) =>
        Programs.Where(p => p.StartDate > after).ToList();

    public Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken)
    {
        var existing = Timers.FirstOrDefault(t => t.ProgramId == programId);
        if (existing is { Status: "New" or "InProgress" })
        {
            return Task.FromResult(ScheduleResult.AlreadyScheduled);
        }

        Scheduled.Add(programId);
        var program = Programs.First(p => p.Id == programId);
        if (existing != null)
        {
            existing.Status = "New";
        }
        else
        {
            Timers.Add(new ScheduledTimer
            {
                TimerId = "t-" + programId,
                ProgramId = programId,
                ChannelName = program.ChannelName,
                StartDate = program.StartDate,
                EndDate = program.EndDate,
                Status = "New",
            });
        }

        return Task.FromResult(ScheduleResult.Created);
    }

    public LibraryItem? FindItemByPath(string path) => Items.FirstOrDefault(i => i.Path == path);

    public LibraryItem? GetItem(Guid itemId) => Items.FirstOrDefault(i => i.Id == itemId);

    public IReadOnlyList<LibraryItem> FindEpisodes(EpisodeKey episode) =>
        Items.Where(i => episode.MatchesLibraryEpisode(i.SeriesName, i.Season, i.Episode, i.EpisodeTitle)).ToList();

    public Task SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken)
    {
        var item = Items.First(i => i.Id == itemId);
        var tags = item.Tags.Where(t => t != tag).ToList();
        if (present)
        {
            tags.Add(tag);
        }

        item.Tags = tags;
        return Task.CompletedTask;
    }

    public void StartRecording(ScheduledTimer timer, string path, int retryCount = 0, int? seriesSeason = 3, int? seriesEpisode = 7, string? episodeTitle = "Pilot")
    {
        timer.Status = "InProgress";
        ActivePaths[timer.TimerId] = path;
        ActiveTimers[path] = new ActiveRecordingTimer
        {
            RetryCount = retryCount,
            StartDate = timer.StartDate,
            EndDate = timer.EndDate,
            PrePaddingSeconds = retryCount == 0 ? timer.PrePaddingSeconds : 0,
            PostPaddingSeconds = timer.PostPaddingSeconds,
            SeriesId = "SH001",
            Name = "Storage Wars",
            SeasonNumber = seriesSeason,
            EpisodeNumber = seriesEpisode,
            EpisodeTitle = episodeTitle,
        };
        FileSizes.TryAdd(path, 0);
    }

    public void StopRecording(ScheduledTimer timer)
    {
        ActivePaths.Remove(timer.TimerId);
        timer.Status = "Completed";
    }

    /// <summary>Grows every file that is being written.</summary>
    public void Write()
    {
        foreach (var path in ActivePaths.Values)
        {
            FileSizes[path] = FileSizes.GetValueOrDefault(path) + 1_000_000;
        }
    }
}

public sealed class IncompleteRecordingTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-incomplete-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(Start.AddHours(-1));
    private readonly FakeRecordingHost _host = new();
    private readonly IncompleteRecordingSettings _settings = new() { AutoRecordComplete = true };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string StorePath => Path.Combine(_dir, "store.json");

    private IncompleteRecordingService NewService() =>
        new(_host, new NoOpLogger<IncompleteRecordingService>(), _clock, StorePath, () => _settings);

    private ScheduledTimer AddTimer(string id = "t1", string programId = "p1", DateTime? start = null, int minutes = 30)
    {
        var timer = new ScheduledTimer
        {
            TimerId = id,
            ProgramId = programId,
            ChannelName = "KABC",
            StartDate = start ?? Start,
            EndDate = (start ?? Start).AddMinutes(minutes),
            PrePaddingSeconds = 120,
            PostPaddingSeconds = 300,
            Status = "New",
        };
        _host.Timers.Add(timer);
        return timer;
    }

    private GuideProgram AddAiring(string id, DateTime start, string channel = "KABC", bool hd = false, int season = 3, int episode = 7, string? title = "Pilot", string seriesId = "SH001")
    {
        var program = new GuideProgram
        {
            Id = id,
            SeriesId = seriesId,
            SeriesName = "Storage Wars",
            Season = season,
            Episode = episode,
            EpisodeTitle = title,
            StartDate = start,
            EndDate = start.AddMinutes(30),
            ChannelName = channel,
            IsHD = hd,
        };
        _host.Programs.Add(program);
        return program;
    }

    /// <summary>Runs the service once a minute until the clock reaches the given time.</summary>
    private async Task RunUntil(IncompleteRecordingService service, DateTime until, Action<DateTime>? each = null)
    {
        while (_clock.UtcNow < until)
        {
            each?.Invoke(_clock.UtcNow);
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private static IncompleteRecordingStoreData ReadStore(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<IncompleteRecordingStoreData>(File.ReadAllText(path))!;

    // Records a 30-minute show with 2 + 5 minutes of padding, writing only between the given
    // minutes after the window opens.
    private async Task<IncompleteRecordingService> RecordAiring(int fromMinute, int toMinute, string path = "/rec/a.ts", ScheduledTimer? timer = null, IncompleteRecordingService? service = null)
    {
        timer ??= AddTimer();
        service ??= NewService();
        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        await RunUntil(service, timer.WindowEnd.AddMinutes(1), now =>
        {
            var minute = (now - timer.WindowStart).TotalMinutes;
            if (minute >= fromMinute && minute < toMinute)
            {
                if (!_host.ActivePaths.ContainsKey(timer.TimerId))
                {
                    _host.StartRecording(timer, path);
                }
            }
            else if (_host.ActivePaths.ContainsKey(timer.TimerId))
            {
                _host.StopRecording(timer);
            }
        });
        return service;
    }

    [Theory]
    [InlineData(32, 37, true)]
    [InlineData(35, 37, false)]
    [InlineData(33.4, 37, false)]
    [InlineData(33.2, 37, true)]
    public void ThresholdIncludesPadding(double recorded, double expected, bool incomplete)
    {
        // A 30-minute show with 2 + 5 minutes of padding needs 33.3 minutes at 90%.
        Assert.Equal(incomplete, IncompleteRecordingRules.IsIncomplete(recorded, expected, 90));
    }

    [Fact]
    public void ThresholdIsClampedToASaneRange()
    {
        Assert.Equal(50, IncompleteRecordingRules.ClampThreshold(5));
        Assert.Equal(100, IncompleteRecordingRules.ClampThreshold(400));
        Assert.False(IncompleteRecordingRules.IsIncomplete(0, 0, 90));
    }

    [Fact]
    public void EpisodeKeyNeedsSeasonAndEpisodeNumbers()
    {
        Assert.Null(EpisodeKey.Create("SH1", "News", null, 4, null));
        Assert.Null(EpisodeKey.Create("SH1", "News", 1, null, null));
        Assert.Null(EpisodeKey.Create(null, " ", 1, 2, null));
        Assert.NotNull(EpisodeKey.Create(null, "Storage Wars", 1, 2, null));
    }

    [Fact]
    public void EpisodeTitlesAreComparedOnlyWhenBothHaveOne()
    {
        var key = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Pilot")!;
        Assert.True(key.MatchesAiring(EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!));
        Assert.True(key.MatchesAiring(EpisodeKey.Create("SH1", "Storage Wars", 3, 7, " pilot ")!));
        Assert.False(key.MatchesAiring(EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Finale")!));
        Assert.False(key.MatchesAiring(EpisodeKey.Create("SH1", "Storage Wars", 3, 8, "Pilot")!));
    }

    [Fact]
    public void SeriesIdIsComparedWhenBothHaveOneAndTheNameOtherwise()
    {
        var withId = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!;
        Assert.False(withId.MatchesAiring(EpisodeKey.Create("SH2", "Storage Wars", 3, 7, null)!));
        Assert.True(withId.MatchesAiring(EpisodeKey.Create("SH1", "Renamed", 3, 7, null)!));
        Assert.True(withId.MatchesAiring(EpisodeKey.Create(null, "storage wars", 3, 7, null)!));
        Assert.False(withId.MatchesAiring(EpisodeKey.Create(null, "Other Show", 3, 7, null)!));
    }

    [Fact]
    public void ChoosesTheEarliestLaterShowingPreferringHd()
    {
        var key = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!;
        var now = Start;
        GuideProgram Airing(string id, int hoursLater, bool hd, int episode = 7) => new()
        {
            Id = id,
            SeriesId = "SH1",
            SeriesName = "Storage Wars",
            Season = 3,
            Episode = episode,
            StartDate = now.AddHours(hoursLater),
            EndDate = now.AddHours(hoursLater).AddMinutes(30),
            IsHD = hd,
        };

        var programs = new[]
        {
            Airing("past", -1, true),
            Airing("other-episode", 1, true, episode: 8),
            Airing("sd", 2, false),
            Airing("hd", 2, true),
            Airing("later", 5, true),
        };

        Assert.Equal("hd", IncompleteRecordingRules.ChooseShowing(key, programs, now)?.Id);
        Assert.Null(IncompleteRecordingRules.ChooseShowing(key, programs.Take(2), now));
    }

    [Fact]
    public async Task LosingTheEndingFlagsTheRecordingAndSchedulesTheLaterShowing()
    {
        AddAiring("p2", Start.AddDays(2));
        AddAiring("p3", Start.AddDays(1));

        // Writes from the start of the window, but stops 7 minutes before it closes.
        await RecordAiring(0, 30);

        var data = ReadStore(StorePath);
        var record = Assert.Single(data.Records);
        Assert.InRange(record.RecordedMinutes, 29, 31);
        Assert.Equal(37, record.ExpectedMinutes);
        var request = Assert.Single(data.Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Equal("p3", request.ScheduledProgramId);
        Assert.Equal(new[] { "p3" }, _host.Scheduled);
    }

    [Fact]
    public async Task MissingOnlyThePreRollIsNotFlagged()
    {
        AddAiring("p2", Start.AddDays(1));

        await RecordAiring(2, 37);

        var data = ReadStore(StorePath);
        Assert.Empty(data.Records);
        Assert.Empty(_host.Scheduled);
        Assert.True(Assert.Single(data.Snapshots).Complete);
    }

    [Fact]
    public async Task MinutesCountOnlyWhileTheFileGrows()
    {
        var timer = AddTimer();
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/a.ts");

        // The recorder keeps running for the whole window, but the tuner stalls for 15 minutes.
        while (_clock.UtcNow < timer.WindowEnd.AddMinutes(1))
        {
            var minute = (_clock.UtcNow - timer.WindowStart).TotalMinutes;
            if (minute < 10 || minute >= 25)
            {
                _host.Write();
            }

            if (_clock.UtcNow >= timer.WindowEnd)
            {
                _host.StopRecording(timer);
            }

            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var record = Assert.Single(ReadStore(StorePath).Records);
        Assert.InRange(record.RecordedMinutes, 21, 23);
    }

    [Fact]
    public async Task ARetryTakesRuntimeFromTheGuideAndNoPadding()
    {
        var timer = AddTimer();
        AddAiring("p1", Start);
        var service = NewService();
        await service.RunAsync(CancellationToken.None);

        // First seen on a retry: the server has already rewritten the timer's start.
        _clock.UtcNow = Start.AddMinutes(10);
        _host.StartRecording(timer, "/rec/a.ts", retryCount: 1);
        _host.ActiveTimers["/rec/a.ts"].StartDate = _clock.UtcNow;
        _host.Write();
        await service.RunAsync(CancellationToken.None);

        var snapshot = Assert.Single(ReadStore(StorePath).Snapshots);
        Assert.Equal(30, snapshot.RuntimeMinutes);
        Assert.Equal(0, snapshot.PrePaddingMinutes);
        Assert.Equal(0, snapshot.PostPaddingMinutes);
        Assert.Equal(timer.WindowEnd, snapshot.WindowEnd);
    }

    [Fact]
    public async Task AFirstAttemptTakesTimesAndPaddingFromTheTimer()
    {
        var timer = AddTimer();
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/a.ts");
        _host.Write();
        await service.RunAsync(CancellationToken.None);

        var snapshot = Assert.Single(ReadStore(StorePath).Snapshots);
        Assert.Equal(30, snapshot.RuntimeMinutes);
        Assert.Equal(2, snapshot.PrePaddingMinutes);
        Assert.Equal(5, snapshot.PostPaddingMinutes);
        Assert.Equal("Storage Wars", snapshot.Episode?.SeriesName);
    }

    [Fact]
    public async Task AStopAndRestartAddsUpToOneRecordAndOneRequest()
    {
        AddAiring("p2", Start.AddDays(1));
        var timer = AddTimer();
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        await RunUntil(service, timer.WindowEnd.AddMinutes(1), now =>
        {
            var minute = (now - timer.WindowStart).TotalMinutes;
            if (minute == 0)
            {
                _host.StartRecording(timer, "/rec/a.ts");
            }
            else if (minute == 10)
            {
                _host.StopRecording(timer);
            }
            else if (minute == 20)
            {
                _host.StartRecording(timer, "/rec/a-1.ts");
            }
            else if (now >= timer.WindowEnd)
            {
                _host.StopRecording(timer);
            }
        });

        var data = ReadStore(StorePath);
        var record = Assert.Single(data.Records);
        Assert.Equal(new[] { "/rec/a.ts", "/rec/a-1.ts" }, record.FragmentPaths);
        Assert.Single(data.Requests);
        Assert.Single(_host.Scheduled);
    }

    [Fact]
    public async Task ARestartMidRecordingKeepsTheSnapshotAndTheGapDoesntCount()
    {
        AddAiring("p2", Start.AddDays(1));
        var timer = AddTimer();
        var first = NewService();
        await first.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/a.ts");
        await RunUntil(first, timer.WindowStart.AddMinutes(15));

        // The server goes down for ten minutes, then the recording restarts in a new file.
        _host.StopRecording(timer);
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = NewService();
        _host.StartRecording(timer, "/rec/a-1.ts", retryCount: 1);
        await RunUntil(second, timer.WindowEnd, _ => { });
        _host.StopRecording(timer);
        await RunUntil(second, timer.WindowEnd.AddMinutes(1));

        var data = ReadStore(StorePath);
        var record = Assert.Single(data.Records);
        Assert.InRange(record.RecordedMinutes, 25, 28);
        Assert.Equal(2, record.FragmentPaths.Count);
        Assert.Single(data.Requests);
    }

    [Fact]
    public async Task APluginStartedMidRecordingGivesTheUnseenMinutesTheBenefitOfTheDoubt()
    {
        var timer = AddTimer();
        _clock.UtcNow = timer.WindowStart.AddMinutes(20);
        _host.StartRecording(timer, "/rec/a.ts");
        var service = NewService();
        await RunUntil(service, timer.WindowEnd, _ => { });
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        Assert.Empty(ReadStore(StorePath).Records);
    }

    [Fact]
    public async Task AttachesToTheItemAtTheOriginalPathAndTagsItOnce()
    {
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);

        await RecordAiring(0, 20);

        var record = Assert.Single(ReadStore(StorePath).Records);
        Assert.Equal(new[] { item.Id }, record.ItemIds);
        Assert.Equal(new[] { IncompleteRecordingRules.Tag }, item.Tags);
    }

    [Fact]
    public async Task AMovedFileAttachesByEpisodeWithinADay()
    {
        var service = await RecordAiring(0, 20);
        var moved = new LibraryItem
        {
            Id = Guid.NewGuid(),
            Path = "/library/Storage Wars/S03E07.mkv",
            SeriesName = "Storage Wars",
            Season = 3,
            Episode = 7,
            DateCreated = _clock.UtcNow,
        };
        _host.Items.Add(moved);
        _clock.Advance(TimeSpan.FromHours(3));
        await service.OnItemAddedAsync(moved, CancellationToken.None);

        Assert.Equal(new[] { moved.Id }, Assert.Single(ReadStore(StorePath).Records).ItemIds);
        Assert.Contains(IncompleteRecordingRules.Tag, moved.Tags);
    }

    [Fact]
    public async Task NothingAttachesAfterADay()
    {
        var service = await RecordAiring(0, 20);
        _clock.Advance(TimeSpan.FromHours(25));
        var late = new LibraryItem { Id = Guid.NewGuid(), Path = "/library/x.mkv", SeriesName = "Storage Wars", Season = 3, Episode = 7 };
        _host.Items.Add(late);
        await service.OnItemAddedAsync(late, CancellationToken.None);

        Assert.Empty(Assert.Single(ReadStore(StorePath).Records).ItemIds);
        Assert.Empty(late.Tags);
    }

    [Fact]
    public async Task AFileOfACompleteRecordingIsNeverAttachedByEpisode()
    {
        var service = await RecordAiring(0, 20);

        var complete = AddTimer("t2", "p9", start: _clock.UtcNow.AddMinutes(10));
        await RecordAiring(0, 37, "/rec/complete.ts", complete, service);
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/complete.ts", SeriesName = "Storage Wars", Season = 3, Episode = 7 };
        _host.Items.Add(item);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.Empty(item.Tags);
    }

    [Fact]
    public async Task ATagGoneForAnyReasonDismissesTheRecordAndIsNeverAddedBack()
    {
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);
        var service = await RecordAiring(0, 20);

        item.Tags = Array.Empty<string>();
        await service.OnItemUpdatedAsync(item, CancellationToken.None);
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.RunAsync(CancellationToken.None);

        Assert.True(Assert.Single(ReadStore(StorePath).Records).Dismissed);
        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, CancellationToken.None));
    }

    [Fact]
    public async Task KeepAsCompleteDismissesAndRemovesTheTag()
    {
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);
        var service = await RecordAiring(0, 20);

        Assert.True(await service.DismissAsync(item.Id, CancellationToken.None));

        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, CancellationToken.None));
        Assert.False(await service.DismissAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task TheStateShowsTheScheduledShowing()
    {
        var later = AddAiring("p2", Start.AddDays(1), channel: "KABC-HD");
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);
        var service = await RecordAiring(0, 20);

        var state = Assert.Single(await service.GetStatesAsync(new[] { item.Id, Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal("scheduled", state.State);
        Assert.Equal(later.StartDate, state.ScheduledStart);
        Assert.Equal("KABC-HD", state.ChannelName);
        Assert.Equal(37, state.ExpectedMinutes);
    }

    [Fact]
    public async Task ACompleteReplacementMarksTheRequestRecorded()
    {
        var later = AddAiring("p2", Start.AddDays(1));
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);
        var service = await RecordAiring(0, 20);

        var replacement = _host.Timers.Single(t => t.ProgramId == "p2");
        replacement.PrePaddingSeconds = 120;
        replacement.PostPaddingSeconds = 300;
        await RecordAiring(0, 37, "/rec/b.ts", replacement, service);

        var state = Assert.Single(await service.GetStatesAsync(new[] { item.Id }, CancellationToken.None));
        Assert.Equal("recorded", state.State);
        Assert.Equal(CompleteShowingState.Done, Assert.Single(ReadStore(StorePath).Requests).State);
        Assert.Equal(later.Id, _host.Scheduled.Single());
    }

    [Fact]
    public async Task AnIncompleteReplacementTriesOnceMoreThenGivesUp()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2));
        AddAiring("p4", Start.AddDays(3));
        var service = await RecordAiring(0, 20);

        foreach (var programId in new[] { "p2", "p3" })
        {
            var replacement = _host.Timers.Single(t => t.ProgramId == programId);
            await RecordAiring(0, 10, "/rec/" + programId + ".ts", replacement, service);
        }

        var request = Assert.Single(ReadStore(StorePath).Requests);
        Assert.Equal(CompleteShowingState.GaveUp, request.State);
        Assert.Equal(new[] { "p2", "p3" }, _host.Scheduled);
        Assert.Equal(3, ReadStore(StorePath).Records.Count);
    }

    [Fact]
    public async Task AShowingSomeoneAlreadyScheduledIsFollowedWithoutASecondTimer()
    {
        AddAiring("p2", Start.AddDays(1));
        AddTimer("other", "p2", start: Start.AddDays(1));

        await RecordAiring(0, 20);

        Assert.Empty(_host.Scheduled);
        var request = Assert.Single(ReadStore(StorePath).Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Equal("p2", request.ScheduledProgramId);
    }

    [Fact]
    public async Task AGreySeriesShowingIsFlippedToANewTimer()
    {
        AddAiring("p2", Start.AddDays(1));
        var grey = AddTimer("grey", "p2", start: Start.AddDays(1));
        grey.Status = "Cancelled";

        await RecordAiring(0, 20);

        Assert.Equal(new[] { "p2" }, _host.Scheduled);
        Assert.Equal("New", grey.Status);
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(ReadStore(StorePath).Requests).State);
    }

    [Fact]
    public async Task CancellingTheScheduledShowingIsFinal()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2));
        var service = await RecordAiring(0, 20);

        _host.Timers.Single(t => t.ProgramId == "p2").Status = "Cancelled";
        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(CompleteShowingState.CancelledByUser, Assert.Single(ReadStore(StorePath).Requests).State);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);
    }

    [Fact]
    public async Task AShowingTheGuideDroppedIsSearchedForAgain()
    {
        AddAiring("p2", Start.AddDays(1));
        var service = await RecordAiring(0, 20);

        _host.Timers.RemoveAll(t => t.ProgramId == "p2");
        _host.Programs.RemoveAll(p => p.Id == "p2");
        AddAiring("p5", Start.AddDays(4));
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);

        var request = Assert.Single(ReadStore(StorePath).Requests);
        Assert.Equal("p5", request.ScheduledProgramId);
    }

    [Fact]
    public async Task AWaitingRequestIsSearchedAfterGuideRefreshesUntilSixtyDays()
    {
        var service = await RecordAiring(0, 20);
        Assert.Equal(CompleteShowingState.Waiting, Assert.Single(ReadStore(StorePath).Requests).State);

        _clock.Advance(TimeSpan.FromDays(10));
        AddAiring("p2", _clock.UtcNow.AddDays(1));
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(ReadStore(StorePath).Requests).State);
    }

    [Fact]
    public async Task AWaitingRequestExpiresAfterSixtyDays()
    {
        var service = await RecordAiring(0, 20);

        _clock.Advance(TimeSpan.FromDays(61));
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(CompleteShowingState.NotFound, Assert.Single(ReadStore(StorePath).Requests).State);
    }

    [Fact]
    public async Task WithoutTheSettingTheRecordingIsOnlyFlagged()
    {
        _settings.AutoRecordComplete = false;
        AddAiring("p2", Start.AddDays(1));

        await RecordAiring(0, 20);

        var data = ReadStore(StorePath);
        Assert.Single(data.Records);
        Assert.Empty(data.Requests);
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task TurningTheSettingOffDropsWaitingAutomaticRequestsButNotExplicitOnes()
    {
        var service = await RecordAiring(0, 20);
        var other = AddAiring("other-now", _clock.UtcNow.AddMinutes(-5), episode: 9);
        await service.RequestCompleteShowingAsync(other.Id, CancellationToken.None);
        Assert.Equal(2, ReadStore(StorePath).Requests.Count);

        _settings.AutoRecordComplete = false;
        await service.RunAsync(CancellationToken.None);

        var remaining = Assert.Single(ReadStore(StorePath).Requests);
        Assert.True(remaining.Explicit);
        Assert.Null(Assert.Single(ReadStore(StorePath).Records).RequestId);
    }

    [Fact]
    public async Task AnExplicitRequestFoldsWithTheAutomaticOneForTheSameEpisode()
    {
        _settings.AutoRecordComplete = true;
        var now = AddAiring("p1", Start);
        AddAiring("p2", Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(10);

        var service = NewService();
        var request = await service.RequestCompleteShowingAsync(now.Id, CancellationToken.None);
        Assert.Equal(CompleteShowingState.Scheduled, request?.State);

        // The airing being recorded right now then comes out incomplete.
        var timer = AddTimer();
        _host.StartRecording(timer, "/rec/a.ts");
        await RunUntil(service, timer.WindowEnd, _ => { });
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        var data = ReadStore(StorePath);
        var single = Assert.Single(data.Requests);
        Assert.Equal(single.Id, Assert.Single(data.Records).RequestId);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);
    }

    [Fact]
    public async Task AProgramWithoutEpisodeNumbersGetsNoRequest()
    {
        var news = AddAiring("news", Start, season: 0, episode: 0);
        news.Season = null;
        news.Episode = null;

        Assert.Null(await NewService().RequestCompleteShowingAsync("news", CancellationToken.None));
        Assert.Null(await NewService().RequestCompleteShowingAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task AProgramWithoutEpisodeNumbersIsStillFlaggedByPath()
    {
        var timer = AddTimer();
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/news.ts" };
        _host.Items.Add(item);
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/news.ts", seriesSeason: null, seriesEpisode: null);
        await RunUntil(service, timer.WindowStart.AddMinutes(10));
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        var data = ReadStore(StorePath);
        Assert.Equal(new[] { item.Id }, Assert.Single(data.Records).ItemIds);
        Assert.Empty(data.Requests);
    }

    [Fact]
    public async Task DeletingTheIncompleteCopyDropsTheRecordButKeepsTheRequest()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);
        var service = await RecordAiring(0, 20);

        _clock.Advance(TimeSpan.FromHours(30));
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);

        var data = ReadStore(StorePath);
        Assert.Empty(data.Records);
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(data.Requests).State);
    }

    [Fact]
    public async Task TheStoreSurvivesARestart()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = "/rec/a.ts" };
        _host.Items.Add(item);
        await RecordAiring(0, 20);

        var restarted = NewService();
        var state = Assert.Single(await restarted.GetStatesAsync(new[] { item.Id }, CancellationToken.None));
        Assert.Equal("scheduled", state.State);
    }

    [Fact]
    public async Task TheScheduleCacheReloadsOnEventsAndOnTheSafetyNet()
    {
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(1, _host.GetTimersCalls);

        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(2, _host.GetTimersCalls);

        _clock.Advance(IncompleteRecordingRules.SafetyNetInterval);
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(3, _host.GetTimersCalls);
    }

    [Fact]
    public async Task IdleUntilTheNextWindowOpens()
    {
        var timer = AddTimer(start: _clock.UtcNow.AddMinutes(10));
        var service = NewService();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(timer.WindowStart, service.NextRunAt());

        _clock.UtcNow = timer.WindowStart;
        Assert.Equal(_clock.UtcNow + IncompleteRecordingRules.SampleInterval, service.NextRunAt());
    }

    [Fact]
    public async Task WithNothingScheduledOnlyTheSafetyNetIsDue()
    {
        var service = NewService();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(_clock.UtcNow + IncompleteRecordingRules.SafetyNetInterval, service.NextRunAt());
    }
}
