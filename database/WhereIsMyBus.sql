/*
    Where is my bus — complete SQL Server script.
    Open this file in SSMS or Azure Data Studio and execute it.
    It creates the WhereIsMyBus database, every table, and the sample corridor:
    Haryana Roadways, buses HR39-1020 and HR39-2218, stops Hisar, Barwala,
    Tohana and Fatehabad, route 101, both schedules, fares, and the map line.
    Demo passwords are in the project README. Running it again is safe.
*/

IF DB_ID(N'WhereIsMyBus') IS NULL
    CREATE DATABASE WhereIsMyBus;
GO
USE WhereIsMyBus;
GO

/*
    Where is my bus — SQL Server schema
    Run database/WhereIsMyBus.sql to create the database and load the sample,
    or run this file first on a database named WhereIsMyBus, then 002_seed.sql.
    The API also executes these scripts on startup.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Operators', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Operators (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Operators PRIMARY KEY,
        Name NVARCHAR(150) NOT NULL,
        Code NVARCHAR(20) NOT NULL,
        ContactName NVARCHAR(120) NULL,
        Phone NVARCHAR(20) NULL,
        Email NVARCHAR(256) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_Operators_IsActive DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Operators_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Operators_Code UNIQUE (Code)
    );
END;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        FullName NVARCHAR(120) NOT NULL,
        Email NVARCHAR(256) NOT NULL,
        Phone NVARCHAR(20) NULL,
        PasswordHash NVARCHAR(500) NOT NULL,
        Role NVARCHAR(20) NOT NULL,
        DriverCode NVARCHAR(30) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Users_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Users_Email UNIQUE (Email)
    );
    CREATE UNIQUE INDEX UQ_Users_DriverCode ON dbo.Users(DriverCode) WHERE DriverCode IS NOT NULL;
END;

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Drivers (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Drivers PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NOT NULL,
        LicenseNumber NVARCHAR(40) NOT NULL,
        OperatorId INT NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_Drivers_IsActive DEFAULT 1,
        CONSTRAINT UQ_Drivers_User UNIQUE (UserId),
        CONSTRAINT FK_Drivers_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(Id),
        CONSTRAINT FK_Drivers_Operators FOREIGN KEY (OperatorId) REFERENCES dbo.Operators(Id)
    );
END;

IF OBJECT_ID(N'dbo.Buses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Buses (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Buses PRIMARY KEY,
        OperatorId INT NOT NULL,
        FleetNumber NVARCHAR(20) NOT NULL,
        RegistrationNumber NVARCHAR(20) NOT NULL,
        Model NVARCHAR(80) NULL,
        Capacity INT NOT NULL,
        BusType NVARCHAR(40) NOT NULL,
        AssignedDriverId INT NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_Buses_IsActive DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Buses_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Buses_Fleet UNIQUE (FleetNumber),
        CONSTRAINT UQ_Buses_Registration UNIQUE (RegistrationNumber),
        CONSTRAINT FK_Buses_Operators FOREIGN KEY (OperatorId) REFERENCES dbo.Operators(Id),
        CONSTRAINT FK_Buses_Drivers FOREIGN KEY (AssignedDriverId) REFERENCES dbo.Drivers(Id)
    );
END;

IF OBJECT_ID(N'dbo.Stops', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Stops (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Stops PRIMARY KEY,
        Name NVARCHAR(120) NOT NULL,
        Code NVARCHAR(20) NOT NULL,
        Latitude DECIMAL(9,6) NOT NULL,
        Longitude DECIMAL(9,6) NOT NULL,
        Address NVARCHAR(250) NULL,
        City NVARCHAR(80) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_Stops_IsActive DEFAULT 1,
        CONSTRAINT UQ_Stops_Code UNIQUE (Code)
    );
END;

IF OBJECT_ID(N'dbo.Routes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Routes (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Routes PRIMARY KEY,
        Code NVARCHAR(20) NOT NULL,
        Name NVARCHAR(150) NOT NULL,
        OperatorId INT NULL,
        DistanceMode NVARCHAR(10) NOT NULL CONSTRAINT DF_Routes_DistanceMode DEFAULT N'Auto',
        DistanceKm DECIMAL(8,2) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_Routes_IsActive DEFAULT 1,
        CONSTRAINT UQ_Routes_Code UNIQUE (Code),
        CONSTRAINT FK_Routes_Operators FOREIGN KEY (OperatorId) REFERENCES dbo.Operators(Id)
    );
END;

IF OBJECT_ID(N'dbo.RouteStops', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RouteStops (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RouteStops PRIMARY KEY,
        RouteId INT NOT NULL,
        StopId INT NOT NULL,
        SequenceNo INT NOT NULL,
        SegmentDistanceKm DECIMAL(8,2) NULL,
        DistanceFromOriginKm DECIMAL(8,2) NOT NULL CONSTRAINT DF_RouteStops_Dist DEFAULT 0,
        CONSTRAINT UQ_RouteStops_Sequence UNIQUE (RouteId, SequenceNo),
        CONSTRAINT UQ_RouteStops_Stop UNIQUE (RouteId, StopId),
        CONSTRAINT FK_RouteStops_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id),
        CONSTRAINT FK_RouteStops_Stops FOREIGN KEY (StopId) REFERENCES dbo.Stops(Id)
    );
END;

IF COL_LENGTH(N'dbo.Routes', N'DistanceMode') IS NULL
    ALTER TABLE dbo.Routes ADD DistanceMode NVARCHAR(10) NOT NULL CONSTRAINT DF_Routes_DistanceMode DEFAULT N'Auto';

IF COL_LENGTH(N'dbo.RouteStops', N'SegmentDistanceKm') IS NULL
    ALTER TABLE dbo.RouteStops ADD SegmentDistanceKm DECIMAL(8,2) NULL;

IF OBJECT_ID(N'dbo.RoutePoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RoutePoints (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RoutePoints PRIMARY KEY,
        RouteId INT NOT NULL,
        SequenceNo INT NOT NULL,
        Latitude DECIMAL(9,6) NOT NULL,
        Longitude DECIMAL(9,6) NOT NULL,
        CONSTRAINT UQ_RoutePoints_Sequence UNIQUE (RouteId, SequenceNo),
        CONSTRAINT FK_RoutePoints_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id)
    );
    CREATE INDEX IX_RoutePoints_Route ON dbo.RoutePoints(RouteId, SequenceNo);
END;

IF OBJECT_ID(N'dbo.FareSlabs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FareSlabs (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FareSlabs PRIMARY KEY,
        RouteId INT NOT NULL,
        FromStopId INT NOT NULL,
        ToStopId INT NOT NULL,
        Amount DECIMAL(10,2) NOT NULL,
        Currency NVARCHAR(3) NOT NULL CONSTRAINT DF_Fare_Currency DEFAULT N'INR',
        CONSTRAINT UQ_FareSlabs_Pair UNIQUE (RouteId, FromStopId, ToStopId),
        CONSTRAINT FK_FareSlabs_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id),
        CONSTRAINT FK_FareSlabs_From FOREIGN KEY (FromStopId) REFERENCES dbo.Stops(Id),
        CONSTRAINT FK_FareSlabs_To FOREIGN KEY (ToStopId) REFERENCES dbo.Stops(Id)
    );
END;

IF OBJECT_ID(N'dbo.Schedules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Schedules (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Schedules PRIMARY KEY,
        RouteId INT NOT NULL,
        BusId INT NULL,
        DriverId INT NULL,
        DepartureTime TIME NOT NULL,
        ServiceDays NVARCHAR(7) NOT NULL CONSTRAINT DF_Schedules_Days DEFAULT N'1111111',
        IsActive BIT NOT NULL CONSTRAINT DF_Schedules_IsActive DEFAULT 1,
        CONSTRAINT FK_Schedules_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id),
        CONSTRAINT FK_Schedules_Buses FOREIGN KEY (BusId) REFERENCES dbo.Buses(Id),
        CONSTRAINT FK_Schedules_Drivers FOREIGN KEY (DriverId) REFERENCES dbo.Drivers(Id)
    );
END;

IF OBJECT_ID(N'dbo.Trips', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Trips (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Trips PRIMARY KEY,
        ScheduleId INT NOT NULL,
        BusId INT NOT NULL,
        DriverId INT NOT NULL,
        RouteId INT NOT NULL,
        ServiceDate DATE NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        StartedAt DATETIME2 NULL,
        EndedAt DATETIME2 NULL,
        Simulation BIT NOT NULL CONSTRAINT DF_Trips_Simulation DEFAULT 0,
        OdometerKm DECIMAL(10,3) NOT NULL CONSTRAINT DF_Trips_Odometer DEFAULT 0,
        CONSTRAINT UQ_Trips_ScheduleDate UNIQUE (ScheduleId, ServiceDate),
        CONSTRAINT FK_Trips_Schedules FOREIGN KEY (ScheduleId) REFERENCES dbo.Schedules(Id),
        CONSTRAINT FK_Trips_Buses FOREIGN KEY (BusId) REFERENCES dbo.Buses(Id),
        CONSTRAINT FK_Trips_Drivers FOREIGN KEY (DriverId) REFERENCES dbo.Drivers(Id),
        CONSTRAINT FK_Trips_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id)
    );
    CREATE INDEX IX_Trips_ServiceDate ON dbo.Trips(ServiceDate, Status);
END;

IF OBJECT_ID(N'dbo.GpsPings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GpsPings (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GpsPings PRIMARY KEY,
        TripId INT NOT NULL,
        Latitude DECIMAL(9,6) NOT NULL,
        Longitude DECIMAL(9,6) NOT NULL,
        SpeedKmph DECIMAL(6,2) NULL,
        Heading DECIMAL(6,2) NULL,
        RecordedAt DATETIME2 NOT NULL,
        ReceivedAt DATETIME2 NOT NULL CONSTRAINT DF_Gps_Received DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_GpsPings_Trips FOREIGN KEY (TripId) REFERENCES dbo.Trips(Id)
    );
    CREATE INDEX IX_GpsPings_Trip_Recorded ON dbo.GpsPings(TripId, RecordedAt DESC);
END;

IF OBJECT_ID(N'dbo.TripStopEtas', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TripStopEtas (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TripStopEtas PRIMARY KEY,
        TripId INT NOT NULL,
        StopId INT NOT NULL,
        SequenceNo INT NOT NULL,
        ScheduledArrival DATETIME2 NULL,
        EstimatedArrival DATETIME2 NULL,
        ActualArrival DATETIME2 NULL,
        ScheduledDeparture DATETIME2 NULL,
        EstimatedDeparture DATETIME2 NULL,
        ActualDeparture DATETIME2 NULL,
        DelayMinutes INT NULL,
        EtaMinutes INT NULL,
        Status NVARCHAR(20) NOT NULL,
        LastUpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_TripStopEtas UNIQUE (TripId, StopId),
        CONSTRAINT FK_TripStopEtas_Trips FOREIGN KEY (TripId) REFERENCES dbo.Trips(Id),
        CONSTRAINT FK_TripStopEtas_Stops FOREIGN KEY (StopId) REFERENCES dbo.Stops(Id)
    );
END;

IF COL_LENGTH(N'dbo.TripStopEtas', N'ScheduledDeparture') IS NULL
    ALTER TABLE dbo.TripStopEtas ADD ScheduledDeparture DATETIME2 NULL;
IF COL_LENGTH(N'dbo.TripStopEtas', N'EstimatedDeparture') IS NULL
    ALTER TABLE dbo.TripStopEtas ADD EstimatedDeparture DATETIME2 NULL;
IF COL_LENGTH(N'dbo.TripStopEtas', N'ActualDeparture') IS NULL
    ALTER TABLE dbo.TripStopEtas ADD ActualDeparture DATETIME2 NULL;
IF COL_LENGTH(N'dbo.TripStopEtas', N'DelayMinutes') IS NULL
    ALTER TABLE dbo.TripStopEtas ADD DelayMinutes INT NULL;
IF COL_LENGTH(N'dbo.TripStopEtas', N'LastUpdatedAt') IS NULL
    ALTER TABLE dbo.TripStopEtas ADD LastUpdatedAt DATETIME2 NULL;

IF OBJECT_ID(N'dbo.Favorites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Favorites (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Favorites PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NOT NULL,
        BusId INT NULL,
        RouteId INT NULL,
        FromStopId INT NULL,
        ToStopId INT NULL,
        Label NVARCHAR(160) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Favorites_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Favorites_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(Id),
        CONSTRAINT FK_Favorites_Buses FOREIGN KEY (BusId) REFERENCES dbo.Buses(Id),
        CONSTRAINT FK_Favorites_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id),
        CONSTRAINT FK_Favorites_From FOREIGN KEY (FromStopId) REFERENCES dbo.Stops(Id),
        CONSTRAINT FK_Favorites_To FOREIGN KEY (ToStopId) REFERENCES dbo.Stops(Id)
    );
    CREATE INDEX IX_Favorites_User ON dbo.Favorites(UserId);
END;

IF OBJECT_ID(N'dbo.Notifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Notifications (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Notifications PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NOT NULL,
        TripId INT NULL,
        Title NVARCHAR(160) NOT NULL,
        Body NVARCHAR(500) NOT NULL,
        Kind NVARCHAR(40) NOT NULL,
        IsRead BIT NOT NULL CONSTRAINT DF_Notifications_Read DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Notifications_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Notifications_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(Id),
        CONSTRAINT FK_Notifications_Trips FOREIGN KEY (TripId) REFERENCES dbo.Trips(Id)
    );
    CREATE INDEX IX_Notifications_User ON dbo.Notifications(UserId, IsRead, CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.SystemSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SystemSettings (
        [Key] NVARCHAR(80) NOT NULL CONSTRAINT PK_SystemSettings PRIMARY KEY,
        [Value] NVARCHAR(400) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_Settings_Updated DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLogs (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY,
        UserId UNIQUEIDENTIFIER NULL,
        Action NVARCHAR(80) NOT NULL,
        Entity NVARCHAR(80) NOT NULL,
        EntityId NVARCHAR(40) NULL,
        Detail NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Audit_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_AuditLogs_Created ON dbo.AuditLogs(CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.Visitors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Visitors (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Visitors PRIMARY KEY,
        FirstSeenAt DATETIME2 NOT NULL CONSTRAINT DF_Visitors_FirstSeenAt DEFAULT SYSUTCDATETIME()
    );
END;
GO

/*
    Sample corridor for WhereIsMyBus.
    Run database/001_schema.sql first, or run database/WhereIsMyBus.sql once.
    This file loads the operator, buses, stops, route, accounts, schedules,
    fares, map geometry, and Meera's favourite. It is safe to run again.
*/

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.Operators WHERE Code = N'HR')
BEGIN
    INSERT INTO dbo.Operators (Name, Code, ContactName, Phone, Email, IsActive)
    VALUES (N'Haryana Roadways', N'HR', N'Depot Manager', N'01662-230000', N'depot@haryanaroadways.example', 1);
