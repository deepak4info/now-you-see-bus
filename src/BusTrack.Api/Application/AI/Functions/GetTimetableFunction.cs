using BusTrack.Api;
using BusTrack.Api.Services;

namespace BusTrack.Api.Application.AI.Functions;

public class GetTimetableFunction
{
    private readonly TrackingService _trackingService;

    public GetTimetableFunction(TrackingService trackingService)
    {
        _trackingService = trackingService;
    }

    public async Task<StopTimetableDto?> ExecuteAsync(string stopCodeOrName, DateOnly? date = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stopCodeOrName))
            return null;

        var targetDate = date ?? DateOnly.FromDateTime(IndiaClock.Now());
        return await _trackingService.GetStopTimetableAsync(stopCodeOrName.Trim(), targetDate, ct);
    }
}
