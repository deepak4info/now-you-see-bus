using System.Text.RegularExpressions;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");
        var connectionString = config.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("Connection string SqlServer is missing.");

        await EnsureDatabaseAsync(connectionString, logger);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
        var geometry = scope.ServiceProvider.GetRequiredService<RouteGeometryService>();

        var dir = FindDatabaseDir();
        if (Directory.Exists(dir))
        {
            try
            {
                await using (var conn = new SqlConnection(connectionString))
                {
                    await OpenWithRetry(conn, logger);
                    var schemaPath = Path.Combine(dir, "001_schema.sql");
                    var seedPath = Path.Combine(dir, "002_seed.sql");
                    if (File.Exists(schemaPath)) await ExecuteScript(conn, schemaPath);
                    if (File.Exists(seedPath)) await ExecuteScript(conn, seedPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Schema script initialization skipped: {Message}", ex.Message);
            }
        }

        await SeedAccountsAsync(db, passwords);
        await SeedDelhiCorridorAsync(db, geometry, logger);
        await SeedOperationsAsync(db);
        await DeduplicateStopsAsync(db, logger);

        var routeIds = await db.Routes.Select(r => r.Id).ToListAsync();
        foreach (var routeId in routeIds)
        {
            var pointsCount = await db.RoutePoints.CountAsync(p => p.RouteId == routeId);
            // Rebuild if route has no points or sparse straight-line chord points (< 30 points)
            if (pointsCount < 30)
            {
                logger.LogInformation("Rebuilding road geometry along actual highways for route {RouteId} (currently {Count} points)...", routeId, pointsCount);
                await geometry.RebuildAsync(routeId);
            }
        }

        logger.LogInformation("WhereIsMyBus database is ready");
    }

    private static async Task EnsureDatabaseAsync(string connectionString, ILogger logger)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var dbName = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(dbName) || !Regex.IsMatch(dbName, "^[A-Za-z0-9_]+$"))
                return;
            builder.InitialCatalog = "master";
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"IF DB_ID(N'{dbName}') IS NULL CREATE DATABASE [{dbName}]", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // On cloud and shared hosting (e.g. SmarterASP.NET, Azure SQL), database is already provisioned
            // and connecting to master is not permitted. Safely skip and proceed.
            logger.LogInformation("Skipping master database check (cloud/shared database host): {Message}", ex.Message);
        }
    }

    private static async Task OpenWithRetry(SqlConnection conn, ILogger logger)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                await conn.OpenAsync();
                return;
            }
            catch (Exception ex) when (attempt < 30)
            {
                logger.LogWarning("SQL Server not ready ({Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static async Task ExecuteScript(SqlConnection conn, string path)
    {
        var sql = await File.ReadAllTextAsync(path);
        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var batch in batches)
        {
            var text = batch.Trim();
            if (text.Length == 0) continue;
            await using var cmd = new SqlCommand(text, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<User> EnsureUser(AppDbContext db, PasswordHasher<User> passwords, string name, string email, string password, string role, string? phone, string? driverCode)
    {
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existing is not null) return existing;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = name,
            Email = email,
            Phone = phone,
            Role = role,
            DriverCode = driverCode,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwords.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Driver> EnsureDriver(AppDbContext db, User user, string license, int operatorId)
    {
        var driver = await db.Drivers.FirstOrDefaultAsync(d => d.UserId == user.Id);
        if (driver is not null) return driver;
        driver = new Driver
        {
            UserId = user.Id,
            LicenseNumber = license,
            OperatorId = operatorId,
            IsActive = true
        };
        db.Drivers.Add(driver);
        await db.SaveChangesAsync();
        return driver;
    }

    private static async Task SeedAccountsAsync(AppDbContext db, PasswordHasher<User> passwords)
    {
        var op = await db.Operators.FirstAsync(o => o.Code == "HR");
        await EnsureUser(db, passwords, "Depot Admin", "admin@whereismybus.local", "Admin@12345", "Admin", "01662-230001", null);
        var rahulUser = await EnsureUser(db, passwords, "Rahul Kumar", "rahul@whereismybus.local", "Driver@12345", "Driver", "9811100102", "DRV-RAHUL");
        var amitUser = await EnsureUser(db, passwords, "Amit Singh", "amit@whereismybus.local", "Driver@12345", "Driver", "9811100118", "DRV-AMIT");
        var meera = await EnsureUser(db, passwords, "Meera Yadav", "meera@whereismybus.local", "Passenger@12345", "Passenger", "9811100201", null);
        var rahul = await EnsureDriver(db, rahulUser, "HR-042026-RAHUL", op.Id);
        var amit = await EnsureDriver(db, amitUser, "HR-042026-AMIT", op.Id);

        var bus102 = await db.Buses.FirstAsync(b => b.FleetNumber == "102");
        var bus118 = await db.Buses.FirstAsync(b => b.FleetNumber == "118");
        if (bus102.AssignedDriverId is null) bus102.AssignedDriverId = rahul.Id;
        if (bus118.AssignedDriverId is null) bus118.AssignedDriverId = amit.Id;

        var route = await db.Routes.FirstAsync(r => r.Code == "101");
        if (!await db.Schedules.AnyAsync(s => s.BusId == bus102.Id))
        {
            db.Schedules.Add(new Schedule
            {
                RouteId = route.Id,
                BusId = bus102.Id,
                DriverId = rahul.Id,
                DepartureTime = new TimeOnly(8, 0),
                ServiceDays = "1111111",
                IsActive = true
            });
        }
        if (!await db.Schedules.AnyAsync(s => s.BusId == bus118.Id))
        {
            db.Schedules.Add(new Schedule
            {
                RouteId = route.Id,
                BusId = bus118.Id,
                DriverId = amit.Id,
                DepartureTime = new TimeOnly(14, 30),
                ServiceDays = "1111111",
                IsActive = true
            });
        }
        await db.SaveChangesAsync();

        if (!await db.Favorites.AnyAsync(f => f.UserId == meera.Id))
        {
            var hisar = await db.Stops.FirstAsync(s => s.Code == "HIS");
            var fatehabad = await db.Stops.FirstAsync(s => s.Code == "FTB");
            db.Favorites.Add(new Favorite
            {
                UserId = meera.Id,
                BusId = bus102.Id,
                RouteId = route.Id,
                FromStopId = hisar.Id,
                ToStopId = fatehabad.Id,
                Label = "Hisar → Fatehabad · Bus 102",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedDelhiCorridorAsync(AppDbContext db, RouteGeometryService geometry, ILogger logger)
    {
        var op = await db.Operators.FirstOrDefaultAsync(o => o.Code == "HR");
        if (op is null) return;

        async Task<Stop> EnsureStop(string code, string name, decimal lat, decimal lng, string city, string address)
        {
            var existing = await db.Stops.FirstOrDefaultAsync(s => s.Code == code || s.Name == name);
            if (existing is not null) return existing;
            var stop = new Stop
            {
                Code = code,
                Name = name,
                Latitude = lat,
                Longitude = lng,
                City = city,
                Address = address,
                IsActive = true
            };
            db.Stops.Add(stop);
            await db.SaveChangesAsync();
            return stop;
        }

        var hisar = await db.Stops.FirstOrDefaultAsync(s => s.Code == "HIS" || s.Name == "Hisar")
            ?? await EnsureStop("HIS", "Hisar", 29.149200m, 75.721700m, "Hisar", "Hisar Bus Stand");
        var hansi = await EnsureStop("HNS", "Hansi", 29.100400m, 75.963500m, "Hansi", "Hansi Bus Stand");
        var mundhal = await EnsureStop("MDL", "Mundhal", 28.988000m, 76.177000m, "Bhiwani", "Mundhal Bus Stand");
        var rohtak = await EnsureStop("ROH", "Rohtak", 28.895500m, 76.606600m, "Rohtak", "Rohtak New Bus Stand");
        var delhi = await EnsureStop("DEL", "Delhi ISBT", 28.666700m, 77.228500m, "Delhi", "Maharana Pratap ISBT, Kashmere Gate");

        var route102 = await db.Routes.FirstOrDefaultAsync(r => r.Code == "102");
        if (route102 is null)
        {
            route102 = new Route
            {
                Code = "102",
                Name = "Hisar - Hansi - Mundhal - Rohtak - Delhi ISBT",
                OperatorId = op.Id,
                DistanceMode = "Auto",
                DistanceKm = 168.00m,
                IsActive = true
            };
            db.Routes.Add(route102);
            await db.SaveChangesAsync();
        }

        if (!await db.RouteStops.AnyAsync(rs => rs.RouteId == route102.Id))
        {
            db.RouteStops.AddRange(
                new RouteStop { RouteId = route102.Id, StopId = hisar.Id, SequenceNo = 1, DistanceFromOriginKm = 0.00m },
                new RouteStop { RouteId = route102.Id, StopId = hansi.Id, SequenceNo = 2, DistanceFromOriginKm = 26.50m },
                new RouteStop { RouteId = route102.Id, StopId = mundhal.Id, SequenceNo = 3, DistanceFromOriginKm = 52.00m },
                new RouteStop { RouteId = route102.Id, StopId = rohtak.Id, SequenceNo = 4, DistanceFromOriginKm = 98.00m },
                new RouteStop { RouteId = route102.Id, StopId = delhi.Id, SequenceNo = 5, DistanceFromOriginKm = 168.00m }
            );
            await db.SaveChangesAsync();
        }

        var bus105 = await db.Buses.FirstOrDefaultAsync(b => b.FleetNumber == "105");
        if (bus105 is null)
        {
            bus105 = new Bus
            {
                OperatorId = op.Id,
                FleetNumber = "105",
                RegistrationNumber = "HR39-1050",
                Model = "Volvo B11R",
                Capacity = 45,
                BusType = "Super Luxury",
                IsActive = true
            };
            db.Buses.Add(bus105);
            await db.SaveChangesAsync();
        }

        if (!await db.Schedules.AnyAsync(s => s.RouteId == route102.Id && s.BusId == bus105.Id))
        {
            var driver = await db.Drivers.FirstOrDefaultAsync(d => d.OperatorId == op.Id);
            db.Schedules.Add(new Schedule
            {
                RouteId = route102.Id,
                BusId = bus105.Id,
                DriverId = driver?.Id,
                DepartureTime = new TimeOnly(9, 0),
                ServiceDays = "1111111",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        if (!await db.FareSlabs.AnyAsync(f => f.RouteId == route102.Id))
        {
            var stopsList = new (int FromId, int ToId, decimal Amt)[]
            {
                (hisar.Id, hansi.Id, 40m), (hisar.Id, mundhal.Id, 75m), (hisar.Id, rohtak.Id, 130m), (hisar.Id, delhi.Id, 260m),
                (hansi.Id, mundhal.Id, 35m), (hansi.Id, rohtak.Id, 90m), (hansi.Id, delhi.Id, 220m),
                (mundhal.Id, rohtak.Id, 55m), (mundhal.Id, delhi.Id, 185m),
                (rohtak.Id, delhi.Id, 130m)
            };
            foreach (var (fId, tId, amt) in stopsList)
            {
                db.FareSlabs.Add(new FareSlab
                {
                    RouteId = route102.Id,
                    FromStopId = fId,
                    ToStopId = tId,
                    Amount = amt,
                    Currency = "INR"
                });
            }
            await db.SaveChangesAsync();
        }

        var ptCount = await db.RoutePoints.CountAsync(p => p.RouteId == route102.Id);
        if (ptCount < 30)
        {
            logger.LogInformation("Building road geometry along NH9 highway for Route 102 (Hisar - Delhi)...");
            await geometry.RebuildAsync(route102.Id);
        }
    }

    private static async Task SeedOperationsAsync(AppDbContext db)
    {
        if (!await db.SystemSettings.AnyAsync(s => s.Key == "OperationsLive"))
        {
            db.SystemSettings.Add(new SystemSetting { Key = "OperationsLive", Value = "false", UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    public static async Task<int> DeduplicateStopsAsync(AppDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var allStops = await db.Stops
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        if (allStops.Count <= 1) return 0;

        var mergedCount = 0;
        var visited = new HashSet<int>();

        var routeStopCounts = await db.RouteStops
            .GroupBy(rs => rs.StopId)
            .Select(g => new { StopId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StopId, x => x.Count, ct);

        for (int i = 0; i < allStops.Count; i++)
        {
            var primary = allStops[i];
            if (visited.Contains(primary.Id) || !primary.IsActive) continue;

            var cluster = new List<Stop> { primary };

            for (int j = i + 1; j < allStops.Count; j++)
            {
                var candidate = allStops[j];
                if (visited.Contains(candidate.Id) || !candidate.IsActive) continue;

                if (StopNormalizer.AreSameStop(
                    primary.Name, candidate.Name,
                    primary.City, candidate.City,
                    (double)primary.Latitude, (double)primary.Longitude,
                    (double)candidate.Latitude, (double)candidate.Longitude))
                {
                    cluster.Add(candidate);
                    visited.Add(candidate.Id);
                }
            }

            if (cluster.Count > 1)
            {
                var canonical = cluster
                    .OrderByDescending(s => routeStopCounts.GetValueOrDefault(s.Id, 0))
                    .ThenBy(s => StopNormalizer.IsDemoOrAutoCode(s.Code) ? 1 : 0)
                    .ThenByDescending(s => s.Name.EndsWith("ar", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(s => s.Id)
                    .First();

                var duplicates = cluster.Where(s => s.Id != canonical.Id).ToList();

                foreach (var dup in duplicates)
                {
                    logger?.LogInformation(
                        "Merging duplicate stop {DupId} ('{DupName}', {DupCode}) into canonical {CanonicalId} ('{CanonicalName}', {CanonicalCode})",
                        dup.Id, dup.Name, dup.Code, canonical.Id, canonical.Name, canonical.Code);

                    var dupRouteStops = await db.RouteStops.Where(rs => rs.StopId == dup.Id).ToListAsync(ct);
                    foreach (var rs in dupRouteStops)
                    {
                        var alreadyHasCanonical = await db.RouteStops.AnyAsync(x => x.RouteId == rs.RouteId && x.StopId == canonical.Id, ct);
                        if (!alreadyHasCanonical)
                            rs.StopId = canonical.Id;
                        else
                            db.RouteStops.Remove(rs);
                    }

                    var dupEtas = await db.TripStopEtas.Where(e => e.StopId == dup.Id).ToListAsync(ct);
                    foreach (var eta in dupEtas)
                    {
                        var alreadyHasCanonicalEta = await db.TripStopEtas.AnyAsync(x => x.TripId == eta.TripId && x.StopId == canonical.Id, ct);
                        if (!alreadyHasCanonicalEta)
                            eta.StopId = canonical.Id;
                        else
                            db.TripStopEtas.Remove(eta);
                    }

                    var fromFavs = await db.Favorites.Where(f => f.FromStopId == dup.Id).ToListAsync(ct);
                    foreach (var f in fromFavs) f.FromStopId = canonical.Id;

                    var toFavs = await db.Favorites.Where(f => f.ToStopId == dup.Id).ToListAsync(ct);
                    foreach (var f in toFavs) f.ToStopId = canonical.Id;

                    var fromFares = await db.FareSlabs.Where(f => f.FromStopId == dup.Id).ToListAsync(ct);
                    foreach (var f in fromFares)
                    {
                        var exists = await db.FareSlabs.AnyAsync(x => x.RouteId == f.RouteId && x.FromStopId == canonical.Id && x.ToStopId == f.ToStopId, ct);
                        if (!exists && canonical.Id != f.ToStopId) f.FromStopId = canonical.Id; else db.FareSlabs.Remove(f);
                    }

                    var toFares = await db.FareSlabs.Where(f => f.ToStopId == dup.Id).ToListAsync(ct);
                    foreach (var f in toFares)
                    {
                        var exists = await db.FareSlabs.AnyAsync(x => x.RouteId == f.RouteId && x.FromStopId == f.FromStopId && x.ToStopId == canonical.Id, ct);
                        if (!exists && f.FromStopId != canonical.Id) f.ToStopId = canonical.Id; else db.FareSlabs.Remove(f);
                    }

                    if (string.IsNullOrWhiteSpace(canonical.City) && !string.IsNullOrWhiteSpace(dup.City))
                        canonical.City = dup.City;
                    if (string.IsNullOrWhiteSpace(canonical.Address) && !string.IsNullOrWhiteSpace(dup.Address))
                        canonical.Address = dup.Address;

                    dup.IsActive = false;
                    mergedCount++;
                }
            }
        }

        if (mergedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger?.LogInformation("Successfully merged {Count} duplicate stops in database.", mergedCount);
        }

        return mergedCount;
    }

    private static string FindDatabaseDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "database");
            if (File.Exists(Path.Combine(candidate, "001_schema.sql"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find database/001_schema.sql");
    }
}
