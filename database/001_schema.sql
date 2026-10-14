/*
    Where is my bus — SQL Server schema
    Run this first on a database named WhereIsMyBus.
    The API also executes this script on startup.
*/

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
        DistanceFromOriginKm DECIMAL(8,2) NOT NULL CONSTRAINT DF_RouteStops_Dist DEFAULT 0,
        CONSTRAINT UQ_RouteStops_Sequence UNIQUE (RouteId, SequenceNo),
        CONSTRAINT UQ_RouteStops_Stop UNIQUE (RouteId, StopId),
        CONSTRAINT FK_RouteStops_Routes FOREIGN KEY (RouteId) REFERENCES dbo.Routes(Id),
        CONSTRAINT FK_RouteStops_Stops FOREIGN KEY (StopId) REFERENCES dbo.Stops(Id)
    );
END;

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
        EtaMinutes INT NULL,
        Status NVARCHAR(20) NOT NULL,
        CONSTRAINT UQ_TripStopEtas UNIQUE (TripId, StopId),
        CONSTRAINT FK_TripStopEtas_Trips FOREIGN KEY (TripId) REFERENCES dbo.Trips(Id),
        CONSTRAINT FK_TripStopEtas_Stops FOREIGN KEY (StopId) REFERENCES dbo.Stops(Id)
    );
END;

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
