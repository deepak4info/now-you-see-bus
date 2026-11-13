using BusTrack.Api.Application.AI.DTOs;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Application.AI.Functions;

public class GetAdminAnalyticsFunction
{
    private readonly AppDbContext _db;
    private readonly TrackingService _trackingService;

    public GetAdminAnalyticsFunction(AppDbContext db, TrackingService trackingService)
    {
        _db = db;
        _trackingService = trackingService;
    }

    public async Task<AiAdminAnalyticsDto> ExecuteAsync(CancellationToken ct = default)
    {
        var totalBuses = await _db.Buses.CountAsync(ct);
        var activeBuses = await _db.Buses.CountAsync(b => b.IsActive, ct);

        var totalRoutes = await _db.Routes.CountAsync(ct);
        var activeRoutes = await _db.Routes.CountAsync(r => r.IsActive, ct);

        var totalSchedules = await _db.Schedules.CountAsync(s => s.IsActive, ct);

        // Find routes that have no active schedules
        var routesWithSchedules = await _db.Schedules
            .Where(s => s.IsActive)
            .Select(s => s.RouteId)
            .Distinct()
            .ToListAsync(ct);

        var routesWithoutSchedules = await _db.Routes
            .Where(r => r.IsActive && !routesWithSchedules.Contains(r.Id))
            .Select(r => $"{r.Code} - {r.Name}")
            .Take(10)
            .ToListAsync(ct);

        // Find inactive buses
        var inactiveBuses = await _db.Buses
            .Where(b => !b.IsActive)
            .Select(b => $"{b.FleetNumber} ({b.RegistrationNumber})")
            .Take(10)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(IndiaClock.Now());
        var tripsToday = await _db.Trips.CountAsync(t => t.ServiceDate == today, ct);
        var activeTripsNow = await _db.Trips.CountAsync(t => t.ServiceDate == today && (t.Status == "Running" || t.Status == "Active"), ct);

        // Get live tracking to count delayed buses
        var liveSnapshots = await _trackingService.GetLiveTrackingAsync(null, null, ct);
        var delayedBusesCount = liveSnapshots.Count(b => b.DelayMinutes.GetValueOrDefault() > 10);

        var summary = $"System Status: {activeBuses}/{totalBuses} buses active. {activeRoutes}/{totalRoutes} routes active with {totalSchedules} schedules. {tripsToday} trips scheduled today ({activeTripsNow} currently running). {delayedBusesCount} buses experiencing delay >10m.";

        return new AiAdminAnalyticsDto(
            TotalBuses: totalBuses,
            ActiveBuses: activeBuses,
            TotalRoutes: totalRoutes,
            ActiveRoutes: activeRoutes,
            TotalSchedules: totalSchedules,
            RoutesWithoutSchedules: routesWithoutSchedules,
            InactiveBuses: inactiveBuses,
            DelayedBusesCount: delayedBusesCount,
            TotalTripsToday: tripsToday,
            ActiveTripsNow: activeTripsNow,
            Summary: summary
        );
    }
}