END;

DECLARE @op INT = (SELECT Id FROM dbo.Operators WHERE Code = N'HR');

IF NOT EXISTS (SELECT 1 FROM dbo.Buses WHERE FleetNumber = N'102')
BEGIN
    INSERT INTO dbo.Buses (OperatorId, FleetNumber, RegistrationNumber, Model, Capacity, BusType, IsActive)
    VALUES (@op, N'102', N'HR39-1020', N'Tata Starbus', 52, N'Express', 1);
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Buses WHERE FleetNumber = N'118')
BEGIN
    INSERT INTO dbo.Buses (OperatorId, FleetNumber, RegistrationNumber, Model, Capacity, BusType, IsActive)
    VALUES (@op, N'118', N'HR39-2218', N'Ashok Leyland Lynx', 42, N'Ordinary', 1);
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'HIS')
    INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City) VALUES
    (N'Hisar', N'HIS', 29.149200, 75.721700, N'Hisar Bus Stand', N'Hisar');

IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'BWL')
    INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City) VALUES
    (N'Barwala', N'BWL', 29.364500, 75.908100, N'Barwala Bus Stand', N'Barwala');

IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'TOH')
    INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City) VALUES
    (N'Tohana', N'TOH', 29.713800, 75.904400, N'Tohana Bus Stand', N'Tohana');

