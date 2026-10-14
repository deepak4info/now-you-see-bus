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
        await using (var conn = new SqlConnection(connectionString))
        {
            await OpenWithRetry(conn, logger);
            await ExecuteScript(conn, Path.Combine(dir, "001_schema.sql"));
            await ExecuteScript(conn, Path.Combine(dir, "002_seed.sql"));
        }

        await SeedAccountsAsync(db, passwords);
        await SeedOperationsAsync(db);

        var routeIds = await db.Routes.Select(r => r.Id).ToListAsync();
        foreach (var routeId in routeIds)
        {
            if (!await db.RoutePoints.AnyAsync(p => p.RouteId == routeId))
                await geometry.RebuildAsync(routeId);
        }

        logger.LogInformation("WhereIsMyBus database is ready");
    }

    private static async Task EnsureDatabaseAsync(string connectionString, ILogger logger)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var dbName = builder.InitialCatalog;
        if (!Regex.IsMatch(dbName, "^[A-Za-z0-9_]+$"))
            throw new InvalidOperationException("Database name is not valid.");
        builder.InitialCatalog = "master";
        await using var conn = new SqlConnection(builder.ConnectionString);
        await OpenWithRetry(conn, logger);
        await using var cmd = new SqlCommand($"IF DB_ID(N'{dbName}') IS NULL CREATE DATABASE [{dbName}]", conn);
        await cmd.ExecuteNonQueryAsync();
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

    private static async Task SeedOperationsAsync(AppDbContext db)
    {
        if (!await db.SystemSettings.AnyAsync(s => s.Key == "OperationsLive"))
        {
            db.SystemSettings.Add(new SystemSetting { Key = "OperationsLive", Value = "false", UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
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
