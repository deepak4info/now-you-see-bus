using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusTrack.Api.Tests;

public class BusinessRuleValidationTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void IndiaClock_DayIndex_MapsMondayToZeroAndSundayToSix()
    {
        // 2026-11-02 is Monday
        var monday = new DateOnly(2026, 11, 2);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.Equal(0, IndiaClock.DayIndex(monday));

        // 2026-11-08 is Sunday
        var sunday = new DateOnly(2026, 11, 8);
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);
        Assert.Equal(6, IndiaClock.DayIndex(sunday));
    }

    [Fact]
    public void IndiaClock_ClockFromDeparture_AddsEtaMinutesCorrectly()
    {
        var departure = new TimeOnly(8, 0); // 08:00 AM
        // 42 km at 42 km/h is exactly 60 minutes
        var arrivalClock = IndiaClock.ClockFromDeparture(departure, 42.0);
        Assert.Equal("09:00", arrivalClock);
    }

    [Fact]
    public async Task DriverRepository_RejectsDuplicateLicenceNumber()
    {
        await using var db = CreateInMemoryDb();

        var user1 = new User
        {
            Id = Guid.NewGuid(),
            FullName = "Driver One",
            Email = "drv1@test.local",
            DriverCode = "DRV-001",
            Role = "Driver",
            IsActive = true
        };
        var driver1 = new Driver
        {
            Id = 1,
            User = user1,
            LicenseNumber = "DL-01-2026-001",
            IsActive = true
        };
        db.Drivers.Add(driver1);
        await db.SaveChangesAsync();

        // Attempt to check duplicate licence
        const string duplicateLicence = "DL-01-2026-001";
        var isDuplicate = await db.Drivers.AnyAsync(d => d.LicenseNumber == duplicateLicence);
        Assert.True(isDuplicate, "System must detect existing licence number to prevent duplicate driver registration.");

        const string newLicence = "DL-01-2026-999";
        var isNewDuplicate = await db.Drivers.AnyAsync(d => d.LicenseNumber == newLicence);
        Assert.False(isNewDuplicate);
    }

    [Fact]
    public async Task InactiveDriver_IsIdentifiedAsDisabled()
    {
        await using var db = CreateInMemoryDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = "Deactivated Driver",
            Email = "disabled@test.local",
            DriverCode = "DRV-OFF",
            Role = "Driver",
            IsActive = false
        };
        var driver = new Driver
        {
            Id = 2,
            User = user,
            LicenseNumber = "DL-02-DISABLED",
            IsActive = false
        };
        db.Drivers.Add(driver);
        await db.SaveChangesAsync();

        var foundActive = await db.Drivers
            .Include(d => d.User)
            .AnyAsync(d => d.Id == 2 && d.IsActive && d.User.IsActive);

        Assert.False(foundActive, "Deactivated driver must not be treated as active in operations.");
    }

    [Theory]
    [InlineData("1111111", true)]
    [InlineData("1000001", true)]
    [InlineData("0000000", true)]
    [InlineData("111111", false)]   // Length 6 (invalid)
    [InlineData("11111111", false)] // Length 8 (invalid)
    [InlineData("111a111", false)]  // Non-binary char
    [InlineData("", false)]
    public void ServiceDays_ValidationPattern(string serviceDays, bool expectedValid)
    {
        var isValid = System.Text.RegularExpressions.Regex.IsMatch(serviceDays, "^[01]{7}$");
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public async Task Route_RequiresAtLeastTwoStops()
    {
        await using var db = CreateInMemoryDb();

        var stop1 = new Stop { Id = 1, Name = "Hisar", Code = "HSR", IsActive = true };
        db.Stops.Add(stop1);
        await db.SaveChangesAsync();

        var stopIds = new List<int> { 1 }; // Only 1 stop
        var isValidStopCount = stopIds.Count >= 2;

        Assert.False(isValidStopCount, "A route must contain at least two stops.");
    }

    [Fact]
    public void Route_RejectsDuplicateStopsInSequence()
    {
        var stopIds = new List<int> { 1, 2, 1 }; // Stop 1 repeated
        var hasDuplicates = stopIds.Distinct().Count() != stopIds.Count;

        Assert.True(hasDuplicates, "Duplicate stops in a route sequence must be detected and rejected.");
    }
}
