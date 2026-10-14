namespace BusTrack.Api.Services;

public class TripSimulator : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TripSimulator> _logger;

    public TripSimulator(IServiceScopeFactory scopes, ILogger<TripSimulator> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                using var scope = _scopes.CreateScope();
                var tracking = scope.ServiceProvider.GetRequiredService<TrackingService>();
                await tracking.AdvanceSimulations(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Demo playback tick failed");
            }
        }
    }
}
