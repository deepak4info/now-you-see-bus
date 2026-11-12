using System.Security.Claims;
using BusTrack.Api.Auth;
using BusTrack.Api.Controllers;
using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Route = BusTrack.Api.Data.Route;

namespace BusTrack.Api.Tests;

public class DistributedCacheTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static TransitCacheService CreateCache(IDistributedCache? distributed = null) =>
        new(
            distributed ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<TransitCacheService>.Instance,
            isRedisConfigured: false);

    [Fact]
    public async Task TransitCacheService_HitsCacheOnRepeatRead_CachesNulls_AndInvalidatesByDomain()
    {
        var cache = CreateCache();
        var calls = 0;

        var first = await cache.GetOrCreateAsync(
            CacheDomains.Stops,
            "sample",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                calls++;
                return Task.FromResult("Tohana -> Bhuna");
            });

        var second = await cache.GetOrCreateAsync(
            CacheDomains.Stops,
            "sample",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                calls++;
                return Task.FromResult("Updated value");
            });

        Assert.Equal("Tohana -> Bhuna", first);
        Assert.Equal("Tohana -> Bhuna", second);
        Assert.Equal(1, calls);

        // Null values are wrapped so repeated null lookups still count as cache HITs.
        var nullCalls = 0;
        var null1 = await cache.GetOrCreateAsync<decimal?>(
            CacheDomains.Fares,
            "missing-fare",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                nullCalls++;
                return Task.FromResult<decimal?>(null);
            });
        var null2 = await cache.GetOrCreateAsync<decimal?>(
            CacheDomains.Fares,
            "missing-fare",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                nullCalls++;
                return Task.FromResult<decimal?>(99m);
            });
        Assert.Null(null1);
        Assert.Null(null2);
        Assert.Equal(1, nullCalls);

        // Domain invalidation rotates version token in O(1), forcing a fresh read on Stops without disturbing Fares.
        await cache.InvalidateDomainAsync(CacheDomains.Stops);

        var third = await cache.GetOrCreateAsync(
            CacheDomains.Stops,
            "sample",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                calls++;
                return Task.FromResult("Updated value");
            });
        Assert.Equal("Updated value", third);
        Assert.Equal(2, calls);

        var null3 = await cache.GetOrCreateAsync<decimal?>(
            CacheDomains.Fares,
            "missing-fare",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                nullCalls++;
                return Task.FromResult<decimal?>(99m);
            });
        Assert.Null(null3);
        Assert.Equal(1, nullCalls);
    }

    [Fact]
    public async Task TransitCacheService_WhenDistributedCacheThrows_FallsBackToDatabaseCleanly()
    {
        var cache = CreateCache(new FailingDistributedCache());
        var calls = 0;

        var value = await cache.GetOrCreateAsync(
            CacheDomains.Routes,
            "catalog",
            TimeSpan.FromMinutes(5),
            _ =>
            {
                calls++;
                return Task.FromResult(42);
            });

        Assert.Equal(42, value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TrackingService_SearchRouteAsync_AndFares_ServeFromCacheUntilInvalidated()
    {
        await using var db = CreateInMemoryDb();
        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var tohana = new Stop { Id = 1, Name = "Tohana", Code = "THN", Latitude = 29.71m, Longitude = 75.90m, IsActive = true };
        var bhuna = new Stop { Id = 2, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.71m, IsActive = true };
        var bus = new Bus
        {
            Id = 1,
            OperatorId = op.Id,
            Operator = op,
            FleetNumber = "HR-22-1001",
            RegistrationNumber = "HR22GA1001",
            BusType = "Express",
            Capacity = 52,
            IsActive = true
        };
        var user = new User { Id = Guid.NewGuid(), FullName = "Ravi", Email = "ravi@test.local", Role = "Driver", IsActive = true };
        var driver = new Driver { Id = 1, UserId = user.Id, User = user, LicenseNumber = "HR-DRV-1", IsActive = true };
        var route = new Route { Id = 1, Code = "THN-BHN", Name = "Tohana - Bhuna", OperatorId = op.Id, IsActive = true };

        db.Operators.Add(op);
        db.Stops.AddRange(tohana, bhuna);
        db.Buses.Add(bus);
        db.Users.Add(user);
        db.Drivers.Add(driver);
        db.Routes.Add(route);
        db.RouteStops.AddRange(
            new RouteStop { Id = 1, RouteId = route.Id, StopId = tohana.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 2, RouteId = route.Id, StopId = bhuna.Id, SequenceNo = 2, DistanceFromOriginKm = 24m }
        );
        db.FareSlabs.Add(new FareSlab { Id = 1, RouteId = route.Id, FromStopId = tohana.Id, ToStopId = bhuna.Id, Amount = 35m });
        db.Schedules.Add(new Schedule
        {
            Id = 1,
            RouteId = route.Id,
            BusId = bus.Id,
            DriverId = driver.Id,
            DepartureTime = new TimeOnly(10, 0),
            ServiceDays = "1111111",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var cache = CreateCache();
        var tracking = new TrackingService(db, new NoopHubContext(), cache);
        var futureDate = IndiaClock.Today().AddDays(3);

        // 1. Cold cache: reads SQL Server and populates Stops + Fares + Schedules search cache.
        var initial = await tracking.SearchRouteAsync("Tohana", "Bhuna", futureDate);
        Assert.Single(initial.Results);
        Assert.Equal(35m, initial.Results[0].Fare);

        // 2. Mutate Fare directly in DB without invalidating cache -> repeat search must hit cache and still see ₹35.
        var slab = await db.FareSlabs.FirstAsync();
        slab.Amount = 50m;
        await db.SaveChangesAsync();

        var cachedHit = await tracking.SearchRouteAsync("tohana", "BHN", futureDate);
        Assert.Single(cachedHit.Results);
        Assert.Equal(35m, cachedHit.Results[0].Fare);

        // 3. Invalidate Fares + Schedules -> next search observes updated ₹50 fare from SQL Server.
        await tracking.InvalidateFareCachesAsync();

        var refreshed = await tracking.SearchRouteAsync("Tohana", "Bhuna", futureDate);
        Assert.Single(refreshed.Results);
        Assert.Equal(50m, refreshed.Results[0].Fare);
    }

    [Fact]
    public async Task AuthController_Me_CachesUserProfile_AndInvalidatesOnUpdate()
    {
        await using var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            FullName = "Deepak Sharma",
            Email = "deepak@test.local",
            Role = "Passenger",
            Phone = "9876543210",
            IsActive = true,
            PasswordHash = "hash"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var cache = CreateCache();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "WhereIsMyBus-Test-Signing-Key-1234567890",
                ["Jwt:Issuer"] = "WhereIsMyBus",
                ["Jwt:Audience"] = "WhereIsMyBus"
            })
            .Build();
        var controller = new AuthController(db, new PasswordHasher<User>(), new JwtIssuer(config), cache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
                        "Bearer"))
                }
            }
        };

        var res1 = Assert.IsType<OkObjectResult>(await controller.Me(CancellationToken.None));
        Assert.Contains("Deepak Sharma", System.Text.Json.JsonSerializer.Serialize(res1.Value));

        // Direct DB modification returns cached profile until explicit user profile invalidation.
        user.FullName = "Deepak Updated";
        await db.SaveChangesAsync();

        var res2 = Assert.IsType<OkObjectResult>(await controller.Me(CancellationToken.None));
        Assert.Contains("Deepak Sharma", System.Text.Json.JsonSerializer.Serialize(res2.Value));

        await cache.RemoveDirectAsync(TransitCacheService.UserProfileKey(userId));
        var res3 = Assert.IsType<OkObjectResult>(await controller.Me(CancellationToken.None));
        Assert.Contains("Deepak Updated", System.Text.Json.JsonSerializer.Serialize(res3.Value));
    }

    [Fact]
    public async Task TrackingService_RoutesCatalog_AndLiveFeed_CacheAndInvalidateOnTripLifecycle()
    {
        await using var db = CreateInMemoryDb();
        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var origin = new Stop { Id = 1, Name = "Tohana", Code = "THN", Latitude = 29.71m, Longitude = 75.90m, IsActive = true };
        var dest = new Stop { Id = 2, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.71m, IsActive = true };
        var bus = new Bus
        {
            Id = 1,
            OperatorId = op.Id,
            Operator = op,
            FleetNumber = "HR-22-2002",
            RegistrationNumber = "HR22GA2002",
            BusType = "Express",
            Capacity = 52,
            IsActive = true
        };
        var user = new User { Id = Guid.NewGuid(), FullName = "Suresh", Email = "suresh@test.local", Role = "Driver", IsActive = true };
        var driver = new Driver { Id = 1, UserId = user.Id, User = user, LicenseNumber = "HR-DRV-2", IsActive = true };
        var route = new Route { Id = 1, Code = "THN-BHN", Name = "Tohana - Bhuna", OperatorId = op.Id, IsActive = true };
        var schedule = new Schedule
        {
            Id = 1,
            RouteId = route.Id,
            Route = route,
            BusId = bus.Id,
            Bus = bus,
            DriverId = driver.Id,
            Driver = driver,
            DepartureTime = new TimeOnly(8, 0),
            ServiceDays = "1111111",
            IsActive = true
        };
        var trip = new Trip
        {
            Id = 1,
            ScheduleId = schedule.Id,
            Schedule = schedule,
            BusId = bus.Id,
            Bus = bus,
            DriverId = driver.Id,
            Driver = driver,
            RouteId = route.Id,
            Route = route,
            ServiceDate = IndiaClock.Today(),
            Status = "Scheduled"
        };

        db.Operators.Add(op);
        db.Stops.AddRange(origin, dest);
        db.Buses.Add(bus);
        db.Users.Add(user);
        db.Drivers.Add(driver);
        db.Routes.Add(route);
        db.RouteStops.AddRange(
            new RouteStop { Id = 1, RouteId = route.Id, StopId = origin.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 2, RouteId = route.Id, StopId = dest.Id, SequenceNo = 2, DistanceFromOriginKm = 24m }
        );
        db.Schedules.Add(schedule);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var cache = CreateCache();
        var tracking = new TrackingService(db, new NoopHubContext(), cache);

        // Route catalog caches active routes until invalidated.
        var catalog1 = await tracking.GetActiveRouteCatalogAsync();
        Assert.Equal("Tohana - Bhuna", Assert.Single(catalog1).Name);

        route.Name = "Tohana - Bhuna Express";
        await db.SaveChangesAsync();

        var catalog2 = await tracking.GetActiveRouteCatalogAsync();
        Assert.Equal("Tohana - Bhuna", Assert.Single(catalog2).Name);

        await tracking.InvalidateRouteCachesAsync();
        var catalog3 = await tracking.GetActiveRouteCatalogAsync();
        Assert.Equal("Tohana - Bhuna Express", Assert.Single(catalog3).Name);

        // Live feed starts empty (cached), then StartAsync invalidates Live cache immediately so LiveAsync sees the running trip.
        Assert.Empty(await tracking.LiveAsync());
        await tracking.StartAsync(trip, "gps", user.Id);
        Assert.Single(await tracking.LiveAsync());

        // Ending the trip invalidates Live + Recent + Schedules caches cleanly.
        await tracking.EndAsync(trip, user.Id);
        Assert.Empty(await tracking.LiveAsync());
        var recent = await tracking.GetRecentTrackingAsync();
        Assert.Equal(1, recent.TotalCount);
    }

    [Fact]
    public void ParseRedisConfiguration_SupportsUriAndConnectionStringFormats()
    {
        var uriOptions = TransitCacheService.ParseRedisConfiguration("rediss://default:s3cr3t@redis.example.com:6380");
        Assert.True(uriOptions.Ssl);
        Assert.Equal("s3cr3t", uriOptions.Password);
        Assert.False(uriOptions.AbortOnConnectFail);
        Assert.Single(uriOptions.EndPoints);

        var stdOptions = TransitCacheService.ParseRedisConfiguration("localhost:6379,password=my-pass");
        Assert.Equal("my-pass", stdOptions.Password);
        Assert.False(stdOptions.AbortOnConnectFail);
        Assert.InRange(stdOptions.ConnectTimeout, 1, 3000);
    }

    private sealed class FailingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException("Redis unavailable");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromException<byte[]?>(new TimeoutException("Redis unavailable"));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => throw new TimeoutException("Redis unavailable");
        public Task RemoveAsync(string key, CancellationToken token = default) =>
            Task.FromException(new TimeoutException("Redis unavailable"));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            throw new TimeoutException("Redis unavailable");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            Task.FromException(new TimeoutException("Redis unavailable"));
    }

    private sealed class NoopHubContext : IHubContext<TrackingHub>
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
