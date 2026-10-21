namespace Moonfin.Server.Services;

/// <summary>The pure decisions behind incomplete recordings, kept apart so they can be tested alone.</summary>
public static class IncompleteRecordingRules
{
    public const string Tag = "DVRIncompleteRecording";

    public const int DefaultThresholdPercent = 90;

    public const int MinThresholdPercent = 50;

    public const int MaxThresholdPercent = 100;

    /// <summary>A later showing is scheduled at most this many times before the request gives up.</summary>
    public const int MaxFailedAttempts = 2;

    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan SafetyNetInterval = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan RequestSearchFallback = TimeSpan.FromDays(1);

    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(60);

    public static readonly TimeSpan AttachWindow = TimeSpan.FromHours(24);

    public static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(7);

    public static readonly TimeSpan ClosedRequestRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// A recording still being written this long after its window closed (someone extended it) is
    /// judged anyway.
    /// </summary>
    public static readonly TimeSpan OverrunLimit = TimeSpan.FromMinutes(30);

    public static int ClampThreshold(int percent) =>
        Math.Clamp(percent, MinThresholdPercent, MaxThresholdPercent);

    /// <summary>Incomplete when fewer minutes were written than the threshold share of the padded schedule.</summary>
    public static bool IsIncomplete(double recordedMinutes, double expectedMinutes, int thresholdPercent)
    {
        if (expectedMinutes <= 0)
        {
            return false;
        }

        return recordedMinutes < ClampThreshold(thresholdPercent) / 100.0 * expectedMinutes;
    }

    /// <summary>
    /// The earliest airing of the episode that starts after now, on any channel, preferring HD when
    /// two start together. This is how the server itself picks among duplicate showings.
    /// </summary>
    public static GuideProgram? ChooseShowing(EpisodeKey episode, IEnumerable<GuideProgram> programs, DateTime now)
    {
        return programs
            .Where(p => p.StartDate > now)
            .Where(p => p.Key is { } key && episode.MatchesAiring(key))
            .OrderBy(p => p.StartDate)
            .ThenByDescending(p => p.IsHD)
            .FirstOrDefault();
    }

    /// <summary>Collapses a request's internal state to what clients show.</summary>
    public static string? DisplayState(CompleteShowingRequest? request) => request?.State switch
    {
        CompleteShowingState.Scheduled => "scheduled",
        CompleteShowingState.Done => "recorded",
        CompleteShowingState.Waiting or CompleteShowingState.GaveUp or CompleteShowingState.NotFound => "notFound",
        _ => null,
    };
}
