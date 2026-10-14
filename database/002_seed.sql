/*
    Master data for the Hisar → Fatehabad sample corridor.
    Users, drivers, schedules, fares, and route geometry are completed
    by the API on startup so passwords stay hashed by ASP.NET Core.
*/

SET NOCOUNT ON;

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