IF NOT EXISTS (SELECT 1 FROM dbo.Stops WHERE Code = N'FTB')
    INSERT INTO dbo.Stops (Name, Code, Latitude, Longitude, Address, City) VALUES
    (N'Fatehabad', N'FTB', 29.515600, 75.455500, N'Fatehabad Bus Stand', N'Fatehabad');

IF NOT EXISTS (SELECT 1 FROM dbo.Routes WHERE Code = N'101')
BEGIN
    INSERT INTO dbo.Routes (Code, Name, OperatorId, DistanceKm, IsActive)
    VALUES (N'101', N'Hisar - Barwala - Tohana - Fatehabad', @op, 117.51, 1);
END;

DECLARE @route INT = (SELECT Id FROM dbo.Routes WHERE Code = N'101');

IF NOT EXISTS (SELECT 1 FROM dbo.RouteStops WHERE RouteId = @route)
BEGIN
    INSERT INTO dbo.RouteStops (RouteId, StopId, SequenceNo, DistanceFromOriginKm)
    SELECT @route, s.Id, v.Seq, v.Dist
    FROM (VALUES
        (N'HIS', 1, CAST(0.00 AS DECIMAL(8,2))),
        (N'BWL', 2, CAST(30.00 AS DECIMAL(8,2))),
        (N'TOH', 3, CAST(68.84 AS DECIMAL(8,2))),
        (N'FTB', 4, CAST(117.51 AS DECIMAL(8,2)))
    ) v(Code, Seq, Dist)
    JOIN dbo.Stops s ON s.Code = v.Code;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE [Key] = N'OperationsLive')
    INSERT INTO dbo.SystemSettings ([Key], [Value]) VALUES (N'OperationsLive', N'false');

