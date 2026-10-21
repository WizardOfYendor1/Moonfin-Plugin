using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// The server calls the incomplete recordings feature makes, behind one small surface so its logic
/// can be tested without a running server.
/// </summary>
public interface ILiveTvRecordingHost
{
    /// <summary>A timer or series timer was created or cancelled.</summary>
    event EventHandler? ScheduleChanged;

    /// <summary>The server's Refresh Guide task finished.</summary>
    event EventHandler? GuideRefreshed;

    event EventHandler<LibraryItem>? ItemAdded;

    event EventHandler<LibraryItem>? ItemUpdated;

    event EventHandler<Guid>? ItemRemoved;

    /// <summary>All timers the server knows, including cancelled and completed ones.</summary>
    Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken);

    /// <summary>The file a timer is writing right now, or null when it isn't recording.</summary>
    string? GetActiveRecordingPath(string timerId);

    ActiveRecordingTimer? GetActiveRecordingTimer(string path);

    /// <summary>Current size of a file, or null when it doesn't exist.</summary>
    long? GetFileLength(string path);

    GuideProgram? GetProgram(string programId);

    /// <summary>Guide airings of a series starting after a time, on every channel.</summary>
    IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after);

    /// <summary>Schedules a one-off recording of a program with the server's timer defaults.</summary>
    Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken);

    LibraryItem? FindItemByPath(string path);

    bool FileExists(string path);

    /// <summary>
    /// Files beside a recording with the same name and another extension: what a post-processing
    /// script leaves when it remuxes or transcodes in place.
    /// </summary>
    IReadOnlyList<string> FindRenamedSiblings(string path);

    /// <summary>Whether a user may see a library item or guide program, parental limits and channel access included.</summary>
    bool IsVisibleTo(Guid itemId, Guid userId);

    Task SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken);
}

/// <summary>The Jellyfin implementation of <see cref="ILiveTvRecordingHost"/>.</summary>
public sealed class JellyfinLiveTvRecordingHost : ILiveTvRecordingHost, IDisposable
{
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly ILiveTvManager _liveTvManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<JellyfinLiveTvRecordingHost> _logger;

    public JellyfinLiveTvRecordingHost(
        ILiveTvManager liveTvManager,
        IRecordingsManager recordingsManager,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IServiceProvider services,
        ILogger<JellyfinLiveTvRecordingHost> logger)
    {
        _services = services;
        _liveTvManager = liveTvManager;
        _recordingsManager = recordingsManager;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;

        _liveTvManager.TimerCreated += OnTimerEvent;
        _liveTvManager.TimerCancelled += OnTimerEvent;
        _liveTvManager.SeriesTimerCreated += OnTimerEvent;
        _liveTvManager.SeriesTimerCancelled += OnTimerEvent;
        _taskManager.TaskCompleted += OnTaskCompleted;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
    }

    public event EventHandler? ScheduleChanged;

    public event EventHandler? GuideRefreshed;

    public event EventHandler<LibraryItem>? ItemAdded;

    public event EventHandler<LibraryItem>? ItemUpdated;

    public event EventHandler<Guid>? ItemRemoved;

    public void Dispose()
    {
        _liveTvManager.TimerCreated -= OnTimerEvent;
        _liveTvManager.TimerCancelled -= OnTimerEvent;
        _liveTvManager.SeriesTimerCreated -= OnTimerEvent;
        _liveTvManager.SeriesTimerCancelled -= OnTimerEvent;
        _taskManager.TaskCompleted -= OnTaskCompleted;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
    }

