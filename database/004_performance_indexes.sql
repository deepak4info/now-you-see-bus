/*
    WhereIsMyBus — Production Database Performance & Index Optimization
    Adds high-impact non-clustered and covering indexes on foreign keys and frequently queried filters,
    eliminating table scans, reducing key lookups, and optimizing join performance.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- 1. RouteStops: Index lookups by StopId (Crucial for stop search, route discovery, and ETA calculation)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RouteStops_StopId_Seq' AND object_id = OBJECT_ID(N'dbo.RouteStops'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_RouteStops_StopId_Seq
    ON dbo.RouteStops (StopId, SequenceNo)
    INCLUDE (RouteId, DistanceFromOriginKm, SegmentDistanceKm);
END;

-- 2. Schedules: Foreign key indexes and covering index for active schedule lookup
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Schedules_Route_Active' AND object_id = OBJECT_ID(N'dbo.Schedules'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Schedules_Route_Active
    ON dbo.Schedules (RouteId, IsActive)
    INCLUDE (BusId, DriverId, DepartureTime, ServiceDays);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Schedules_BusId' AND object_id = OBJECT_ID(N'dbo.Schedules'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Schedules_BusId
    ON dbo.Schedules (BusId)
    WHERE BusId IS NOT NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Schedules_DriverId' AND object_id = OBJECT_ID(N'dbo.Schedules'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Schedules_DriverId
    ON dbo.Schedules (DriverId)
    WHERE DriverId IS NOT NULL;
END;

-- 3. Trips: High frequency tracking, daily materialization and search lookups
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trips_BusId_Date' AND object_id = OBJECT_ID(N'dbo.Trips'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Trips_BusId_Date
    ON dbo.Trips (BusId, ServiceDate, Status);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trips_DriverId_Date' AND object_id = OBJECT_ID(N'dbo.Trips'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Trips_DriverId_Date
    ON dbo.Trips (DriverId, ServiceDate, Status);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trips_RouteId_Date' AND object_id = OBJECT_ID(N'dbo.Trips'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Trips_RouteId_Date
    ON dbo.Trips (RouteId, ServiceDate, Status);
END;

-- Filtered Index for active in-progress trips (dramatically speeds up passenger live map polling)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trips_Active_InProgress' AND object_id = OBJECT_ID(N'dbo.Trips'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Trips_Active_InProgress
    ON dbo.Trips (BusId, RouteId)
    INCLUDE (ServiceDate, StartedAt, OdometerKm)
    WHERE Status IN (N'Scheduled', N'InProgress');
END;

-- 4. TripStopEtas: Covering index for stop sequence and delay tracking
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TripStopEtas_StopId_Status' AND object_id = OBJECT_ID(N'dbo.TripStopEtas'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_TripStopEtas_StopId_Status
    ON dbo.TripStopEtas (StopId, Status)
    INCLUDE (TripId, SequenceNo, ScheduledArrival, EstimatedArrival, DelayMinutes);
END;

-- 5. FareSlabs: Direct covering index for fare pair lookups
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FareSlabs_From_To' AND object_id = OBJECT_ID(N'dbo.FareSlabs'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_FareSlabs_From_To
    ON dbo.FareSlabs (FromStopId, ToStopId)
    INCLUDE (RouteId, Amount, Currency);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FareSlabs_ToStopId' AND object_id = OBJECT_ID(N'dbo.FareSlabs'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_FareSlabs_ToStopId
    ON dbo.FareSlabs (ToStopId)
    INCLUDE (RouteId, FromStopId, Amount, Currency);
END;

-- 6. Buses & Drivers foreign key support
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Buses_OperatorId' AND object_id = OBJECT_ID(N'dbo.Buses'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Buses_OperatorId
    ON dbo.Buses (OperatorId)
    INCLUDE (FleetNumber, RegistrationNumber, IsActive);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Buses_AssignedDriver' AND object_id = OBJECT_ID(N'dbo.Buses'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Buses_AssignedDriver
    ON dbo.Buses (AssignedDriverId)
    WHERE AssignedDriverId IS NOT NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Drivers_OperatorId' AND object_id = OBJECT_ID(N'dbo.Drivers'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Drivers_OperatorId
    ON dbo.Drivers (OperatorId)
    WHERE OperatorId IS NOT NULL;
END;