/* Accounts, assignments, schedules, fares, geometry, and Meera's favourite.
   Password hashes are ASP.NET Core Identity hashes for the passwords in the README.
   Re-running this script does not duplicate rows. */

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Email = N'admin@whereismybus.local')
    INSERT INTO dbo.Users (Id, FullName, Email, Phone, PasswordHash, Role, DriverCode, IsActive)
    VALUES ('23E756E5-14AA-449C-9D15-668199899E4F', N'Depot Admin', N'admin@whereismybus.local', N'01662-230001',
            N'AQAAAAIAAYagAAAAEHnyy6IsFp6f8BAA3onbuzDP49N52Go0M7UhJ+3isCuRkheRQUlsorsmWmCe9oP5hg==', N'Admin', NULL, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Email = N'rahul@whereismybus.local')
    INSERT INTO dbo.Users (Id, FullName, Email, Phone, PasswordHash, Role, DriverCode, IsActive)
    VALUES ('CBCA69EE-98FB-4473-A83D-58790CA2120A', N'Rahul Kumar', N'rahul@whereismybus.local', N'9811100102',
            N'AQAAAAIAAYagAAAAEBMAXbV7FO6HLwMwXH6QBa04Ks6chZnuwRwcVWW6M+a7pfL6cfJT1k0fWaGJeVlJHg==', N'Driver', N'DRV-RAHUL', 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Email = N'amit@whereismybus.local')
    INSERT INTO dbo.Users (Id, FullName, Email, Phone, PasswordHash, Role, DriverCode, IsActive)
    VALUES ('A1144826-FAA5-4DE5-928F-4545843D74AB', N'Amit Singh', N'amit@whereismybus.local', N'9811100118',
            N'AQAAAAIAAYagAAAAECWp4VhmEWvmqeExS6pue69sxYUcyBQ+cMZgCVe2E0zglF5ENN1TBZ5o0eZI2k5fEg==', N'Driver', N'DRV-AMIT', 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Email = N'meera@whereismybus.local')
    INSERT INTO dbo.Users (Id, FullName, Email, Phone, PasswordHash, Role, DriverCode, IsActive)
    VALUES ('CBEA8B3D-85BF-495C-AE8D-01F8148DF315', N'Meera Yadav', N'meera@whereismybus.local', N'9811100201',
            N'AQAAAAIAAYagAAAAEBzRYT67R7kdQAA1Z4b87qevwQ4lFOeFXvC8focOH7crhuJbPdx7+WfSo9h04UGwew==', N'Passenger', NULL, 1);

