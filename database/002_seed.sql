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
