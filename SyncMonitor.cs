namespace SyncthingNotifier;

internal sealed class SyncMonitor : IDisposable
{
    private const double CompleteThreshold = 99.999d;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, double> _lastCompletions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingFolders = new(StringComparer.Ordinal);
    private Task? _monitorTask;

    public event Action<string>? Synchronized;
    public event Action<string>? StatusChanged;

    public void Start()
    {
        if (_monitorTask is not null)
        {
            return;
        }

        _monitorTask = Task.Run(() => RunAsync(_cancellation.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await MonitorConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                StatusChanged?.Invoke($"Syncthing unavailable: {exception.Message} Retrying in 15 seconds.");
                await Task.Delay(RetryInterval, cancellationToken);
            }
        }
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        var configuration = SyncthingConfigurationReader.Load();
        using var client = new SyncthingApiClient(configuration);
        _lastCompletions.Clear();
        _pendingFolders.Clear();

        // Establish a baseline so starting the notifier does not announce every already-synced folder.
        var baseline = await client.GetCompletionsAsync(configuration.Folders.Keys, cancellationToken);
        foreach (var completion in baseline)
        {
            _lastCompletions[completion.Key] = completion.Value;
        }

        StatusChanged?.Invoke($"Monitoring {configuration.Folders.Count} Syncthing folder(s).");
        var monitorStartedAt = DateTimeOffset.UtcNow;
        long lastEventId = 0;
        var eventsTask = client.GetEventsAsync(lastEventId, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var pollTask = Task.Delay(PollInterval, cancellationToken);
            var completedTask = await Task.WhenAny(eventsTask, pollTask);

            if (completedTask == eventsTask)
            {
                var events = await eventsTask;
                foreach (var syncEvent in events.OrderBy(item => item.Id))
                {
                    lastEventId = Math.Max(lastEventId, syncEvent.Id);

                    // Syncthing retains an event buffer. Ignore events that happened before this run.
                    if (syncEvent.Time is { } eventTime && eventTime >= monitorStartedAt)
                    {
                        await ProcessEventAsync(syncEvent, configuration, client, cancellationToken);
                    }
                }

                eventsTask = client.GetEventsAsync(lastEventId, cancellationToken);
            }
            else
            {
                await RefreshCompletionsAsync(configuration, client, cancellationToken);
            }
        }
    }

    private async Task ProcessEventAsync(
        SyncthingEvent syncEvent,
        SyncthingConfiguration configuration,
        SyncthingApiClient client,
        CancellationToken cancellationToken)
    {
        var folderId = syncEvent.FolderId;
        if (string.IsNullOrWhiteSpace(folderId) || !configuration.Folders.ContainsKey(folderId))
        {
            return;
        }

        if (string.Equals(syncEvent.Type, "StateChanged", StringComparison.Ordinal))
        {
            if (string.Equals(syncEvent.State, "idle", StringComparison.OrdinalIgnoreCase))
            {
                await NotifyIfCompleteAsync(folderId, configuration, client, cancellationToken);
            }
            else
            {
                _pendingFolders.Add(folderId);
            }

            return;
        }

        if (string.Equals(syncEvent.Type, "FolderCompletion", StringComparison.Ordinal))
        {
            if (syncEvent.Completion is { } completion && completion < CompleteThreshold)
            {
                _pendingFolders.Add(folderId);
            }
            else if (_pendingFolders.Contains(folderId))
            {
                await NotifyIfCompleteAsync(folderId, configuration, client, cancellationToken);
            }
        }
    }

    private async Task RefreshCompletionsAsync(
        SyncthingConfiguration configuration,
        SyncthingApiClient client,
        CancellationToken cancellationToken)
    {
        var completions = await client.GetCompletionsAsync(configuration.Folders.Keys, cancellationToken);
        foreach (var (folderId, completion) in completions)
        {
            var wasIncomplete = _lastCompletions.TryGetValue(folderId, out var previous) && previous < CompleteThreshold;
            _lastCompletions[folderId] = completion;

            if (wasIncomplete && completion >= CompleteThreshold)
            {
                Notify(folderId, configuration);
            }
        }
    }

    private async Task NotifyIfCompleteAsync(
        string folderId,
        SyncthingConfiguration configuration,
        SyncthingApiClient client,
        CancellationToken cancellationToken)
    {
        var completion = await client.GetCompletionAsync(folderId, cancellationToken);
        _lastCompletions[folderId] = completion;
        if (completion >= CompleteThreshold && _pendingFolders.Remove(folderId))
        {
            Notify(folderId, configuration);
        }
    }

    private void Notify(string folderId, SyncthingConfiguration configuration)
    {
        _pendingFolders.Remove(folderId);
        Synchronized?.Invoke($"{configuration.Folders[folderId]} - synchronized");
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
