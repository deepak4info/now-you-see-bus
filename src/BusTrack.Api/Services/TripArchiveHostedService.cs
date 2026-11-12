using BusTrack.Api.Data;

namespace BusTrack.Api.Services;

/// <summary>
/// Periodically moves aged completed/cancelled trips and their GPS pings from the primary catalog database
/// into configured telemetry/archive shards when sharding and periodic archival are enabled.
/// </summary>
public class TripArchiveHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IShardedDataService _shards;
    private readonly ILogger<TripArchiveHostedService> _logger;

    public TripArchiveHostedService(
        IServiceScopeFactory scopes,
        IShardedDataService shards,
        ILogger<TripArchiveHostedService> logger)
    {
        _scopes = scopes;
        _shards = shards;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_shards.IsEnabled || _shards.Router.Options.ArchiveIntervalMinutes <= 0)
            return;

        // Wait briefly so initial startup and DbInitializer finish first.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _shards.Router.Options.ArchiveIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled trip archival run failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task<TripArchiveResultDto> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracking = scope.ServiceProvider.GetRequiredService<TrackingService>();
        var cutoff = IndiaClock.Today().AddDays(-_shards.Router.Options.RetentionDays);
        var result = await _shards.ArchiveCompletedTripsAsync(db, cutoff, ct);
        if (result.ArchivedTrips > 0)
        {
            await tracking.InvalidateTripLifecycleCachesAsync(ct);
            _logger.LogInformation(
                "Archived {TripCount} historical trip(s) and {PingCount} GPS ping(s) across {ShardCount} shard(s) (cutoff < {Cutoff}).",
                result.ArchivedTrips,
                result.MovedGpsPings,
                result.ShardsWritten,
                cutoff);
        }
        return result;
    }
}
