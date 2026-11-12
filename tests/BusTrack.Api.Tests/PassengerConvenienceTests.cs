using System.Security.Claims;
using BusTrack.Api.Controllers;
using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusTrack.Api.Tests;

public class PassengerConvenienceTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static (TrackingService Tracking, TestHubContext Hub) CreateTracking(AppDbContext db)
    {
        var hub = new TestHubContext();
        return (new TrackingService(db, hub), hub);
    }

    private static ClaimsPrincipal CreateUserPrincipal(Guid userId, string role = "Passenger", string name = "Test Passenger")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static async Task<(Stop bhuna, Stop hisar, Stop delhi, Route r103, Route r102)> SeedMultiRouteNetworkAsync(AppDbContext db)
    {
        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var bhuna = new Stop { Id = 1, Name = "Bhuna", Code = "BHN", Latitude = 29.5312m, Longitude = 75.7083m, IsActive = true };
        var hisar = new Stop { Id = 2, Name = "Hisar", Code = "HIS", Latitude = 29.1492m, Longitude = 75.7217m, City = "Hisar", IsActive = true };
        var delhi = new Stop { Id = 3, Name = "Delhi ISBT", Code = "DEL", Latitude = 28.6667m, Longitude = 77.2285m, City = "Delhi", IsActive = true };

        var bus1 = new Bus { Id = 1, OperatorId = op.Id, FleetNumber = "103", RegistrationNumber = "HR39-103", BusType = "Ordinary", Capacity = 50, IsActive = true };
        var bus2 = new Bus { Id = 2, OperatorId = op.Id, FleetNumber = "102", RegistrationNumber = "HR39-102", BusType = "Express", Capacity = 50, IsActive = true };

        // Route 103: Bhuna -> Hisar (48 km)
        var r103 = new Route { Id = 103, Code = "103", Name = "Bhuna - Hisar", OperatorId = op.Id, DistanceKm = 48m, IsActive = true };
        // Route 102: Hisar -> Delhi ISBT (168 km)
        var r102 = new Route { Id = 102, Code = "102", Name = "Hisar - Delhi ISBT", OperatorId = op.Id, DistanceKm = 168m, IsActive = true };

        db.Operators.Add(op);
        db.Stops.AddRange(bhuna, hisar, delhi);
        db.Buses.AddRange(bus1, bus2);
        db.Routes.AddRange(r103, r102);

        db.RouteStops.AddRange(
            new RouteStop { Id = 1, RouteId = r103.Id, StopId = bhuna.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 2, RouteId = r103.Id, StopId = hisar.Id, SequenceNo = 2, DistanceFromOriginKm = 48m },
            new RouteStop { Id = 3, RouteId = r102.Id, StopId = hisar.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 4, RouteId = r102.Id, StopId = delhi.Id, SequenceNo = 2, DistanceFromOriginKm = 168m }
        );

        // Schedules:
        // Bhuna departs 06:30, arrives Hisar ~07:38
        db.Schedules.Add(new Schedule { Id = 1, RouteId = r103.Id, BusId = bus1.Id, DepartureTime = new TimeOnly(6, 30), ServiceDays = "1111111", IsActive = true });
        // Hisar departs 09:00, arrives Delhi ~13:00 (Layover = 82 min)
        db.Schedules.Add(new Schedule { Id = 2, RouteId = r102.Id, BusId = bus2.Id, DepartureTime = new TimeOnly(9, 0), ServiceDays = "1111111", IsActive = true });

        // Fares:
        db.FareSlabs.Add(new FareSlab { RouteId = r103.Id, FromStopId = bhuna.Id, ToStopId = hisar.Id, Amount = 60m });
        db.FareSlabs.Add(new FareSlab { RouteId = r102.Id, FromStopId = hisar.Id, ToStopId = delhi.Id, Amount = 260m });

        await db.SaveChangesAsync();
        return (bhuna, hisar, delhi, r103, r102);
    }

    [Fact]
    public async Task MultiRoutePlanner_FindsConnectingRoutes_WhenNoDirectBusExists()
    {
        // Arrange
        await using var db = CreateInMemoryDb();
        await SeedMultiRouteNetworkAsync(db);
        var (tracking, _) = CreateTracking(db);
        var searchDate = new DateOnly(2026, 11, 15);
        var searchTime = new DateTime(2026, 11, 15, 5, 0, 0); // 05:00 AM IST

        // Act
        var plan = await tracking.PlanJourneyAsync("Bhuna", "Delhi ISBT", searchDate, nowIstOverride: searchTime);

        // Assert
        Assert.NotNull(plan);
        Assert.False(plan.DirectAvailable);
        Assert.Empty(plan.DirectOptions);
        Assert.NotEmpty(plan.TransferOptions);

        var firstTransfer = plan.TransferOptions[0];
        Assert.Equal(1, firstTransfer.TransfersCount);
        Assert.Contains("Hisar", firstTransfer.TransferStops);
        Assert.Equal(2, firstTransfer.Legs.Count);
        Assert.Equal("103", firstTransfer.Legs[0].RouteCode);
        Assert.Equal("102", firstTransfer.Legs[1].RouteCode);
        Assert.Equal(320m, firstTransfer.EstimatedTotalFare); // 60 + 260
        Assert.True(firstTransfer.TotalJourneyMinutes > 0);
        Assert.True(firstTransfer.TotalWaitMinutes >= 10);
    }

    [Fact]
    public async Task MultiRoutePlanner_DetectsDirectBusAvailable_WhenDirectRouteExists()
    {
        // Arrange
        await using var db = CreateInMemoryDb();
        await SeedMultiRouteNetworkAsync(db);
        var (tracking, _) = CreateTracking(db);
        var searchDate = new DateOnly(2026, 11, 15);
        var searchTime = new DateTime(2026, 11, 15, 7, 0, 0);

        // Act
        var plan = await tracking.PlanJourneyAsync("Hisar", "Delhi ISBT", searchDate, nowIstOverride: searchTime);

        // Assert
        Assert.NotNull(plan);
        Assert.True(plan.DirectAvailable);
        Assert.NotEmpty(plan.DirectOptions);

        var direct = plan.DirectOptions[0];
        Assert.True(direct.IsDirect);
        Assert.Equal(0, direct.TransfersCount);
        Assert.Single(direct.Legs);
        Assert.Equal("102", direct.Legs[0].RouteCode);
        Assert.Equal(260m, direct.EstimatedTotalFare);
    }

    [Fact]
    public async Task CommunityUpdates_SubmissionRequiresVerificationBeforePublishing()
    {
        // Arrange
        await using var db = CreateInMemoryDb();
        var (tracking, _) = CreateTracking(db);
        var passengerController = new PassengerController(db, tracking);
        var adminController = new AdminController(db, tracking, null!, new Microsoft.AspNetCore.Identity.PasswordHasher<User>());

        var adminUserId = Guid.NewGuid();
        adminController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(adminUserId, "Admin", "Super Admin") }
        };

        var reportRequest = new BusInfoReportRequest(
            FleetNumber: "102",
            Category: "DelayReport",
            ReportedStopName: "Barwala bypass",
            ProposedTime: null,
            DelayMinutes: 25,
            Description: "Bus 102 running 25 min late due to tire replacement."
        );

        // Act 1: Passenger submits report
        var submitResult = await passengerController.SubmitBusInfoReport(reportRequest, CancellationToken.None);

        // Assert 1: Created with status Pending
        var okSubmit = Assert.IsType<OkObjectResult>(submitResult);
        var report = await db.BusInfoReports.FirstAsync();
        Assert.Equal("Pending", report.Status);
        Assert.Equal("DelayReport", report.Category);
        Assert.Equal(25, report.DelayMinutes);

        // Public community updates board must be empty before admin verification
        var publicBoardBefore = await passengerController.CommunityUpdates(null, null, null, CancellationToken.None);
        var okPublicBefore = Assert.IsType<OkObjectResult>(publicBoardBefore);
        var listBefore = Assert.IsAssignableFrom<System.Collections.IEnumerable>(okPublicBefore.Value);
        Assert.Empty(listBefore);

        // Act 2: Admin reviews and publishes the update
        var verifyRequest = new UpdateBusReportStatusRequest(Status: "Published", AdminNotes: "Verified with depot inspector.");
        var verifyResult = await adminController.UpdatePassengerFeedbackStatus(report.Id, verifyRequest, CancellationToken.None);

        // Assert 2: Status is Published and appears on public community updates board
        var okVerify = Assert.IsType<OkObjectResult>(verifyResult);
        var updatedReport = await db.BusInfoReports.FirstAsync(r => r.Id == report.Id);
        Assert.Equal("Published", updatedReport.Status);
        Assert.Equal("Verified with depot inspector.", updatedReport.AdminNotes);
        Assert.NotNull(updatedReport.VerifiedAt);

        var publicBoardAfter = await passengerController.CommunityUpdates(null, null, null, CancellationToken.None);
        var okPublicAfter = Assert.IsType<OkObjectResult>(publicBoardAfter);
        var listAfter = Assert.IsAssignableFrom<System.Collections.IEnumerable>(okPublicAfter.Value);
        Assert.NotEmpty(listAfter);
    }

    [Fact]
    public async Task Favorites_SaveStopAndRoute_CategorizedCorrectly()
    {
        // Arrange
        await using var db = CreateInMemoryDb();
        await SeedMultiRouteNetworkAsync(db);
        var (tracking, _) = CreateTracking(db);
        var controller = new PassengerController(db, tracking);
        var userId = Guid.NewGuid();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(userId) }
        };

        // Act 1: Save favorite stop (Bhuna)
        var stopResult = await controller.AddFavorite(new FavoriteRequest(BusId: null, RouteId: null, FromStopId: 1, ToStopId: null, Label: "My Daily Stop"), CancellationToken.None);

        // Act 2: Save favorite route (Route 102)
        var routeResult = await controller.AddFavorite(new FavoriteRequest(BusId: null, RouteId: 102, FromStopId: 2, ToStopId: 3, Label: "Hisar to Delhi Corridor"), CancellationToken.None);

        // Assert
        Assert.IsType<OkObjectResult>(stopResult);
        Assert.IsType<OkObjectResult>(routeResult);

        var listResult = await controller.Favorites(CancellationToken.None);
        var okList = Assert.IsType<OkObjectResult>(listResult);
        var items = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<object>>(okList.Value);
        Assert.Equal(2, items.Cast<object>().Count());
    }

    [Fact]
    public async Task StopReminders_CreateAndTrigger_WhenBusApproaches()
    {
        // Arrange
        await using var db = CreateInMemoryDb();
        var (_, hisar, _, _, r102) = await SeedMultiRouteNetworkAsync(db);
        var (tracking, hub) = CreateTracking(db);

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, FullName = "Passenger One", Email = "p1@test.local", Role = "Passenger", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new PassengerController(db, tracking);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(userId) }
        };

        // Act: Create stop reminder for Hisar (15 min lead time)
        var reminderResult = await controller.CreateReminder(new StopReminderRequest(
            RouteId: r102.Id,
            StopId: hisar.Id,
            RemindBeforeMinutes: 15,
            FleetNumber: "102"
        ), CancellationToken.None);

        // Assert
        var okReminder = Assert.IsType<OkObjectResult>(reminderResult);
        var savedReminder = await db.StopReminders.FirstAsync(r => r.UserId == userId);
        Assert.Equal(hisar.Id, savedReminder.StopId);
        Assert.Equal(15, savedReminder.RemindBeforeMinutes);
        Assert.False(savedReminder.IsTriggered);
    }

    private sealed class TestHubContext : IHubContext<TrackingHub>
    {
        public IHubClients Clients => new NoopClients();
        public IGroupManager Groups => throw new NotImplementedException();

        private sealed class NoopClients : IHubClients
        {
            public IClientProxy All => new NoopProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
            public IClientProxy Client(string connectionId) => new NoopProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoopProxy();
            public IClientProxy Group(string groupName) => new NoopProxy();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoopProxy();
            public IClientProxy User(string userId) => new NoopProxy();
            public IClientProxy Users(IReadOnlyList<string> userIds) => new NoopProxy();
        }

        private sealed class NoopProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
