using System.Text.Json.Serialization;

namespace Moonfin.Server.Services;

/// <summary>
/// Identifies one episode across airings: the guide's series id (or the exact series name when the
/// guide has none), season and episode number, plus the episode title when both sides have one.
/// </summary>
public sealed class EpisodeKey
{
    public string? SeriesId { get; set; }

    public string SeriesName { get; set; } = string.Empty;

    public int Season { get; set; }

    public int Episode { get; set; }

    public string? EpisodeTitle { get; set; }

    /// <summary>Builds a key, or null when the program has no season and episode numbers.</summary>
    public static EpisodeKey? Create(string? seriesId, string? seriesName, int? season, int? episode, string? episodeTitle)
    {
        if (season is null || episode is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(seriesId) && string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        return new EpisodeKey
        {
            SeriesId = string.IsNullOrWhiteSpace(seriesId) ? null : seriesId,
            SeriesName = seriesName ?? string.Empty,
            Season = season.Value,
            Episode = episode.Value,
            EpisodeTitle = string.IsNullOrWhiteSpace(episodeTitle) ? null : episodeTitle,
        };
    }

    /// <summary>
    /// Same episode as another guide airing. The series id is compared when both have one, the
    /// series name otherwise, the same way series timers match airings.
    /// </summary>
    public bool MatchesAiring(EpisodeKey other)
    {
        var sameSeries = SeriesId != null && other.SeriesId != null
            ? string.Equals(SeriesId, other.SeriesId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(SeriesName, other.SeriesName, StringComparison.OrdinalIgnoreCase);

        return sameSeries && MatchesNumbersAndTitle(other.Season, other.Episode, other.EpisodeTitle);
    }

    /// <summary>
    /// Same episode as a library item. Library episodes carry no guide series id, so this matches by
    /// series name, as the server's own "already in library" check does.
    /// </summary>
    public bool MatchesLibraryEpisode(string? seriesName, int? season, int? episode, string? episodeTitle)
    {
        if (season is null || episode is null
            || !string.Equals(SeriesName, seriesName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesNumbersAndTitle(season.Value, episode.Value, episodeTitle);
    }

    private bool MatchesNumbersAndTitle(int season, int episode, string? episodeTitle)
    {
        if (Season != season || Episode != episode)
        {
            return false;
        }

        return EpisodeTitle == null || string.IsNullOrWhiteSpace(episodeTitle)
            || string.Equals(EpisodeTitle.Trim(), episodeTitle.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One file a recording wrote; a retry or restart of the same airing adds another.</summary>
public sealed class RecordingFragment
{
    public string Path { get; set; } = string.Empty;

    public long LastSize { get; set; }
}

/// <summary>What the plugin captured about one timer while it was recording.</summary>
public sealed class RecordingSnapshot
{
    /// <summary>The service's own timer id, which the recordings manager keys active recordings by.</summary>
    public string TimerId { get; set; } = string.Empty;

    /// <summary>Library id of the guide program, in the "N" format the API uses.</summary>
    public string? ProgramId { get; set; }

    public string? ChannelName { get; set; }

    public DateTime WindowStart { get; set; }

    public DateTime WindowEnd { get; set; }

    public double RuntimeMinutes { get; set; }

    public double PrePaddingMinutes { get; set; }

    public double PostPaddingMinutes { get; set; }

    public EpisodeKey? Episode { get; set; }

    public List<RecordingFragment> Fragments { get; set; } = new();

    public double RecordedMinutes { get; set; }

    public DateTime? LastSampleAt { get; set; }

    public DateTime? JudgedAt { get; set; }

    public bool? Complete { get; set; }

    public double ExpectedMinutes => PrePaddingMinutes + RuntimeMinutes + PostPaddingMinutes;
}

/// <summary>A recording judged incomplete, and the library items it was attached to.</summary>
public sealed class IncompleteRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TimerId { get; set; } = string.Empty;

    public double RecordedMinutes { get; set; }

    public double ExpectedMinutes { get; set; }

    public EpisodeKey? Episode { get; set; }

    public List<string> FragmentPaths { get; set; } = new();

    /// <summary>When the recording ended; items matching by episode attach up to a day after this.</summary>
    public DateTime JudgedAt { get; set; }

    public List<Guid> ItemIds { get; set; } = new();

    /// <summary>Items the tag was added to. A tag missing from one of these means the user dismissed it.</summary>
    public List<Guid> TaggedItemIds { get; set; } = new();

    public string? RequestId { get; set; }

    public bool Dismissed { get; set; }
}

public static class CompleteShowingState
{
    public const string Waiting = "waiting";
    public const string Scheduled = "scheduled";
    public const string Done = "done";
    public const string GaveUp = "gaveUp";
    public const string NotFound = "notFound";
    public const string CancelledByUser = "cancelledByUser";

    public static bool IsOpen(string state) => state is Waiting or Scheduled;
}

/// <summary>A request to record a complete showing of one episode. One open request per episode.</summary>
public sealed class CompleteShowingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public EpisodeKey Episode { get; set; } = new();

    public string State { get; set; } = CompleteShowingState.Waiting;

    /// <summary>Opened by a user from the Record prompt; survives the automatic setting being turned off.</summary>
    public bool Explicit { get; set; }

    public DateTime OpenedAt { get; set; }

    /// <summary>Scheduled showings that recorded incomplete. The second one ends the request.</summary>
    public int FailedAttempts { get; set; }

    public string? ScheduledProgramId { get; set; }

    public DateTime? ScheduledStart { get; set; }

    public DateTime? ScheduledEnd { get; set; }

    public string? ScheduledChannelName { get; set; }

    public DateTime? RecordedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}

/// <summary>Everything the feature persists, kept in one file.</summary>
public sealed class IncompleteRecordingStoreData
{
    public List<RecordingSnapshot> Snapshots { get; set; } = new();

    public List<IncompleteRecord> Records { get; set; } = new();

    public List<CompleteShowingRequest> Requests { get; set; } = new();

    public DateTime? LastRequestSearchAt { get; set; }
}

/// <summary>A timer as the schedule cache holds it.</summary>
public sealed class ScheduledTimer
{
    /// <summary>The service's own timer id (TimerInfoDto.ExternalId).</summary>
    public string TimerId { get; set; } = string.Empty;

    public string? ProgramId { get; set; }

    public string? ChannelName { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public int PrePaddingSeconds { get; set; }

    public int PostPaddingSeconds { get; set; }

    /// <summary>New, InProgress, Cancelled or Completed, as the server reports it.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime WindowStart => StartDate.AddSeconds(-PrePaddingSeconds);

    public DateTime WindowEnd => EndDate.AddSeconds(PostPaddingSeconds);
}

/// <summary>The server's internal timer behind an active recording.</summary>
public sealed class ActiveRecordingTimer
{
    public int RetryCount { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public int PrePaddingSeconds { get; set; }

    public int PostPaddingSeconds { get; set; }

    public string? SeriesId { get; set; }

    public string? Name { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? EpisodeTitle { get; set; }
}

/// <summary>One airing in the guide.</summary>
public sealed class GuideProgram
{
    public string Id { get; set; } = string.Empty;

    public string? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? EpisodeTitle { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public string? ChannelName { get; set; }

    public bool IsHD { get; set; }

    public EpisodeKey? Key => EpisodeKey.Create(SeriesId, SeriesName, Season, Episode, EpisodeTitle);
}

/// <summary>The parts of a library item the attach step looks at.</summary>
public sealed class LibraryItem
{
    public Guid Id { get; set; }

    public string? Path { get; set; }

    public string? SeriesName { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? EpisodeTitle { get; set; }

    public DateTime DateCreated { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
}

public enum ScheduleResult
{
    Created,
    AlreadyScheduled,
    Failed,
}

/// <summary>Server-wide settings for the feature.</summary>
public sealed class IncompleteRecordingSettings
{
    [JsonPropertyName("autoRecordComplete")]
    public bool AutoRecordComplete { get; set; }

    [JsonPropertyName("thresholdPercent")]
    public int ThresholdPercent { get; set; } = IncompleteRecordingRules.DefaultThresholdPercent;
}

/// <summary>What a client shows for one incomplete item.</summary>
public sealed class IncompleteRecordingStateDto
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("recordedMinutes")]
    public double RecordedMinutes { get; set; }

    [JsonPropertyName("expectedMinutes")]
    public double ExpectedMinutes { get; set; }

    /// <summary>scheduled, recorded, notFound, or null when there is nothing to say.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("scheduledStart")]
    public DateTime? ScheduledStart { get; set; }

    [JsonPropertyName("channelName")]
    public string? ChannelName { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTime? RecordedAt { get; set; }
}
