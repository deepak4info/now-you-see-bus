/*
    Optional, idempotent test fixture for the admin, search, and tracking flows.
    Run after 001_schema.sql and 002_seed.sql against a development database.
    This file is intentionally not executed by the API on startup.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @operatorId INT = (SELECT Id FROM dbo.Operators WHERE Code = N'HR');
    DECLARE @driverId INT = (
        SELECT TOP (1) d.Id
        FROM dbo.Drivers d
        INNER JOIN dbo.Users u ON u.Id = d.UserId
        WHERE u.DriverCode = N'DRV-RAHUL'
    );

    IF @operatorId IS NULL OR @driverId IS NULL
        THROW 50001, 'Run the standard sample seed before adding test data.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'TEST-ORI')
        INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City)
        VALUES (N'Sample Test Origin', N'TEST-ORI', 29.149200, 75.721700, N'Test-only stop', N'Test Data');

    IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'TEST-MID')
        INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City)
        VALUES (N'Sample Test Junction', N'TEST-MID', 29.207000, 75.780000, N'Test-only stop', N'Test Data');

    IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'TEST-DST')
        INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City)
        VALUES (N'Sample Test Destination', N'TEST-DST', 29.294000, 75.860000, N'Test-only stop', N'Test Data');

    IF NOT EXISTS (SELECT 1 FROM dbo.Routes WHERE Code = N'TEST-DATA-01')
        INSERT INTO dbo.Routes (Code, Name, OperatorId, DistanceMode, DistanceKm, IsActive)
        VALUES (N'TEST-DATA-01', N'Sample test corridor', @operatorId, N'Manual', 20.75, 1);

    DECLARE @routeId INT = (SELECT Id FROM dbo.Routes WHERE Code = N'TEST-DATA-01');
    DECLARE @originId INT = (SELECT Id FROM dbo.Stops WHERE Code = N'TEST-ORI');
    DECLARE @junctionId INT = (SELECT Id FROM dbo.Stops WHERE Code = N'TEST-MID');
    DECLARE @destinationId INT = (SELECT Id FROM dbo.Stops WHERE Code = N'TEST-DST');

    IF NOT EXISTS (SELECT 1 FROM dbo.RouteStops WHERE RouteId = @routeId)
    BEGIN
        INSERT INTO dbo.RouteStops (RouteId, StopId, SequenceNo, SegmentDistanceKm, DistanceFromOriginKm)
        VALUES
            (@routeId, @originId, 1, NULL, 0.00),
            (@routeId, @junctionId, 2, 8.50, 8.50),
            (@routeId, @destinationId, 3, 12.25, 20.75);
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.FareSlabs WHERE RouteId = @routeId AND FromStopId = @originId AND ToStopId = @junctionId)
        INSERT INTO dbo.FareSlabs (RouteId, FromStopId, ToStopId, Amount, Currency)
        VALUES (@routeId, @originId, @junctionId, 20.00, N'INR');

    IF NOT EXISTS (SELECT 1 FROM dbo.FareSlabs WHERE RouteId = @routeId AND FromStopId = @junctionId AND ToStopId = @destinationId)
        INSERT INTO dbo.FareSlabs (RouteId, FromStopId, ToStopId, Amount, Currency)
        VALUES (@routeId, @junctionId, @destinationId, 30.00, N'INR');

    IF NOT EXISTS (SELECT 1 FROM dbo.FareSlabs WHERE RouteId = @routeId AND FromStopId = @originId AND ToStopId = @destinationId)
        INSERT INTO dbo.FareSlabs (RouteId, FromStopId, ToStopId, Amount, Currency)
        VALUES (@routeId, @originId, @destinationId, 50.00, N'INR');

    IF NOT EXISTS (SELECT 1 FROM dbo.RoutePoints WHERE RouteId = @routeId)
    BEGIN
        INSERT INTO dbo.RoutePoints (RouteId, SequenceNo, Latitude, Longitude)
        VALUES
            (@routeId, 1, 29.149200, 75.721700),
            (@routeId, 2, 29.207000, 75.780000),
            (@routeId, 3, 29.294000, 75.860000);
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.Buses WHERE FleetNumber = N'TEST-001')
        INSERT INTO dbo.Buses (OperatorId, FleetNumber, RegistrationNumber, Model, Capacity, BusType, AssignedDriverId, IsActive)
        VALUES (@operatorId, N'TEST-001', N'HRTEST0001', N'Test Fixture Bus', 20, N'Test', @driverId, 1);

    DECLARE @busId INT = (SELECT Id FROM dbo.Buses WHERE FleetNumber = N'TEST-001');

    IF NOT EXISTS (
        SELECT 1 FROM dbo.Schedules
        WHERE RouteId = @routeId AND BusId = @busId AND DepartureTime = CAST(N'23:59' AS TIME)
    )
        INSERT INTO dbo.Schedules (RouteId, BusId, DriverId, DepartureTime, ServiceDays, IsActive)
        VALUES (@routeId, @busId, @driverId, CAST(N'23:59' AS TIME), N'1111111', 1);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
