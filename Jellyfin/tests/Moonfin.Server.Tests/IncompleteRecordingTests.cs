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

    /// <summary>Files on disk and their sizes.</summary>
    public Dictionary<string, long> Files { get; } = new();

    public List<GuideProgram> Programs { get; } = new();

    public List<LibraryItem> Items { get; } = new();

    public HashSet<Guid> Hidden { get; } = new();

    public List<string> Scheduled { get; } = new();

    public int GetTimersCalls { get; private set; }

    /// <summary>Makes the next schedule call create the timer and then fail, as a crash mid-call would.</summary>
    public bool CrashAfterNextSchedule { get; set; }

    public Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken)
    {
        GetTimersCalls++;
        return Task.FromResult<IReadOnlyList<ScheduledTimer>>(Timers.ToList());
    }

    public string? GetActiveRecordingPath(string timerId) => ActivePaths.GetValueOrDefault(timerId);

    public ActiveRecordingTimer? GetActiveRecordingTimer(string path) => ActiveTimers.GetValueOrDefault(path);

    public long? GetFileLength(string path) => Files.TryGetValue(path, out var size) ? size : null;

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
                PrePaddingSeconds = 120,
                PostPaddingSeconds = 300,
                Status = "New",
            });
        }

        if (CrashAfterNextSchedule)
        {
            CrashAfterNextSchedule = false;
            throw new InvalidOperationException("Server went away after creating the timer");
        }

        return Task.FromResult(ScheduleResult.Created);
    }

    public LibraryItem? FindItemByPath(string path) => Items.FirstOrDefault(i => i.Path == path);

    public bool FileExists(string path) => Files.ContainsKey(path);

    public IReadOnlyList<string> FindRenamedSiblings(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        return Files.Keys
            .Where(f => f != path && Path.GetDirectoryName(f) == directory && Path.GetFileNameWithoutExtension(f) == stem)
            .ToList();
    }

    public bool IsVisibleTo(Guid itemId, Guid userId) => !Hidden.Contains(itemId);

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

    public void StartRecording(ScheduledTimer timer, string path, int retryCount = 0, int? season = 3, int? episode = 7, string? episodeTitle = "Pilot")
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
            SeasonNumber = season,
            EpisodeNumber = episode,
            EpisodeTitle = episodeTitle,
        };
        Files.TryAdd(path, 0);
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
            Files[path] = Files.GetValueOrDefault(path) + 1_000_000;
        }
    }

    public LibraryItem AddItem(string path, DateTime created)
    {
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = path, DateCreated = created };
        Items.Add(item);
        Files.TryAdd(path, 1);
        return item;
    }
}