IF NOT EXISTS (
    SELECT 1 FROM dbo.Drivers d JOIN dbo.Users u ON u.Id = d.UserId WHERE u.Email = N'rahul@whereismybus.local')
    INSERT INTO dbo.Drivers (UserId, LicenseNumber, OperatorId, IsActive)
    SELECT u.Id, N'HR-042026-RAHUL', @op, 1 FROM dbo.Users u WHERE u.Email = N'rahul@whereismybus.local';

IF NOT EXISTS (
    SELECT 1 FROM dbo.Drivers d JOIN dbo.Users u ON u.Id = d.UserId WHERE u.Email = N'amit@whereismybus.local')
    INSERT INTO dbo.Drivers (UserId, LicenseNumber, OperatorId, IsActive)
    SELECT u.Id, N'HR-042026-AMIT', @op, 1 FROM dbo.Users u WHERE u.Email = N'amit@whereismybus.local';

UPDATE b
SET AssignedDriverId = d.Id
FROM dbo.Buses b
JOIN dbo.Users u ON u.DriverCode = N'DRV-RAHUL'
JOIN dbo.Drivers d ON d.UserId = u.Id
WHERE b.FleetNumber = N'102' AND b.AssignedDriverId IS NULL;

UPDATE b
SET AssignedDriverId = d.Id
FROM dbo.Buses b
JOIN dbo.Users u ON u.DriverCode = N'DRV-AMIT'
JOIN dbo.Drivers d ON d.UserId = u.Id
WHERE b.FleetNumber = N'118' AND b.AssignedDriverId IS NULL;

