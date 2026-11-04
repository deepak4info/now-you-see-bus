# System Architecture & Workflows

This document outlines the end-to-end architecture, runtime communication channels, data lifecycles, and security controls for the **Where Is My Bus (BusTrack Platform)**.

---

## Table of Contents

1. [High-Level Architecture & Communication Flow](#1-high-level-architecture--communication-flow)
2. [Passenger Search & Live Tracking Flow](#2-passenger-search--live-tracking-flow)
3. [Driver Trip Lifecycle & GPS Telemetry Flow](#3-driver-trip-lifecycle--gps-telemetry-flow)
4. [Dispatch Operations & Schedule Materialization Flow](#4-dispatch-operations--schedule-materialization-flow)
5. [Bulk Schedule CSV Upload & Validation Flow](#5-bulk-schedule-csv-upload--validation-flow)
6. [Authentication & Security Middleware Flow](#6-authentication--security-middleware-flow)
7. [EU Horizon TELEMETRY IoT Defense Framework](#7-eu-horizon-telemetry-iot-defense-framework)

---

## 1. High-Level Architecture & Communication Flow

The platform is split into a **React 18 SPA**, an **ASP.NET Core 8 Web API** with real-time **SignalR WebSockets**, **Microsoft SQL Server**, and the **Project OSRM** road geometry engine.

```mermaid
flowchart TD
    subgraph Clients["Frontend Clients"]
        P["Passenger Web (React + Leaflet)"]
        D["Driver Console (Mobile Web)"]
        A["Dispatch Admin Dashboard"]
    end

    subgraph Gateway["API Gateway & Security"]
        RL["Rate Limiter (10 req/min)"]
        SEC["Security Headers & CORS"]
        JWT["JWT Auth & Invalidation Hook"]
    end

    subgraph Backend["ASP.NET Core 8 Web API"]
        CTRL["REST Controllers (Search, Admin, Driver)"]
        HUB["SignalR Hub (/hubs/tracking)"]
        GEO["Geometry & Stop Normalizer Service"]
    end

    subgraph Storage["Data & External Services"]
        SQL[("SQL Server (db_ad0bfd_busdb)")]
        OSRM["Project OSRM Routing Engine"]
    end

    P -->|HTTPS REST| SEC
    D -->|GPS Telemetry POST| SEC
    A -->|Admin Operations| SEC

    SEC --> RL --> JWT --> CTRL
    P <-->|WebSockets (Live GPS)| HUB
    CTRL <--> HUB
    CTRL --> GEO
    GEO <--> OSRM
    CTRL <--> SQL
```

### Architecture Components
- **Passenger Web (`client/src/pages`)**: Public responsive web app. Allows search, route exploration, and real-time map tracking without requiring mandatory login.
- **Driver Console (`client/src/pages/Driver.tsx`)**: Dedicated mobile-optimized interface for assigned drivers to stream GPS telemetry or test corridors via simulator.
- **Dispatch Control Room (`client/src/pages/admin`)**: Operations management portal for schedules, buses, drivers, stops, fare slabs, and bulk imports.
- **SignalR Hub (`src/BusTrack.Api/Hubs/TrackingHub.cs`)**: Distributes real-time GPS coordinates to connected passengers subscribed to specific bus channels (`bus-{fleet}`) or the live board.
- **Routing Engine (`src/BusTrack.Api/Services/RoadRouter.cs`)**: Queries Project OSRM to generate polyline road coordinates following actual highways.

---

## 2. Passenger Search & Live Tracking Flow

Describes the passenger lifecycle from initial search between any two stops to live map updates over SignalR.

```mermaid
flowchart TD
    Start(["Passenger Visits Website"]) --> SearchInput["Enters Origin & Destination Stops"]
    SearchInput --> QueryAPI["GET /api/search?from=...&to=..."]
    QueryAPI --> MatchStops{"Valid Stops Found?"}
    
    MatchStops -- "No" --> Suggestion["Show Stop Transliteration Suggestions"]
    MatchStops -- "Yes" --> FindRoutes["Query Active Schedules & Matched Routes"]
    
    FindRoutes --> CheckRunning{"Are Buses Currently Running?"}
    CheckRunning -- "Yes" --> ShowLiveCards["Display Live Buses with Dynamic ETAs & Fare"]
    CheckRunning -- "No" --> ShowTimetable["Display Upcoming Scheduled Departures"]
    
    ShowLiveCards --> ClickTrack["Passenger clicks 'Track Bus' (/track/:fleet)"]
    ClickTrack --> LeafletInit["Load Leaflet Map & Road Polyline"]
    LeafletInit --> ConnectSignalR["Join SignalR Group: 'bus-{fleet}'"]
    ConnectSignalR --> StreamGPS["Receive Real-time Marker Position & Stop ETAs"]
    StreamGPS --> StopReached{"Trip Finished?"}
    StopReached -- "No" --> StreamGPS
    StopReached -- "Yes" --> DisplayCompleted["Show 'Trip Completed' Notice"]
```

---

## 3. Driver Trip Lifecycle & GPS Telemetry Flow

Outlines how drivers authenticate, begin active scheduled runs, submit GPS coordinates, and mark station arrival/departure timestamps.

```mermaid
flowchart TD
    Start(["Driver Opens Mobile App"]) --> Login["Driver Signs in (e.g. DRV-RAHUL)"]
    Login --> LoadRoster["Load Assigned Bus & Scheduled Trips for Today"]
    LoadRoster --> CheckOps{"Have Operations Started?"}
    
    CheckOps -- "No" --> WaitDispatch["Wait for Dispatch to Start Operations"]
    CheckOps -- "Yes" --> SelectTrip["Select Trip & Click 'Start Trip'"]
    
    SelectTrip --> ChooseMode{"Execution Mode"}
    ChooseMode -- "Handset GPS" --> WatchGPS["Browser Geolocation Watcher (lat, lon, speed, heading)"]
    ChooseMode -- "Simulate Run" --> Simulator["Background Simulator Moves Bus at 42 km/h"]
    
    WatchGPS --> SendLocation["POST /api/driver/trips/{id}/location"]
    Simulator --> SendLocation
    
    SendLocation --> ProjectRoad["Project Point to Nearest Highway Geometry Segment"]
    ProjectRoad --> UpdateETA["Recalculate Delay & Station Stop ETAs"]
    UpdateETA --> BroadcastHub["Broadcast to SignalR /hubs/tracking"]
    
    BroadcastHub --> CheckStation{"At Intermediate Stop?"}
    CheckStation -- "Yes" --> ConfirmArrival["Driver confirms Arrival / Departure Checkpoint"]
    ConfirmArrival --> BroadcastHub
    CheckStation -- "No" --> CheckEnd{"Reached Final Station?"}
    
    CheckEnd -- "No" --> WatchGPS
    CheckEnd -- "Yes" --> ClickEnd["Driver presses 'End Trip'"]
    ClickEnd --> CompleteTrip["Set Status = 'Completed' & Archive GPS Log"]
```

---

## 4. Dispatch Operations & Schedule Materialization Flow

Explains how recurring weekly schedules (`ServiceDays: 1111111`) are transformed into concrete daily trip instances when dispatch starts morning operations.

```mermaid
flowchart LR
    Admin(["Dispatch Admin"]) --> Dashboard["Open Admin Dashboard"]
    Dashboard --> ClickStart["Click 'Start Operations'"]
    
    ClickStart --> SetFlag["Set SystemSetting: OperationsLive = true"]
    SetFlag --> GetDay["Identify Today's Day of Week (0 = Mon .. 6 = Sun)"]
    GetDay --> FilterSchedules["Filter Active Schedules where ServiceDays[day] == '1'"]
    
    FilterSchedules --> Loop{"For Each Schedule"}
    Loop --> CheckExists{"Trip Already Exists for Today?"}
    
    CheckExists -- "Yes" --> Skip["Skip Duplicate"]
    CheckExists -- "No" --> CreateTrip["Materialize Trip Record (Status: 'Scheduled')"]
    
    CreateTrip --> Loop
    Skip --> Loop
    Loop -- "Done" --> WriteAudit["Write Audit Log: 'Created N Trips'"]
    WriteAudit --> NotifyDrivers["Rosters Unlocked for Drivers"]
```

---

## 5. Bulk Schedule CSV Upload & Validation Flow

Details the safety, sanitization, dry-run validation, and execution pipeline for bulk uploading transit master data from CSV spreadsheets.

```mermaid
flowchart TD
    Upload(["Admin Uploads CSV"]) --> CheckFile{"File Extension .csv & Size < 15MB?"}
    CheckFile -- "No" --> RejectFile["Reject with 400 Bad Request"]
    
    CheckFile -- "Yes" --> Sanitize["Sanitize Cells: Escape Formula Triggers (=, +, -, @)"]
    Sanitize --> ParseRows["Parse Rows into Memory Session"]
    
    ParseRows --> Validate{"Validate Business Rules"}
    Validate --> V1["Check Duplicate Stop Codes & Names"]
    Validate --> V2["Verify Bus Registrations & Unique Driver Licences"]
    Validate --> V3["Validate Departure Times & Service Days (7 binary digits)"]
    Validate --> V4["Check Schedule Overlaps for same Bus or Driver"]
    
    Validate --> Preview["Generate Validation Report (Valid, Warnings, Errors)"]
    Preview --> AdminReview{"Admin Confirms Commit?"}
    
    AdminReview -- "Cancel" --> Discard["Discard Session"]
    AdminReview -- "Confirm" --> DBTransaction["Begin SQL Server Transaction"]
    
    DBTransaction --> UpsertData["Insert/Update Records into db_ad0bfd_busdb"]
    UpsertData --> RebuildGeo["Trigger OSRM Road Geometry Reconstruction"]
    RebuildGeo --> Commit["Commit Transaction & Audit Log"]
```

---

## 6. Authentication & Security Middleware Flow

Illustrates defensive security layers enforcing rate limits, HTTP security headers, CORS restrictions, token verification, and real-time session invalidation.

```mermaid
flowchart TD
    Req(["Incoming HTTP Request"]) --> SecHeaders["Inject Security Headers (CSP, HSTS, X-Frame-Options: DENY, nosniff)"]
    SecHeaders --> RateLimit{"Path is /api/auth/* ?"}
    
    RateLimit -- "Yes" --> CheckCap{"Rate Limit > 10 req/min?"}
    CheckCap -- "Yes" --> Err429["Return 429 Too Many Requests"]
    CheckCap -- "No" --> PassRL["Allow Request"]
    RateLimit -- "No" --> PassRL
    
    PassRL --> AuthCheck{"Endpoint Requires [Authorize]?"}
    AuthCheck -- "No" --> ExecuteController["Execute Public Endpoint (Search / Track)"]
    
    AuthCheck -- "Yes" --> ValidateToken{"Valid JWT Signature & Not Expired?"}
    ValidateToken -- "No" --> Err401["Return 401 Unauthorized"]
    
    ValidateToken -- "Yes" --> CheckActiveDB{"OnTokenValidated: User.IsActive == true in DB?"}
    CheckActiveDB -- "No (Deactivated)" --> ErrRevoked["Reject Token (Immediate Session Revocation)"]
    CheckActiveDB -- "Yes" --> CheckRole{"Matches Required Role (Admin / Driver)?"}
    
    CheckRole -- "No" --> Err403["Return 403 Forbidden"]
    CheckRole -- "Yes" --> ExecuteController
```

---

## 7. EU Horizon TELEMETRY IoT Defense Framework

The platform integrates the cybersecurity methodologies of the **EU Horizon TELEMETRY Project** ([telemetry-project.eu](https://telemetry-project.eu/)) for automated security testing and anomaly detection of IoT telematics devices (On-Board Units / GPS trackers).

### Multi-Layer Anomaly Detection:
1. **Kinematic & Velocity Verification**: Computes implied velocity between consecutive pings using the Haversine formula over elapsed time. Flags `SPOOFING_IMPOSSIBLE_SPEED` if velocity exceeds 130 km/h or `SPOOFING_TELEPORTATION_JUMP` if spatial displacement exceeds 3 km in under 10 seconds.
2. **Clock Skew & Replay Defense**: Rejects packets with timestamps > 3 minutes in the future (`FUTURE_TIMESTAMP_DRIFT`) or older than 24 hours (`EXPIRED_OR_REPLAY_TIMESTAMP`) to thwart GNSS spoofing and replay attacks.
3. **GNSS Signal Quality Checks**: Assesses receiver metrics; flags `LOW_SATELLITE_COUNT` (< 4 satellites) or `DEGRADED_HDOP_ACCURACY` (HDOP > 8.0).
4. **Dynamic Trust Score Model**: Maintains a rolling trust score `[0.0, 1.0]` per device. Clean packets restore trust (+0.05). Anomalies apply graded penalties (-0.10 to -0.40). Devices falling below the quarantine threshold (< 0.35) are isolated from passenger live maps.
5. **Real-time Incident Logging**: Automatically writes detected anomalies with payload coordinates and trust metrics into SQL Server `AuditLogs`.

```mermaid
flowchart TD
    OBU["IoT OBU / GPS Tracker (/api/telemetry/ingest)"] --> CHK["TELEMETRY Anomaly Engine"]
    CHK --> SPEED{"Implied Speed > 130 km/h or Jump > 3 km?"}
    CHK --> SKEW{"Clock Skew > 3 min or Replay?"}
    CHK --> GNSS{"Satellites < 4 or HDOP > 8?"}
    
    SPEED -- "Yes" --> PEN["Apply Trust Penalty (-0.40)"]
    SKEW -- "Yes" --> PEN
    GNSS -- "Yes" --> PEN
    SPEED -- "No" --> HEAL["Gradual Trust Boost (+0.05)"]
    
    PEN --> QUAR{"Trust Score < 0.35?"}
    QUAR -- "Quarantined" --> DROP["Isolate Device & AuditLog Alert"]
    QUAR -- "Trusted" --> TRACK["Sync Live Trip (TrackingService)"]
    HEAL --> TRACK
    TRACK --> HUB["SignalR WebSocket (Live Passenger Map)"]
```

