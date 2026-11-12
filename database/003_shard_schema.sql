/*
    Where is my bus — Telemetry & Historical Archive Shard Schema (SQL Server)
    Applied automatically to configured shard databases or run manually when provisioning a shard.
    Each shard stores high-volume GPS pings, archived historical trips, and activity audit logs
    without requiring cross-database foreign keys back to the primary catalog database.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ShardGpsPings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShardGpsPings (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShardGpsPings PRIMARY KEY,
        TripId INT NOT NULL,
        RouteId INT NOT NULL,
        BusId INT NOT NULL,
        Region NVARCHAR(40) NOT NULL,
        ShardKey NVARCHAR(80) NOT NULL,
        Latitude DECIMAL(9,6) NOT NULL,
        Longitude DECIMAL(9,6) NOT NULL,
        SpeedKmph DECIMAL(6,2) NULL,
        Heading DECIMAL(6,2) NULL,
        RecordedAt DATETIME2 NOT NULL,
        ReceivedAt DATETIME2 NOT NULL CONSTRAINT DF_ShardGpsPings_Received DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_ShardGpsPings_Trip_Recorded ON dbo.ShardGpsPings(TripId, RecordedAt DESC, Id DESC);
    CREATE INDEX IX_ShardGpsPings_Region_Recorded ON dbo.ShardGpsPings(Region, RecordedAt DESC);
END;

IF OBJECT_ID(N'dbo.ArchivedTrips', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ArchivedTrips (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ArchivedTrips PRIMARY KEY,
        OriginalTripId INT NOT NULL,
        ScheduleId INT NOT NULL,
        BusId INT NOT NULL,
        DriverId INT NOT NULL,
        RouteId INT NOT NULL,
        OperatorId INT NOT NULL,
        FleetNumber NVARCHAR(20) NOT NULL,
        RegistrationNumber NVARCHAR(20) NOT NULL,
        BusName NVARCHAR(120) NOT NULL,
        BusType NVARCHAR(40) NOT NULL,
        OperatorCode NVARCHAR(20) NOT NULL,
        OperatorName NVARCHAR(150) NOT NULL,
        RouteCode NVARCHAR(20) NOT NULL,
        RouteName NVARCHAR(150) NOT NULL,
        OriginStop NVARCHAR(120) NOT NULL,
        DestinationStop NVARCHAR(120) NOT NULL,
        StopSearchText NVARCHAR(2000) NOT NULL,
        OriginLatitude DECIMAL(9,6) NULL,
        OriginLongitude DECIMAL(9,6) NULL,
        DestinationLatitude DECIMAL(9,6) NULL,
        DestinationLongitude DECIMAL(9,6) NULL,
        ServiceDate DATE NOT NULL,
        DepartureTime TIME NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        StartedAt DATETIME2 NULL,
        EndedAt DATETIME2 NULL,
        Simulation BIT NOT NULL CONSTRAINT DF_ArchivedTrips_Simulation DEFAULT 0,
        OdometerKm DECIMAL(10,3) NOT NULL CONSTRAINT DF_ArchivedTrips_Odometer DEFAULT 0,
        EstimatedArrivalTime NVARCHAR(32) NULL,
        ActualDepartureTime NVARCHAR(32) NULL,
        ActualArrivalTime NVARCHAR(32) NULL,
        StopEtasJson NVARCHAR(MAX) NULL,
        Region NVARCHAR(40) NOT NULL,
        ShardId NVARCHAR(60) NOT NULL,
        ArchivedAt DATETIME2 NOT NULL CONSTRAINT DF_ArchivedTrips_ArchivedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_ArchivedTrips_OriginalTrip UNIQUE (OriginalTripId)
    );
    CREATE INDEX IX_ArchivedTrips_ServiceDate_Status ON dbo.ArchivedTrips(ServiceDate DESC, Status);
    CREATE INDEX IX_ArchivedTrips_Bus_Date ON dbo.ArchivedTrips(BusId, ServiceDate DESC);
    CREATE INDEX IX_ArchivedTrips_Route_Date ON dbo.ArchivedTrips(RouteId, ServiceDate DESC);
END;

IF OBJECT_ID(N'dbo.ShardAuditLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShardAuditLogs (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShardAuditLogs PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NULL,
        Action NVARCHAR(80) NOT NULL,
        Entity NVARCHAR(80) NOT NULL,
        EntityId NVARCHAR(40) NULL,
        Detail NVARCHAR(500) NULL,
        Region NVARCHAR(40) NOT NULL,
        ShardKey NVARCHAR(80) NOT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ShardAuditLogs_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_ShardAuditLogs_Created ON dbo.ShardAuditLogs(CreatedAt DESC);
    CREATE INDEX IX_ShardAuditLogs_Action_Entity_Created ON dbo.ShardAuditLogs(Action, Entity, CreatedAt DESC);
END;
