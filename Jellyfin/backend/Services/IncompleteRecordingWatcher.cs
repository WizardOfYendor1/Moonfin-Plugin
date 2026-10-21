using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Runs <see cref="IncompleteRecordingService"/> on the schedule it asks for: once a minute while a
/// recording window is open, otherwise asleep until the next window, the safety-net check or a
/// server event wakes it.
/// </summary>
public sealed class IncompleteRecordingWatcher : BackgroundService
{
    private readonly IncompleteRecordingService _service;
    private readonly ILiveTvRecordingHost _host;
    private readonly ILogger<IncompleteRecordingWatcher> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationToken _stopping;

    public IncompleteRecordingWatcher(
        IncompleteRecordingService service,
        ILiveTvRecordingHost host,
        ILogger<IncompleteRecordingWatcher> logger)
    {
        _service = service;
        _host = host;
        _logger = logger;
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _host.ScheduleChanged += OnScheduleChanged;
        _host.GuideRefreshed += OnGuideRefreshed;
        _host.ItemAdded += OnItemAdded;
        _host.ItemUpdated += OnItemUpdated;
        _host.ItemRemoved += OnItemRemoved;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _service.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Incomplete recordings check failed");
                }

                var delay = _service.NextRunAt() - DateTime.UtcNow;
                if (delay < TimeSpan.FromSeconds(1))
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                await _wake.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        finally
        {
            _host.ScheduleChanged -= OnScheduleChanged;
            _host.GuideRefreshed -= OnGuideRefreshed;
            _host.ItemAdded -= OnItemAdded;
            _host.ItemUpdated -= OnItemUpdated;
            _host.ItemRemoved -= OnItemRemoved;
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already due to wake.
        }
    }

    private void OnScheduleChanged(object? sender, EventArgs e)
    {
        _service.MarkScheduleChanged();
        Wake();
    }

    private void OnGuideRefreshed(object? sender, EventArgs e)
    {
        _service.MarkGuideRefreshed();
        Wake();
    }

    // The service raises library events itself when it saves a tag, possibly on the thread that
    // holds its lock, so each event is handled on its own task instead of the raising thread.
    private void OnItemAdded(object? sender, LibraryItem item) =>
        Handle(() => _service.OnItemAddedAsync(item, _stopping));

    private void OnItemUpdated(object? sender, LibraryItem item) =>
        Handle(() => _service.OnItemUpdatedAsync(item, _stopping));

    private void OnItemRemoved(object? sender, Guid itemId) =>
        Handle(() => _service.OnItemRemovedAsync(itemId, _stopping));

    private void Handle(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Server shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incomplete recordings library event failed");
            }
        });
    }
}
