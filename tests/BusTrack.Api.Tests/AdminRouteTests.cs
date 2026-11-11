using System.Net;
using System.Security.Claims;
using BusTrack.Api.Controllers;
using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusTrack.Api.Tests;

public class AdminRouteTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static AdminController CreateController(AppDbContext db, HttpMessageHandler? httpHandler = null)
    {
        var httpClient = new HttpClient(httpHandler ?? new StubFailureHandler())
        {
            BaseAddress = new Uri("https://router.project-osrm.org/")
        };
        var roads = new RoadRouter(httpClient);
        var geometry = new RouteGeometryService(db, roads);
        var tracking = new TrackingService(db, new NoopHubContext());
        var controller = new AdminController(db, tracking, geometry, new PasswordHasher<User>());
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Admin")
            },
            "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static async Task<(Operator op, Stop s1, Stop s2, Stop s3)> SeedBasicGraphAsync(AppDbContext db)
    {
        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var s1 = new Stop { Id = 10, Name = "Fatehabad", Code = "FTB", Latitude = 29.5150m, Longitude = 75.4550m, IsActive = true };
        var s2 = new Stop { Id = 11, Name = "Agroha", Code = "AGR", Latitude = 29.3350m, Longitude = 75.6250m, IsActive = true };
        var s3 = new Stop { Id = 12, Name = "Hisar", Code = "HSR", Latitude = 29.1492m, Longitude = 75.7217m, IsActive = true };
        db.Operators.Add(op);
        db.Stops.AddRange(s1, s2, s3);
        await db.SaveChangesAsync();
        return (op, s1, s2, s3);
    }

    [Fact]
    public async Task CreateRoute_ValidAutoRequestWhenOsrmUnavailable_CreatesRouteStopsPointsAndFares()
    {
        await using var db = CreateInMemoryDb();
        var (op, s1, s2, s3) = await SeedBasicGraphAsync(db);
        var controller = CreateController(db);

        var request = new RouteRequest(
            Code: "FTB-HSR-01",
            Name: "Fatehabad to Hisar",
            OperatorId: op.Id,
            StopIds: new List<int> { s1.Id, s2.Id, s3.Id },
            DistanceMode: "Auto",
            SegmentDistancesKm: new List<double>()
        );

        var result = await controller.CreateRoute(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        Assert.Equal(1, await db.Routes.CountAsync());
        Assert.Equal(3, await db.RouteStops.CountAsync());
        Assert.Equal(3, await db.FareSlabs.CountAsync());
        Assert.True(await db.RoutePoints.CountAsync() >= 2);
        Assert.Equal(1, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateRoute_NullOperatorId_SucceedsCleanly()
    {
        await using var db = CreateInMemoryDb();
        var (_, s1, s2, _) = await SeedBasicGraphAsync(db);
        var controller = CreateController(db);

        var request = new RouteRequest(
            Code: "NO-OP-01",
            Name: "No Operator Route",
            OperatorId: null,
            StopIds: new List<int> { s1.Id, s2.Id },
            DistanceMode: "Auto"
        );

        var result = await controller.CreateRoute(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateRoute_CodeLongerThan20Chars_ReturnsBadRequestInsteadOfSql500()
    {
        await using var db = CreateInMemoryDb();
        var (op, s1, s2, _) = await SeedBasicGraphAsync(db);
        var controller = CreateController(db);

        var request = new RouteRequest(
            Code: "FATEHABAD-TO-HISAR-2026",
            Name: "Long code test",
            OperatorId: op.Id,
            StopIds: new List<int> { s1.Id, s2.Id },
            DistanceMode: "Auto"
        );

        var result = await controller.CreateRoute(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, await db.Routes.CountAsync());
    }

    [Fact]
    public async Task CreateRoute_InvalidOrInactiveOperatorId_ReturnsBadRequestInsteadOfFk500()
    {
        await using var db = CreateInMemoryDb();
        var (_, s1, s2, _) = await SeedBasicGraphAsync(db);
        db.Operators.Add(new Operator { Id = 99, Name = "Inactive Op", Code = "INACT", IsActive = false });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var nonexistent = new RouteRequest("R-999", "Test Route", 999, new List<int> { s1.Id, s2.Id });
        Assert.IsType<BadRequestObjectResult>(await controller.CreateRoute(nonexistent, CancellationToken.None));

        var inactive = new RouteRequest("R-99", "Test Route", 99, new List<int> { s1.Id, s2.Id });
        Assert.IsType<BadRequestObjectResult>(await controller.CreateRoute(inactive, CancellationToken.None));
    }

    [Fact]
    public async Task CreateRoute_DuplicateCodeCaseInsensitive_ReturnsBadRequest()
    {
        await using var db = CreateInMemoryDb();
        var (op, s1, s2, _) = await SeedBasicGraphAsync(db);
        var controller = CreateController(db);

        var first = new RouteRequest("RT-101", "Route One", op.Id, new List<int> { s1.Id, s2.Id });
        Assert.IsType<OkObjectResult>(await controller.CreateRoute(first, CancellationToken.None));

        var second = new RouteRequest("rt-101", "Route Duplicate", op.Id, new List<int> { s1.Id, s2.Id });
        Assert.IsType<BadRequestObjectResult>(await controller.CreateRoute(second, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateRoute_RebuildsStopsAndPointsWithoutSequenceConflicts()
    {
        await using var db = CreateInMemoryDb();
        var (op, s1, s2, s3) = await SeedBasicGraphAsync(db);
        var controller = CreateController(db);

        var createReq = new RouteRequest("UPD-01", "Initial Route", op.Id, new List<int> { s1.Id, s2.Id });
        await controller.CreateRoute(createReq, CancellationToken.None);
        var routeId = await db.Routes.Select(r => r.Id).FirstAsync();

        var updateReq = new RouteRequest(
            Code: "UPD-01",
            Name: "Updated Route",
            OperatorId: op.Id,
            StopIds: new List<int> { s1.Id, s2.Id, s3.Id },
            DistanceMode: "Manual",
            SegmentDistancesKm: new List<double> { 22.5, 24.0 }
        );

        var updateRes = await controller.UpdateRoute(routeId, updateReq, CancellationToken.None);
        Assert.IsType<OkObjectResult>(updateRes);

        var updatedRoute = await db.Routes.FirstAsync(r => r.Id == routeId);
        Assert.Equal("Updated Route", updatedRoute.Name);
        Assert.Equal(46.50m, updatedRoute.DistanceKm);
        Assert.Equal(3, await db.RouteStops.CountAsync(rs => rs.RouteId == routeId));
        Assert.Equal(3, await db.FareSlabs.CountAsync(f => f.RouteId == routeId));
    }

    [Fact]
    public async Task CreateRoute_MassiveOsrmPolyline_CapsRoutePointsAndPreservesEndpoints()
    {
        await using var db = CreateInMemoryDb();
        var (op, s1, s2, _) = await SeedBasicGraphAsync(db);

        // Build a synthetic OSRM GeoJSON response with 2400 spread coordinates
        var coordsJson = string.Join(",",
            Enumerable.Range(0, 2400).Select(i =>
            {
                var lng = 75.45 + (i * 0.001);
                var lat = 29.51 - (i * 0.001);
                return $"[{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
            }));
        var payload = $"{{\"code\":\"Ok\",\"routes\":[{{\"legs\":[{{\"distance\":35000}}],\"geometry\":{{\"coordinates\":[{coordsJson}]}}}}]}}";

        var controller = CreateController(db, new StubJsonHandler(payload));
        var req = new RouteRequest("CAP-01", "Long Polyline Route", op.Id, new List<int> { s1.Id, s2.Id });
        var result = await controller.CreateRoute(req, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var points = await db.RoutePoints.OrderBy(p => p.SequenceNo).ToListAsync();
        Assert.Equal(1500, points.Count);
        Assert.Equal(29.510000m, points[0].Latitude);
        Assert.Equal(75.450000m, points[0].Longitude);
        Assert.Equal(Math.Round((decimal)(29.51 - 2399 * 0.001), 6), points[^1].Latitude);
    }

    private class StubFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private class StubJsonHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubJsonHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private class NoopHubContext : IHubContext<TrackingHub>
    {
        public IHubClients Clients => new NoopClients();
        public IGroupManager Groups => throw new NotImplementedException();

        private class NoopClients : IHubClients
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

        private class NoopProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }
}