IF NOT EXISTS (
    SELECT 1 FROM dbo.Schedules s JOIN dbo.Buses b ON b.Id = s.BusId WHERE b.FleetNumber = N'102')
    INSERT INTO dbo.Schedules (RouteId, BusId, DriverId, DepartureTime, ServiceDays, IsActive)
    SELECT @route, b.Id, d.Id, CAST(N'08:00' AS TIME), N'1111111', 1
    FROM dbo.Buses b
    JOIN dbo.Users u ON u.DriverCode = N'DRV-RAHUL'
    JOIN dbo.Drivers d ON d.UserId = u.Id
    WHERE b.FleetNumber = N'102';

IF NOT EXISTS (
    SELECT 1 FROM dbo.Schedules s JOIN dbo.Buses b ON b.Id = s.BusId WHERE b.FleetNumber = N'118')
    INSERT INTO dbo.Schedules (RouteId, BusId, DriverId, DepartureTime, ServiceDays, IsActive)
    SELECT @route, b.Id, d.Id, CAST(N'14:30' AS TIME), N'1111111', 1
    FROM dbo.Buses b
    JOIN dbo.Users u ON u.DriverCode = N'DRV-AMIT'
    JOIN dbo.Drivers d ON d.UserId = u.Id
    WHERE b.FleetNumber = N'118';

IF NOT EXISTS (SELECT 1 FROM dbo.FareSlabs WHERE RouteId = @route)
BEGIN
    INSERT INTO dbo.FareSlabs (RouteId, FromStopId, ToStopId, Amount, Currency)
    SELECT @route, sf.Id, st.Id, v.Amount, N'INR'
    FROM (VALUES
        (N'HIS', N'BWL', CAST(55.00 AS DECIMAL(10,2))),
        (N'HIS', N'TOH', CAST(125.00 AS DECIMAL(10,2))),
        (N'HIS', N'FTB', CAST(210.00 AS DECIMAL(10,2))),
        (N'BWL', N'TOH', CAST(70.00 AS DECIMAL(10,2))),
        (N'BWL', N'FTB', CAST(160.00 AS DECIMAL(10,2))),
        (N'TOH', N'FTB', CAST(90.00 AS DECIMAL(10,2)))
    ) v(FromCode, ToCode, Amount)
    JOIN dbo.Stops sf ON sf.Code = v.FromCode
    JOIN dbo.Stops st ON st.Code = v.ToCode;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.RoutePoints WHERE RouteId = @route)
