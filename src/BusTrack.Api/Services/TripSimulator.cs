namespace BusTrack.Api.Services;

public class TripSimulator : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TripSimulator> _logger;
    private DateOnly? _openedFor;

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
                var today = IndiaClock.Today();
                if (_openedFor != today && await tracking.OperationsAreLive(stoppingToken))
                {
                    var created = await tracking.MaterializeToday(stoppingToken);
                    _openedFor = today;
                    if (created > 0)
                        _logger.LogInformation("Opened {Count} trips for {Date}", created, today);
                }
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