public sealed class IncompleteRecordingTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = Guid.NewGuid();

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

    private IncompleteRecordingStoreData Store =>
        System.Text.Json.JsonSerializer.Deserialize<IncompleteRecordingStoreData>(File.ReadAllText(StorePath))!;

    // A new instance is a new server process.
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

    private GuideProgram AddAiring(string id, DateTime start, string channel = "KABC", bool hd = false, int? season = 3, int? episode = 7, string? title = "Pilot", string? seriesId = "SH001")
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

    /// <summary>Runs the service once a minute until the clock reaches the given time, writing to active files.</summary>
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

    // Records a 30-minute show with 2 + 5 minutes of padding (37 minutes, 33.3 needed at 90%),
    // writing only between the given minutes after the window opens.
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

    private static RecordingFragment Fragment(double coveredMinutes, double unknownMinutes = 0) =>
        new() { CoveredSeconds = coveredMinutes * 60, UnknownSeconds = unknownMinutes * 60 };

    // Rules

    [Theory]
    [InlineData(32, 37, true)]
    [InlineData(35, 37, false)]
    [InlineData(33.4, 37, false)]
    [InlineData(33.2, 37, true)]
    public void ThresholdIncludesPadding(double recorded, double expected, bool incomplete)
    {
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
    public void TwoPartialFilesAreNotACompleteCopy()
    {
        // A 60-minute episode split into two 29-minute files: together 97%, but neither plays whole.
        var verdict = IncompleteRecordingRules.Classify(new[] { Fragment(29), Fragment(29) }, 60, 90);
        Assert.Equal(RecordingVerdict.Incomplete, verdict);
    }

    [Fact]
    public void OneSufficientFileIsComplete()
    {
        Assert.Equal(RecordingVerdict.Complete, IncompleteRecordingRules.Classify(new[] { Fragment(5), Fragment(55) }, 60, 90));
    }

    [Fact]
    public void UnobservedTimeThatCouldDecideTheVerdictMakesItUnknown()
    {
        Assert.Equal(RecordingVerdict.Unknown, IncompleteRecordingRules.Classify(new[] { Fragment(45, 10) }, 60, 90));
        Assert.Equal(RecordingVerdict.Incomplete, IncompleteRecordingRules.Classify(new[] { Fragment(30, 10) }, 60, 90));
        Assert.Equal(RecordingVerdict.Incomplete, IncompleteRecordingRules.Classify(Array.Empty<RecordingFragment>(), 60, 90));
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
    public void ThePersistedKeyIgnoresTheTitleAndNamespacesTheSeries()
    {
        var titled = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Pilot")!;
        var untitled = EpisodeKey.Create("sh1", "Other Name", 3, 7, null)!;
        var byName = EpisodeKey.Create(null, "  Storage   WARS ", 3, 7, null)!;

        Assert.Equal(titled.Id, untitled.Id);
        Assert.Equal("id:sh1|3|7", titled.Id);
        Assert.Equal("name:storage wars|3|7", byName.Id);
    }

    [Fact]
    public void TitlesAreCheckedAgainstTheAnchorNotChained()
    {
        var anchor = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!;
        var pilot = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Pilot")!;
        var finale = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Finale")!;

        // An untitled anchor accepts either, but a titled one never accepts a different title,
        // so "Pilot" and "Finale" can't be joined through the untitled airing.
        Assert.True(anchor.Matches(pilot));
        Assert.True(anchor.Matches(finale));
        Assert.False(pilot.Matches(finale));
        Assert.True(pilot.Matches(anchor));
    }

    [Fact]
    public void SeriesIdIsComparedWhenBothHaveOneAndTheNameOtherwise()
    {
        var withId = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!;
        Assert.False(withId.Matches(EpisodeKey.Create("SH2", "Storage Wars", 3, 7, null)!));
        Assert.True(withId.Matches(EpisodeKey.Create("SH1", "Renamed", 3, 7, null)!));
        Assert.True(withId.Matches(EpisodeKey.Create(null, "storage  wars", 3, 7, null)!));
        Assert.False(withId.Matches(EpisodeKey.Create(null, "Other Show", 3, 7, null)!));
    }

    [Fact]
    public void ChoosesTheEarliestLaterShowingPreferringHd()
    {
        var key = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!;
        GuideProgram Airing(string id, int hoursLater, bool hd, int episode = 7) => new()
        {
            Id = id,
            SeriesId = "SH1",
            SeriesName = "Storage Wars",
            Season = 3,
            Episode = episode,
            StartDate = Start.AddHours(hoursLater),
            EndDate = Start.AddHours(hoursLater).AddMinutes(30),
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

        Assert.Equal("hd", IncompleteRecordingRules.ChooseShowing(key, programs, Start)?.Id);
        Assert.Null(IncompleteRecordingRules.ChooseShowing(key, programs.Take(2), Start));
    }

    // Measuring

    [Fact]
    public async Task LosingTheEndingFlagsTheRecordingAndSchedulesTheLaterShowing()
    {
        AddAiring("p2", Start.AddDays(2));
        AddAiring("p3", Start.AddDays(1));

        await RecordAiring(0, 30);

        var record = Assert.Single(Store.Records);
        Assert.InRange(record.RecordedMinutes, 28.5, 30);
        Assert.Equal(37, record.ExpectedMinutes);
        var request = Assert.Single(Store.Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Equal("p3", request.ScheduledProgramId);
        Assert.Equal(new[] { "p3" }, _host.Scheduled);
    }

    [Fact]
    public async Task AWatchedLateStartIsNotUnobservedTime()
    {
        AddAiring("p2", Start.AddDays(1));

        await RecordAiring(2, 37);

        var snapshot = Assert.Single(Store.Snapshots);
        Assert.Equal(RecordingVerdict.Complete, snapshot.Verdict);
        Assert.InRange(Assert.Single(snapshot.Fragments).UnknownSeconds, 0, 121);
        Assert.Empty(Store.Records);
        Assert.Empty(_host.Scheduled);
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

        var record = Assert.Single(Store.Records);
        Assert.InRange(record.RecordedMinutes, 20, 23);
    }

    [Fact]
    public async Task MissedSamplesAreUnknownAndCanLeaveTheVerdictUnknown()
    {
        var timer = AddTimer();
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/a.ts");
        await RunUntil(service, timer.WindowStart.AddMinutes(10));

        // The file keeps growing, but the plugin misses ten minutes of samples in the same process.
        _host.Write();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunUntil(service, timer.WindowEnd);
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        var snapshot = Assert.Single(Store.Snapshots);
        Assert.Equal(RecordingVerdict.Unknown, snapshot.Verdict);
        Assert.InRange(snapshot.Fragments[0].UnknownSeconds, 600, 800);
        Assert.Empty(Store.Records);
    }

    [Fact]
    public async Task ARetryTakesRuntimeFromTheGuideAndNoPadding()
    {
        var timer = AddTimer();
        AddAiring("p1", Start);
        var service = NewService();
        await service.RunAsync(CancellationToken.None);

        _clock.UtcNow = Start.AddMinutes(10);
        _host.StartRecording(timer, "/rec/a.ts", retryCount: 1);
        _host.ActiveTimers["/rec/a.ts"].StartDate = _clock.UtcNow;
        await service.RunAsync(CancellationToken.None);

        var snapshot = Assert.Single(Store.Snapshots);
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
        await service.RunAsync(CancellationToken.None);

        var snapshot = Assert.Single(Store.Snapshots);
        Assert.Equal(30, snapshot.RuntimeMinutes);
        Assert.Equal(2, snapshot.PrePaddingMinutes);
        Assert.Equal(5, snapshot.PostPaddingMinutes);
        Assert.Equal("id:sh001|3|7", snapshot.Episode?.Id);
    }

    [Fact]
    public async Task AStopAndRestartKeepsSeparateFilesAndBadgesBoth()
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
            else if (minute == 18)
            {
                _host.StopRecording(timer);
            }
            else if (minute == 19)
            {
                _host.StartRecording(timer, "/rec/a-1.ts");
            }
            else if (now >= timer.WindowEnd)
            {
                _host.StopRecording(timer);
            }
        });
        var first = _host.AddItem("/rec/a.ts", _clock.UtcNow);
        var second = _host.AddItem("/rec/a-1.ts", _clock.UtcNow);
        await service.OnItemAddedAsync(first, CancellationToken.None);
        await service.OnItemAddedAsync(second, CancellationToken.None);

        // 18 + 18 minutes would pass if added together; one file each is incomplete.
        var record = Assert.Single(Store.Records);
        Assert.Equal(new[] { "/rec/a.ts", "/rec/a-1.ts" }, record.Fragments.Select(f => f.Path));
        Assert.Contains(IncompleteRecordingRules.Tag, first.Tags);
        Assert.Contains(IncompleteRecordingRules.Tag, second.Tags);
        Assert.Single(Store.Requests);
        Assert.Single(_host.Scheduled);
    }

    [Fact]
    public async Task ARebootGapIsNeverCredited()
    {
        AddAiring("p2", Start.AddDays(1));
        var timer = AddTimer();
        var first = NewService();
        await first.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/a.ts");
        await RunUntil(first, timer.WindowStart.AddMinutes(15));

        // The server is down for ten minutes. The new process's recorder writes to the same file
        // name and the file is bigger than before, but none of the gap counts.
        _host.Write();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = NewService();
        _host.StartRecording(timer, "/rec/a.ts", retryCount: 1);
        await RunUntil(second, timer.WindowEnd);
        _host.StopRecording(timer);
        await RunUntil(second, timer.WindowEnd.AddMinutes(1));

        var fragments = Assert.Single(Store.Records).Fragments;
        Assert.Equal(2, fragments.Count);
        Assert.All(fragments, f => Assert.True(f.CoveredSeconds <= 15 * 60));
        // Each file's only unobserved time is the minute after its last sample.
        Assert.All(fragments, f => Assert.InRange(f.UnknownSeconds, 0, 60));
        Assert.Single(Store.Requests);
    }

    [Fact]
    public async Task ARecordingInterruptedByTheRestartThatInstalledThePluginIsJudgedOnWhatThisProcessSaw()
    {
        // The plugin first sees this recording after the server started, so only what this
        // process observed is counted.
        var timer = AddTimer();
        _clock.UtcNow = timer.WindowStart.AddMinutes(20);
        _host.StartRecording(timer, "/rec/a.ts", retryCount: 1);
        var service = NewService();
        await RunUntil(service, timer.WindowEnd);
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        var snapshot = Assert.Single(Store.Snapshots);
        Assert.InRange(snapshot.Fragments[0].UnknownSeconds, 0, 60);
        Assert.Equal(RecordingVerdict.Incomplete, snapshot.Verdict);
    }

    [Fact]
    public async Task ARecordingTheCacheMissedIsJudgedUnknownRatherThanIncomplete()
    {
        var service = NewService();
        await service.RunAsync(CancellationToken.None);

        // The timer appears only at the safety-net reload, fifteen minutes into its window.
        var timer = AddTimer();
        _host.StartRecording(timer, "/rec/a.ts");
        _clock.UtcNow = timer.WindowStart.AddMinutes(15);
        await RunUntil(service, timer.WindowEnd);
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        Assert.Equal(RecordingVerdict.Unknown, Assert.Single(Store.Snapshots).Verdict);
        Assert.Empty(Store.Records);
    }

    // Attaching

    [Fact]
    public async Task AnItemAddedBeforeTheJudgmentIsReconciledAtJudging()
    {
        // The server refreshes the library as the recording starts, so the item exists first.
        var item = _host.AddItem("/rec/a.ts", Start.AddMinutes(-2));

        await RecordAiring(0, 20);

        Assert.Equal(new[] { item.Id }, Assert.Single(Store.Records).ItemIds);
        Assert.Equal(new[] { IncompleteRecordingRules.Tag }, item.Tags);
    }

    [Fact]
    public async Task AFileRenamedInPlaceAttaches()
    {
        var service = await RecordAiring(0, 20);
        _host.Files.Remove("/rec/a.ts");
        var remuxed = _host.AddItem("/rec/a.mkv", _clock.UtcNow);
        await service.OnItemAddedAsync(remuxed, CancellationToken.None);

        Assert.Equal(new[] { remuxed.Id }, Assert.Single(Store.Records).ItemIds);
        Assert.Contains(IncompleteRecordingRules.Tag, remuxed.Tags);
    }

    [Fact]
    public async Task AnotherCopyOfTheSameEpisodeIsNeverAttached()
    {
        var service = await RecordAiring(0, 20);
        _host.Files.Remove("/rec/a.ts");

        // The same episode, recorded completely somewhere else.
        var elsewhere = _host.AddItem("/library/Storage Wars/S03E07.mkv", _clock.UtcNow);
        await service.OnItemAddedAsync(elsewhere, CancellationToken.None);

        Assert.Empty(Assert.Single(Store.Records).ItemIds);
        Assert.Empty(elsewhere.Tags);
    }

    [Fact]
    public async Task AnAmbiguousRenameIsLeftUnattached()
    {
        var service = await RecordAiring(0, 20);
        _host.Files.Remove("/rec/a.ts");
        _host.Files["/rec/a.srt"] = 1;
        var remuxed = _host.AddItem("/rec/a.mkv", _clock.UtcNow);
        await service.OnItemAddedAsync(remuxed, CancellationToken.None);

        Assert.Empty(remuxed.Tags);
    }

    [Fact]
    public async Task ARenameIsIgnoredWhileTheOriginalStillExists()
    {
        var service = await RecordAiring(0, 20);
        var transcode = _host.AddItem("/rec/a.mkv", _clock.UtcNow);
        await service.OnItemAddedAsync(transcode, CancellationToken.None);

        Assert.Empty(transcode.Tags);
    }

    [Fact]
    public async Task NothingAttachesByRenameAfterADay()
    {
        var service = await RecordAiring(0, 20);
        _clock.Advance(TimeSpan.FromHours(25));
        _host.Files.Remove("/rec/a.ts");
        var late = _host.AddItem("/rec/a.mkv", _clock.UtcNow);
        await service.OnItemAddedAsync(late, CancellationToken.None);

        Assert.Empty(late.Tags);
    }

    [Fact]
    public async Task AMoveAndADeletionBothDetachAndTheRecordLapsesAfterADay()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);

        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        Assert.Single(Store.Records);
        Assert.Empty(Store.Records[0].ItemIds);

        _clock.Advance(TimeSpan.FromHours(25));
        await service.RunAsync(CancellationToken.None);
        Assert.Empty(Store.Records);
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(Store.Requests).State);
    }

    [Fact]
    public async Task ATagGoneForAnyReasonDismissesTheRecordAndIsNeverAddedBack()
    {
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);

        item.Tags = Array.Empty<string>();
        await service.OnItemUpdatedAsync(item, CancellationToken.None);
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.RunAsync(CancellationToken.None);

        Assert.True(Assert.Single(Store.Records).Dismissed);
        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    // Display and permissions

    [Fact]
    public async Task TheStateShowsTheScheduledShowing()
    {
        var later = AddAiring("p2", Start.AddDays(1), channel: "KABC-HD");
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);

        var state = Assert.Single(await service.GetStatesAsync(new[] { item.Id, Guid.NewGuid() }, User, CancellationToken.None));
        Assert.Equal("scheduled", state.State);
        Assert.Equal(later.StartDate, state.ScheduledStart);
        Assert.Equal("KABC-HD", state.ChannelName);
        Assert.Equal(37, state.ExpectedMinutes);
    }

    [Fact]
    public async Task WithoutItemIdsEveryVisibleIncompleteItemIsReturned()
    {
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);

        Assert.Equal(item.Id.ToString("N"), Assert.Single(await service.GetStatesAsync(null, User, CancellationToken.None)).ItemId);

        _host.Hidden.Add(item.Id);
        Assert.Empty(await service.GetStatesAsync(null, User, CancellationToken.None));
        Assert.False(await service.DismissAsync(item.Id, User, CancellationToken.None));
    }

    [Fact]
    public async Task AHiddenProgramCantBeRequested()
    {
        var program = AddAiring("6f1c2c58a1e34c1f9a8b3d2e1f0a9b8c", Start);
        _host.Hidden.Add(Guid.Parse(program.Id));

        var (request, refusal) = await NewService().RequestCompleteShowingAsync(program.Id, User, CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(CompleteShowingRefusal.NotFound, refusal);
    }

    [Fact]
    public async Task AProgramWithoutEpisodeNumbersGetsNoRequest()
    {
        AddAiring("0b9e8c1d2f3a4b5c6d7e8f9a0b1c2d3e", Start, season: null, episode: null);

        var (request, refusal) = await NewService().RequestCompleteShowingAsync("0b9e8c1d2f3a4b5c6d7e8f9a0b1c2d3e", User, CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(CompleteShowingRefusal.NoEpisodeNumbers, refusal);
    }

    [Fact]
    public async Task AProgramWithoutEpisodeNumbersIsStillFlaggedByPath()
    {
        var timer = AddTimer();
        var item = _host.AddItem("/rec/news.ts", Start);
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/news.ts", season: null, episode: null);
        await RunUntil(service, timer.WindowStart.AddMinutes(10));
        _host.StopRecording(timer);
        await RunUntil(service, timer.WindowEnd.AddMinutes(1));

        Assert.Equal(new[] { item.Id }, Assert.Single(Store.Records).ItemIds);
        Assert.Empty(Store.Requests);
    }

    // Requests

    [Fact]
    public async Task ACompleteReplacementMarksTheRequestRecorded()
    {
        var later = AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);

        var replacement = _host.Timers.Single(t => t.ProgramId == "p2");
        await RecordAiring(0, 37, "/rec/b.ts", replacement, service);

        Assert.Equal("recorded", Assert.Single(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None)).State);
        Assert.Equal(CompleteShowingState.Done, Assert.Single(Store.Requests).State);
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

        Assert.Equal(CompleteShowingState.GaveUp, Assert.Single(Store.Requests).State);
        Assert.Equal(new[] { "p2", "p3" }, _host.Scheduled);
        Assert.Equal(3, Store.Snapshots.Count(s => s.Verdict == RecordingVerdict.Incomplete));
    }

    [Fact]
    public async Task AnAdoptedTimerIsFollowedAndUsesAnAttempt()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2));
        var adopted = AddTimer("someone-elses", "p2", start: Start.AddDays(1));
        var service = await RecordAiring(0, 20);

        var request = Assert.Single(Store.Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Equal("p2", request.ScheduledProgramId);
        Assert.Empty(_host.Scheduled);

        // The adopted showing comes out incomplete too, so the next one is scheduled.
        await RecordAiring(0, 10, "/rec/p2.ts", adopted, service);
        request = Assert.Single(Store.Requests);
        Assert.Equal(1, request.FailedAttempts);
        Assert.Equal("p3", request.ScheduledProgramId);
        Assert.Equal(new[] { "p3" }, _host.Scheduled);
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
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(Store.Requests).State);
    }

    [Fact]
    public async Task ACrashAfterTheServerMadeTheTimerDoesntScheduleItTwice()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2));
        _host.CrashAfterNextSchedule = true;
        var timer = AddTimer();
        var service = NewService();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = timer.WindowStart;
        _host.StartRecording(timer, "/rec/a.ts");
        await RunUntil(service, timer.WindowStart.AddMinutes(10));
        _host.StopRecording(timer);
        _clock.UtcNow = timer.WindowEnd;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(CancellationToken.None));
        Assert.Equal("p2", Assert.Single(Store.Requests).PendingProgramId);

        // After the restart the saved intent is reconciled against the server's timers.
        var restarted = NewService();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "p2" }, _host.Scheduled);
        var request = Assert.Single(Store.Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Null(request.PendingProgramId);
    }

    [Fact]
    public async Task AnExplicitRequestIsIdempotent()
    {
        var now = AddAiring("6a1b2c3d4e5f60718293a4b5c6d7e8f9", Start);
        AddAiring("p2", Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(10);
        var service = NewService();

        var (first, _) = await service.RequestCompleteShowingAsync(now.Id, User, CancellationToken.None);
        var (second, _) = await service.RequestCompleteShowingAsync(now.Id, User, CancellationToken.None);

        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(Store.Requests);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);
    }

    [Fact]
    public async Task CancellingTheScheduledShowingIsFinalEvenAfterAGuideRefresh()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2));
        var service = await RecordAiring(0, 20);

        _host.Timers.Single(t => t.ProgramId == "p2").Status = "Cancelled";
        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(CompleteShowingState.CancelledByUser, Assert.Single(Store.Requests).State);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);
    }

    [Fact]
    public async Task ASeriesRuleMovingTheShowingIsFollowedNotTakenAsACancel()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2), hd: true);
        var service = await RecordAiring(0, 20);

        // The series rule cancels the plugin's pick and records the HD airing instead.
        _host.Timers.Single(t => t.ProgramId == "p2").Status = "Cancelled";
        AddTimer("series-hd", "p3", start: Start.AddDays(2));
        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);

        var request = Assert.Single(Store.Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Equal("p3", request.ScheduledProgramId);
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

        Assert.Equal("p5", Assert.Single(Store.Requests).ScheduledProgramId);
    }

    [Fact]
    public async Task AWaitingRequestIsSearchedAfterGuideRefreshes()
    {
        var service = await RecordAiring(0, 20);
        Assert.Equal(CompleteShowingState.Waiting, Assert.Single(Store.Requests).State);

        _clock.Advance(TimeSpan.FromDays(10));
        AddAiring("p2", _clock.UtcNow.AddDays(1));
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(Store.Requests).State);
    }

    [Fact]
    public async Task AWaitingRequestExpiresAfterSixtyDays()
    {
        var service = await RecordAiring(0, 20);

        _clock.Advance(TimeSpan.FromDays(61));
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(CompleteShowingState.NotFound, Assert.Single(Store.Requests).State);
    }

    [Fact]
    public async Task WithoutTheSettingTheRecordingIsOnlyFlagged()
    {
        _settings.AutoRecordComplete = false;
        AddAiring("p2", Start.AddDays(1));

        await RecordAiring(0, 20);

        Assert.Single(Store.Records);
        Assert.Empty(Store.Requests);
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task TurningTheSettingOffKeepsARequestAnExplicitOneFoldedInto()
    {
        var service = await RecordAiring(0, 20);
        Assert.Equal(CompleteShowingState.Waiting, Assert.Single(Store.Requests).State);

        // Someone asks explicitly for the same episode, which joins the automatic request.
        var airing = AddAiring("7d6c5b4a39281706f5e4d3c2b1a09f8e", _clock.UtcNow.AddMinutes(-5));
        await service.RequestCompleteShowingAsync(airing.Id, User, CancellationToken.None);

        _settings.AutoRecordComplete = false;
        await service.RunAsync(CancellationToken.None);

        Assert.True(Assert.Single(Store.Requests).Explicit);
    }

    [Fact]
    public async Task TurningTheSettingOffDropsWaitingAutomaticRequests()
    {
        var service = await RecordAiring(0, 20);
        _settings.AutoRecordComplete = false;
        await service.RunAsync(CancellationToken.None);

        Assert.Empty(Store.Requests);
        Assert.Null(Assert.Single(Store.Records).RequestId);
    }

    [Fact]
    public async Task KeepAsCompleteClosesTheAutomaticRequestAndRemovesTheTag()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);

        Assert.True(await service.DismissAsync(item.Id, User, CancellationToken.None));

        Assert.Empty(item.Tags);
        Assert.Equal(CompleteShowingState.Dismissed, Assert.Single(Store.Requests).State);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    [Fact]
    public async Task KeepAsCompleteLeavesAnExplicitRequestOpen()
    {
        _settings.AutoRecordComplete = false;
        var airing = AddAiring("5e4d3c2b1a09f8e77d6c5b4a39281706", Start);
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts", Start);
        var timer = AddTimer(programId: airing.Id);
        var service = NewService();
        _clock.UtcNow = Start.AddMinutes(1);
        await service.RequestCompleteShowingAsync(airing.Id, User, CancellationToken.None);
        await RecordAiring(1, 20, timer: timer, service: service);

        Assert.True(await service.DismissAsync(item.Id, User, CancellationToken.None));

        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(Store.Requests).State);
    }

    // Storage and scheduling

    [Fact]
    public async Task TheStoreSurvivesARestart()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts", Start);
        await RecordAiring(0, 20);

        var state = Assert.Single(await NewService().GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
        Assert.Equal("scheduled", state.State);
    }

    [Fact]
    public async Task OldRecordsAndRequestsArePruned()
    {
        var item = _host.AddItem("/rec/a.ts", Start);
        var service = await RecordAiring(0, 20);
        await service.DismissAsync(item.Id, User, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(31));
        await service.RunAsync(CancellationToken.None);

        Assert.Empty(Store.Snapshots);
        Assert.Empty(Store.Records);
        Assert.Empty(Store.Requests);
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