BEGIN
    INSERT INTO dbo.RoutePoints (RouteId, SequenceNo, Latitude, Longitude)
    SELECT @route, v.Seq, v.Lat, v.Lng
    FROM (VALUES
        (1, CAST(29.149200 AS DECIMAL(9,6)), CAST(75.721700 AS DECIMAL(9,6))),
        (2, CAST(29.162656 AS DECIMAL(9,6)), CAST(75.733350 AS DECIMAL(9,6))),
        (3, CAST(29.176112 AS DECIMAL(9,6)), CAST(75.745000 AS DECIMAL(9,6))),
        (4, CAST(29.189569 AS DECIMAL(9,6)), CAST(75.756650 AS DECIMAL(9,6))),
        (5, CAST(29.203025 AS DECIMAL(9,6)), CAST(75.768300 AS DECIMAL(9,6))),
        (6, CAST(29.216481 AS DECIMAL(9,6)), CAST(75.779950 AS DECIMAL(9,6))),
        (7, CAST(29.229938 AS DECIMAL(9,6)), CAST(75.791600 AS DECIMAL(9,6))),
        (8, CAST(29.243394 AS DECIMAL(9,6)), CAST(75.803250 AS DECIMAL(9,6))),
        (9, CAST(29.256850 AS DECIMAL(9,6)), CAST(75.814900 AS DECIMAL(9,6))),
        (10, CAST(29.270306 AS DECIMAL(9,6)), CAST(75.826550 AS DECIMAL(9,6))),
        (11, CAST(29.283762 AS DECIMAL(9,6)), CAST(75.838200 AS DECIMAL(9,6))),
        (12, CAST(29.297219 AS DECIMAL(9,6)), CAST(75.849850 AS DECIMAL(9,6))),
        (13, CAST(29.310675 AS DECIMAL(9,6)), CAST(75.861500 AS DECIMAL(9,6))),
        (14, CAST(29.324131 AS DECIMAL(9,6)), CAST(75.873150 AS DECIMAL(9,6))),
        (15, CAST(29.337588 AS DECIMAL(9,6)), CAST(75.884800 AS DECIMAL(9,6))),
        (16, CAST(29.351044 AS DECIMAL(9,6)), CAST(75.896450 AS DECIMAL(9,6))),
        (17, CAST(29.364500 AS DECIMAL(9,6)), CAST(75.908100 AS DECIMAL(9,6))),
        (18, CAST(29.381965 AS DECIMAL(9,6)), CAST(75.907915 AS DECIMAL(9,6))),
        (19, CAST(29.399430 AS DECIMAL(9,6)), CAST(75.907730 AS DECIMAL(9,6))),
        (20, CAST(29.416895 AS DECIMAL(9,6)), CAST(75.907545 AS DECIMAL(9,6))),
        (21, CAST(29.434360 AS DECIMAL(9,6)), CAST(75.907360 AS DECIMAL(9,6))),
        (22, CAST(29.451825 AS DECIMAL(9,6)), CAST(75.907175 AS DECIMAL(9,6))),
        (23, CAST(29.469290 AS DECIMAL(9,6)), CAST(75.906990 AS DECIMAL(9,6))),
        (24, CAST(29.486755 AS DECIMAL(9,6)), CAST(75.906805 AS DECIMAL(9,6))),
        (25, CAST(29.504220 AS DECIMAL(9,6)), CAST(75.906620 AS DECIMAL(9,6))),
        (26, CAST(29.521685 AS DECIMAL(9,6)), CAST(75.906435 AS DECIMAL(9,6))),
        (27, CAST(29.539150 AS DECIMAL(9,6)), CAST(75.906250 AS DECIMAL(9,6))),
        (28, CAST(29.556615 AS DECIMAL(9,6)), CAST(75.906065 AS DECIMAL(9,6))),
        (29, CAST(29.574080 AS DECIMAL(9,6)), CAST(75.905880 AS DECIMAL(9,6))),
        (30, CAST(29.591545 AS DECIMAL(9,6)), CAST(75.905695 AS DECIMAL(9,6))),
        (31, CAST(29.609010 AS DECIMAL(9,6)), CAST(75.905510 AS DECIMAL(9,6))),
        (32, CAST(29.626475 AS DECIMAL(9,6)), CAST(75.905325 AS DECIMAL(9,6))),
        (33, CAST(29.643940 AS DECIMAL(9,6)), CAST(75.905140 AS DECIMAL(9,6))),
        (34, CAST(29.661405 AS DECIMAL(9,6)), CAST(75.904955 AS DECIMAL(9,6))),
        (35, CAST(29.678870 AS DECIMAL(9,6)), CAST(75.904770 AS DECIMAL(9,6))),
        (36, CAST(29.696335 AS DECIMAL(9,6)), CAST(75.904585 AS DECIMAL(9,6))),
        (37, CAST(29.713800 AS DECIMAL(9,6)), CAST(75.904400 AS DECIMAL(9,6))),
        (38, CAST(29.705872 AS DECIMAL(9,6)), CAST(75.886444 AS DECIMAL(9,6))),
        (39, CAST(29.697944 AS DECIMAL(9,6)), CAST(75.868488 AS DECIMAL(9,6))),
        (40, CAST(29.690016 AS DECIMAL(9,6)), CAST(75.850532 AS DECIMAL(9,6))),
        (41, CAST(29.682088 AS DECIMAL(9,6)), CAST(75.832576 AS DECIMAL(9,6))),
        (42, CAST(29.674160 AS DECIMAL(9,6)), CAST(75.814620 AS DECIMAL(9,6))),
        (43, CAST(29.666232 AS DECIMAL(9,6)), CAST(75.796664 AS DECIMAL(9,6))),
        (44, CAST(29.658304 AS DECIMAL(9,6)), CAST(75.778708 AS DECIMAL(9,6))),
        (45, CAST(29.650376 AS DECIMAL(9,6)), CAST(75.760752 AS DECIMAL(9,6))),
        (46, CAST(29.642448 AS DECIMAL(9,6)), CAST(75.742796 AS DECIMAL(9,6))),
        (47, CAST(29.634520 AS DECIMAL(9,6)), CAST(75.724840 AS DECIMAL(9,6))),
        (48, CAST(29.626592 AS DECIMAL(9,6)), CAST(75.706884 AS DECIMAL(9,6))),
        (49, CAST(29.618664 AS DECIMAL(9,6)), CAST(75.688928 AS DECIMAL(9,6))),
        (50, CAST(29.610736 AS DECIMAL(9,6)), CAST(75.670972 AS DECIMAL(9,6))),
        (51, CAST(29.602808 AS DECIMAL(9,6)), CAST(75.653016 AS DECIMAL(9,6))),
        (52, CAST(29.594880 AS DECIMAL(9,6)), CAST(75.635060 AS DECIMAL(9,6))),
        (53, CAST(29.586952 AS DECIMAL(9,6)), CAST(75.617104 AS DECIMAL(9,6))),
        (54, CAST(29.579024 AS DECIMAL(9,6)), CAST(75.599148 AS DECIMAL(9,6))),
        (55, CAST(29.571096 AS DECIMAL(9,6)), CAST(75.581192 AS DECIMAL(9,6))),
        (56, CAST(29.563168 AS DECIMAL(9,6)), CAST(75.563236 AS DECIMAL(9,6))),
        (57, CAST(29.555240 AS DECIMAL(9,6)), CAST(75.545280 AS DECIMAL(9,6))),
        (58, CAST(29.547312 AS DECIMAL(9,6)), CAST(75.527324 AS DECIMAL(9,6))),
        (59, CAST(29.539384 AS DECIMAL(9,6)), CAST(75.509368 AS DECIMAL(9,6))),
        (60, CAST(29.531456 AS DECIMAL(9,6)), CAST(75.491412 AS DECIMAL(9,6))),
        (61, CAST(29.523528 AS DECIMAL(9,6)), CAST(75.473456 AS DECIMAL(9,6))),
        (62, CAST(29.515600 AS DECIMAL(9,6)), CAST(75.455500 AS DECIMAL(9,6)))
    ) v(Seq, Lat, Lng);
END;

IF NOT EXISTS (
    SELECT 1 FROM dbo.Favorites f JOIN dbo.Users u ON u.Id = f.UserId WHERE u.Email = N'meera@whereismybus.local')
    INSERT INTO dbo.Favorites (UserId, BusId, RouteId, FromStopId, ToStopId, Label)
    SELECT u.Id, b.Id, @route, sf.Id, st.Id, N'Hisar → Fatehabad · Bus 102'
    FROM dbo.Users u
    JOIN dbo.Buses b ON b.FleetNumber = N'102'
    JOIN dbo.Stops sf ON sf.Code = N'HIS'
    JOIN dbo.Stops st ON st.Code = N'FTB'
    WHERE u.Email = N'meera@whereismybus.local';