    public async Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken)
    {
        var result = await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false);
        return result.Items
            .Where(t => !string.IsNullOrEmpty(t.ExternalId))
            .Select(t => new ScheduledTimer
            {
                TimerId = t.ExternalId,
                ProgramId = t.ProgramId,
                ChannelName = t.ChannelName,
                StartDate = t.StartDate,
                EndDate = t.EndDate,
                PrePaddingSeconds = t.PrePaddingSeconds,
                PostPaddingSeconds = t.PostPaddingSeconds,
                Status = t.Status.ToString(),
            })
            .ToList();
    }

    public string? GetActiveRecordingPath(string timerId) => _recordingsManager.GetActiveRecordingPath(timerId);

    public ActiveRecordingTimer? GetActiveRecordingTimer(string path)
    {
        var timer = _recordingsManager.GetActiveRecordingInfo(path)?.Timer;
        if (timer == null)
        {
            return null;
        }

        return new ActiveRecordingTimer
        {
            RetryCount = timer.RetryCount,
            StartDate = timer.StartDate,
            EndDate = timer.EndDate,
            PrePaddingSeconds = timer.PrePaddingSeconds,
            PostPaddingSeconds = timer.PostPaddingSeconds,
            SeriesId = timer.SeriesId,
            Name = timer.Name,
            SeasonNumber = timer.SeasonNumber,
            EpisodeNumber = timer.EpisodeNumber,
            EpisodeTitle = timer.EpisodeTitle,
        };
    }

    public long? GetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public GuideProgram? GetProgram(string programId)
    {
        if (!Guid.TryParse(programId, out var id))
        {
            return null;
        }

        return _libraryManager.GetItemById(id) is LiveTvProgram program ? ToGuideProgram(program, new()) : null;
    }

    public IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            MinStartDate = after,
            ParentIndexNumber = episode.Season,
            IndexNumber = episode.Episode,
        };

        if (episode.SeriesId != null)
        {
            query.ExternalSeriesId = episode.SeriesId;
        }
        else
        {
            query.Name = episode.SeriesName;
        }

        var channels = new Dictionary<Guid, BaseItem?>();
        return _libraryManager.GetItemList(query)
            .OfType<LiveTvProgram>()
            .Select(p => ToGuideProgram(p, channels))
            .ToList();
    }

    public async Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken)
    {
        try
        {
            var defaults = await _liveTvManager.GetNewTimerDefaults(programId, cancellationToken).ConfigureAwait(false);

            // The defaults come back as a series timer; the timer DTO shares its base, so the
            // shared fields carry over.
            var timer = JsonSerializer.Deserialize<TimerInfoDto>(JsonSerializer.Serialize<BaseTimerInfoDto>(defaults));
            if (timer == null)
            {
                return ScheduleResult.Failed;
            }

            await _liveTvManager.CreateTimer(timer, cancellationToken).ConfigureAwait(false);
            return ScheduleResult.Created;
        }
        catch (ArgumentException)
        {
            // The server refuses a program that already has an active timer.
            return ScheduleResult.AlreadyScheduled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not schedule program {ProgramId}", programId);
            return ScheduleResult.Failed;
        }
    }

    public LibraryItem? FindItemByPath(string path)
    {
        var item = _libraryManager.FindByPath(path, false);
        return item == null ? null : ToLibraryItem(item);
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> FindRenamedSiblings(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            return Directory.EnumerateFiles(directory)
                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public bool IsVisibleTo(Guid itemId, Guid userId)
    {
        var item = _libraryManager.GetItemById(itemId);
        var user = UserReflection.ResolveUser(_services, userId);
        return item != null && user != null && UserReflection.IsVisibleTo(item, user);
    }

    public async Task SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return;
        }

        var tags = item.Tags ?? Array.Empty<string>();
        var has = tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        if (has == present)
        {
            return;
        }

        item.Tags = present
            ? tags.Append(tag).ToArray()
            : tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    private GuideProgram ToGuideProgram(LiveTvProgram program, Dictionary<Guid, BaseItem?> channels)
    {
        if (!channels.TryGetValue(program.ChannelId, out var channel))
        {
            channel = program.ChannelId == Guid.Empty ? null : _libraryManager.GetItemById(program.ChannelId);
            channels[program.ChannelId] = channel;
        }

        return new GuideProgram
        {
            Id = program.Id.ToString("N"),
            SeriesId = program.ExternalSeriesId,
            SeriesName = program.Name,
            Season = program.ParentIndexNumber,
            Episode = program.IndexNumber,
            EpisodeTitle = program.EpisodeTitle,
            StartDate = program.StartDate,
            EndDate = program.EndDate ?? program.StartDate,
            ChannelName = channel?.Name,
            IsHD = channel?.IsHD ?? false,
        };
    }

    private static LibraryItem ToLibraryItem(BaseItem item)
    {
        return new LibraryItem
        {
            Id = item.Id,
            Path = item.Path,
            DateCreated = item.DateCreated,
            Tags = item.Tags ?? Array.Empty<string>(),
        };
    }

    private void OnTimerEvent(object? sender, EventArgs e) => ScheduleChanged?.Invoke(this, EventArgs.Empty);

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Task?.ScheduledTask?.Key, RefreshGuideTaskKey, StringComparison.Ordinal))
        {
            GuideRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { IsVirtualItem: false, IsFolder: false })
        {
            ItemAdded?.Invoke(this, ToLibraryItem(e.Item));
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { IsVirtualItem: false, IsFolder: false })
        {
            ItemUpdated?.Invoke(this, ToLibraryItem(e.Item));
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item != null)
        {
            ItemRemoved?.Invoke(this, e.Item.Id);
        }
    }
}
