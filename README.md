# Where Is My Bus (BusTrack Platform)

An enterprise-grade, real-time bus tracking and transport operations platform designed for regional, inter-city, and campus transit networks. Passengers can search routes, view dynamic ETAs, and track buses live on high-precision maps; drivers manage daily runs with live GPS telemetry; and dispatch administrators supervise fleet operations, weekly schedules, fare tables, and bulk imports.

---

## Table of Contents

1. [Architecture & System Overview](#architecture--system-overview)
2. [Working Functionality](#working-functionality)
   - [Passenger Experience](#1-passenger-experience)
   - [Driver Operations Portal](#2-driver-operations-portal)
   - [Dispatch & Fleet Management](#3-dispatch--fleet-management)
   - [Core Real-Time & Routing Engine](#4-core-real-time--routing-engine)
   - [EU Horizon TELEMETRY IoT Defense Framework](#5-eu-horizon-telemetry-iot-defense-framework)
3. [Where It Can Be Used (Target Use Cases)](#where-it-can-be-used-target-use-cases)
4. [System Requirements & Prerequisites](#system-requirements--prerequisites)
   - [Hardware & Network](#hardware--network-specifications)
   - [Operating System & Runtimes](#operating-system--runtimes)
   - [Dependencies & Services](#dependencies--services)
5. [Security Audit & Hardening Measures](#security-audit--hardening-measures)
6. [Automated Testing](#automated-testing)
7. [Ready to Upgrade in Production (Deployment Guide)](#ready-to-upgrade-in-production-deployment-guide)
   - [Environment Configuration](#1-environment-configuration)
   - [Database Deployment](#2-database-deployment)
   - [Backend API Publish & Systemd Service](#3-backend-api-publish--systemd-service)
   - [Frontend Production Build](#4-frontend-production-build)
   - [Reverse Proxy & WebSocket (Nginx Example)](#5-reverse-proxy--websocket-configuration-nginx)
   - [Health Check & Monitoring](#6-health-check--monitoring)
   - [Production Readiness Verification Checklist](#7-production-readiness-verification-checklist)
8. [Local Development Setup](#local-development-setup)
9. [Default Accounts & Corridors](#default-accounts--corridors)

---

## Architecture & System Overview

- **Backend API**: ASP.NET Core 8.0 Web API with Entity Framework Core 8, SignalR for WebSockets real-time communication, and JWT Bearer security.
- **Frontend SPA**: React 18 with TypeScript, Vite 6, Tailwind CSS, Lucide icons, and Leaflet for interactive map rendering.
- **Database**: Microsoft SQL Server (2019/2022 / Azure SQL) with spatial coordinates, normalized schema, transactional triggers, and audit logging.
- **Geographic Services**: Integration with Project OSRM (Open Source Routing Machine) to rebuild highway-aligned route geometries, calculate Haversine distances, project real-time GPS locations onto road segments, and estimate arrival times at road speed (42 km/h).

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

> [!TIP]
> **Complete Operational & Security Flowcharts:** For detailed sequence and state flowcharts covering Passenger Search, Driver GPS Telemetry, Daily Schedule Materialization, CSV Bulk Import, and Authentication Middleware, refer to [ARCHITECTURE.md](file:///mnt/c/Users/deepa/OneDrive/Desktop/whereismybus/bus-track/bus-track/ARCHITECTURE.md).

---

## Working Functionality

### 1. Passenger Experience
- **Origin-Destination Search**: Search trips between any two stops across multi-stop corridors (e.g., Hisar → Barwala → Tohana → Fatehabad, or Delhi → Rohtak → Hansi → Hisar).
- **Live GPS Tracking (`/track/:fleet`)**:
  - Interactive Leaflet map with live bus markers, route polylines, and station markers.
  - SignalR real-time updates without full-page reloads.
  - Dynamically calculates remaining distance and ETA to boarding stop and final destination.
- **Stop Timeline & Status**:
  - Displays stop-by-stop progress with scheduled, estimated, and actual arrival/departure times.
  - Real-time delay badges (`On time`, `+15 min delay`, `Outside slot`, `Trip completed`).
  - Automatic fare calculation based on distance slabs or configured stop pairs.
- **Favourites & Notifications**: Registered passengers can bookmark frequent buses/routes and view alerts when their bus departs the depot or arrives at stations.
- **Anonymous Visitor Tracking**: Securely tracks unique anonymous visitors for transport department usage metrics.
- **Public Transport Directory**: Depot enquiry phone numbers and station directory.

### 2. Driver Operations Portal
- **Role-Based Driver Console**: Drivers authenticate using their unique Driver ID (e.g., `DRV-RAHUL`).
- **Roster & Schedule View**: Displays assigned bus, operator, corridor, scheduled departure times, and stops sequence for the active service day.
- **Active Trip Lifecycle**:
  - **Start Trip**: Starts live trip and enables phone GPS streaming.
  - **Simulate Run**: Handset-free simulation mode that advances the bus marker along the corridor road geometry for testing or automated playback.
  - **Station Arrival & Departure Checkpoints**: Record timestamps as the bus reaches or leaves stations.
  - **End Trip & Replay**: Closes active trip and generates completed metrics, with replay option for test corridors.

### 3. Dispatch & Fleet Management
- **Central Operations Dashboard**:
  - Daily overview metrics: active operators, registered buses, rostered drivers, stops, routes, active trips, delays (> 5 mins), and live audit logs.
  - **Start Operations Action**: Materializes today's trips automatically from active weekly recurring schedules.
- **Master Data Management**:
  - **Operators**: Add, edit, deactivate, or bulk-manage transit companies and state depots.
  - **Buses**: Manage fleet numbers, registration numbers, bus models, seating capacities (1–120), and assigned drivers, with strict duplicate checking.
  - **Drivers**: Register drivers with mandatory licence number uniqueness, linked user accounts, contact details, and operator assignments.
  - **Stops & Stations**: Create geographic stops with latitude/longitude validation, code generation, and automated transliteration deduplication.
  - **Routes & Geometries**: Configure ordered stops, auto/manual leg distances, and trigger OSRM highway road rebuilds.
  - **Schedules & Timetables**: Set weekly recurring departures (`ServiceDays: 1111111`) with automated collision detection for overlapping bus and driver assignments.
  - **Distance-Based Fare Slabs**: Configure pricing matrix per stop-pair with negative fare prevention.
- **Bulk CSV Upload & Validation**:
  - Upload CSV spreadsheets for stops, routes, buses, drivers, schedules, and fares.
  - Dry-run validation session previewing valid rows, warnings, and errors before database commit.
  - Built-in formula injection protection (neutralizes `=,+,-,@` spreadsheet payloads).
- **System Audit Trail**: Captures user ID, IP, action, entity, and timestamp for all administrative operations.

### 4. Core Real-Time & Routing Engine
- **Highway Geometry Reconstruction**: Fetches actual road geometries from OSRM to mirror real highway turns (e.g. NH-9) rather than straight-line chord segments.
- **Projection & Map Matching**: Projects GPS points to the nearest polyline segment to calculate precise distance along the route.
- **Stop Normalizer**: Phonetic Romanization matching (`Hissar` → `Hisar`, `Dhanger` → `Dhangar`) and transit suffix stripping (`Bus Stand`, `ISBT`, `Chowk`, `Bypass`) to prevent duplicate stop records.

### 5. EU Horizon TELEMETRY IoT Defense Framework
Conforming to the EU Horizon Europe **TELEMETRY Project** ([telemetry-project.eu](https://telemetry-project.eu/)) for automated security testing and telematics anomaly detection:
- **Hardware Telematics Ingestion (`POST /api/telemetry/ingest`)**: Secure gateway accepting hardware telematics streams from on-board units (OBUs) and GPS trackers (e.g., Teltonika, Queclink, OBD-II/CAN bus units).
- **Automated Cybersecurity Anomaly Engine**:
  - **GPS Spoofing & Teleportation Defense**: Detects mathematically impossible speeds (> 130 km/h) or coordinate leaps (> 3 km in under 10s) using Haversine kinematic delta analysis.
  - **Clock Skew & Replay Shield**: Enforces strict timestamp bounds (rejects > 3 min future drift or > 24 hr expired packets) to thwart replay attacks.
  - **GNSS Quality & Coordinate Sanitization**: Eliminates out-of-bounds coordinates and (0, 0) Null Island anomalies; flags degraded HDOP (> 8.0) or low satellite count (< 4).
  - **Sensor Consistency Check**: Identifies conflicting sensor reports (e.g., stationary speed reporting while position moves rapidly).
- **Dynamic Device Trust Scoring & Quarantine**:
  - Tracks rolling trust score `[0.0, 1.0]` per device.
  - Automatically isolates and quarantines devices with trust scores below `0.35`, shielding passenger live maps from fraudulent route diversion.
- **Admin Observability & Recalibration**:
  - `GET /api/telemetry/devices`: Real-time inventory of connected devices, trust states, and packet metrics.
  - `GET /api/telemetry/anomalies`: Live cybersecurity incident feed.
  - `GET /api/telemetry/stats`: Fleet-wide telematics health and quarantine rates.
  - `POST /api/telemetry/devices/{deviceUid}/reset-trust`: Dispatcher/admin trust restoration following physical inspection.

---

## Where It Can Be Used (Target Use Cases)

1. **State Roadways & Regional Transit Undertakings**:
   - Ideal for state transport corporations (e.g., Haryana Roadways, Punjab Roadways, DTC, UPSRTC, RSRTC) managing inter-district and rural feeder corridors.
2. **Private Inter-City Bus Fleet Operators**:
   - Private coach and sleeper operators offering scheduled inter-city passenger services with online tracking and passenger ETA notifications.
3. **University & College Campus Shuttles**:
   - Multi-bus campus transport tracking student and faculty shuttles between hostels, academic blocks, and suburban metro stations.
4. **Corporate & Industrial Employee Transport**:
   - Corporations and industrial hubs running dedicated employee shuttle fleets with fixed shift schedules, route monitoring, and stop arrival alerts.
5. **Municipal City Bus Services**:
   - Municipal corporations running fixed-route city bus systems with stage-based ticketing and digital passenger information displays (PIDS).

---

## System Requirements & Prerequisites

### Hardware & Network Specifications
| Parameter | Minimum (Small Fleet / Pilot) | Recommended (Production / State Depot) |
|---|---|---|
| **CPU** | 2 vCPUs (x86_64 or ARM64) | 4–8 vCPUs |
| **RAM** | 4 GB | 8–16 GB |
| **Disk Storage** | 20 GB SSD | 100+ GB NVMe SSD |
| **Network** | 10 Mbps continuous bandwidth | 100+ Mbps redundant uplink with static IP |
| **WebSocket Support** | Required on proxy/firewall | Required (SignalR long-polling & WebSockets) |

### Operating System & Runtimes
- **Operating System**: Linux (Ubuntu 22.04 LTS, Ubuntu 24.04 LTS, Debian 12, RHEL 9, Rocky Linux) or Windows Server 2022.
- **.NET Runtime**: .NET 8.0 SDK (for building) or ASP.NET Core Runtime 8.0 (for hosting).
- **Node.js**: Node.js 18.x or 20.x LTS with npm 9+ (for building frontend assets).
- **Database Engine**: Microsoft SQL Server 2019 / 2022 (Standard or Enterprise), Azure SQL Database, or SQL Server 2022 Linux container.

### Dependencies & Services
- **OSRM Routing Engine**: Access to `https://router.project-osrm.org/` or a dedicated self-hosted OSRM container for routing queries.
- **Reverse Proxy**: Nginx, Apache HTTP Server, Caddy, or Cloudflare / Azure Application Gateway with SSL/TLS termination.

---

## Security Audit & Hardening Measures

The following security controls have been audited and hardened across the platform:

| Security Domain | Vulnerability / Threat | Implemented Defense & Hardening |
|---|---|---|
| **Authentication & JWT** | Credential forging, stale sessions | Cryptographic HMAC-SHA256 signature enforcement, minimum 32-character key validation, production rejection of dev keys, and active user database verification on every token validation (`OnTokenValidated`). |
| **Brute Force & Flooding** | Password guessing / credential stuffing | Built-in ASP.NET Core Rate Limiting middleware (`auth-limit`) enforcing fixed-window request caps on `/api/auth/login` and `/api/auth/register`. |
| **Cross-Origin Requests (CORS)** | Cross-origin unauthorized calls | Production CORS policy restricted to explicit trusted domains defined in `Cors:AllowedOrigins` (disallows wildcard `*` in production). |
| **HTTP Security Headers** | Clickjacking, MIME-sniffing, XSS | Middleware injects `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-XSS-Protection: 1; mode=block`, `Content-Security-Policy`, and `Strict-Transport-Security` (HSTS). |
| **Data Input & CSV Injection** | Formula execution (`=cmd`, `@SUM`) | Neutralizes spreadsheet formulas in bulk CSV uploads by sanitizing cells starting with `=`, `@`, `+`, or non-numeric `-`. |
| **Data Integrity & Validation** | Duplicate licence numbers, overlapping schedules | Unique constraint checks for driver licences (`LicenseNumber`), duplicate bus registrations, valid route stop sequences, and server-side schedule collision detection. |
| **IoT Device & Telematics** | GPS spoofing, replay attacks, coordinate tampering | EU Horizon TELEMETRY framework: kinematic speed checking (> 130 km/h), clock skew protection, dynamic trust scoring, and automatic device quarantine. |
| **API Disclosure** | Public endpoint exposure | Swagger UI automatically restricted to Development environment (`app.Environment.IsDevelopment()`). |

---

## Automated Testing

An automated xUnit test suite (58 passing tests) is included in `tests/BusTrack.Api.Tests` covering security, domain algorithms, phonetic normalizers, business validation rules, and IoT telemetry anomaly detection.

### Running the Test Suite:

```bash
dotnet test tests/BusTrack.Api.Tests/BusTrack.Api.Tests.csproj -c Release
```

### Test Suite Highlights:
- **`TelemetryServiceTests` (EU Horizon TELEMETRY Framework)**:
  - Ingestion acceptance with initial `1.0` trust score and "Trusted" status.
  - Invalid coordinate rejection and (0,0) Null Island spoofing detection.
  - Clock skew drift detection (> 3 min future) and replay attack rejection (> 24 hr expired).
  - Degraded GNSS signal quality detection (satellites < 4, HDOP > 8.0).
  - Kinematic teleportation and impossible speed spoofing detection (> 130 km/h).
  - Automated device quarantine enforcement when trust score falls below 0.35.
  - Trust score recalibration and reset actions.
  - Cross-device telemetry statistics aggregation.
- **`AuthAndSecurityTests`**:
  - JWT token generation, claims verification (`NameIdentifier`, `Role`, `Email`, `driver_code`), and lifetime checks.
  - Cryptographic token signature verification and rejection of forged tokens.
  - Salted PBKDF2 password hashing verification and rejection of invalid passwords.
  - CSV formula injection sanitization tests.
- **`GeoAndRoutingTests`**:
  - Haversine distance accuracy, polyline length calculations, and point projection onto road lines.
  - ETA calculations at standard road speed (42 km/h).
- **`StopNormalizerTests`**:
  - Stop name suffix stripping (`Bus Stand`, `Bypass`, `ISBT`, `Chowk`).
  - Indian Romanization transliteration handling (`Hissar` → `Hisar`, `Dhanger` → `Dhangar`).
  - Deduplication and stop equivalence verification using Levenshtein distance and coordinates.
- **`BusinessRuleValidationTests`**:
  - Indian Standard Time (`IndiaClock`) conversions and DayIndex mappings.
  - Driver repository duplicate licence number rejection.
  - Deactivated driver account lockout checks.
  - Binary service days validation (`^[01]{7}$`) and route stop count rules.

---

## Ready to Upgrade in Production (Deployment Guide)

### 1. Environment Configuration

Create or configure `appsettings.Production.json` or supply environment variables:

```bash
# ASP.NET Core Hosting Environment
export ASPNETCORE_ENVIRONMENT=Production

# Database Connection (Encrypt=True with valid CA certificate)
export ConnectionStrings__SqlServer="Server=sql.yourdomain.com,1433;Database=WhereIsMyBus;User Id=BusTrackAppUser;Password=StrongProductionPassword123!;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True;"

# JWT Security (Generate a 64-character random string)
export Jwt__Key="super_secret_production_key_minimum_32_characters_randomly_generated_987654321"
export Jwt__Issuer="WhereIsMyBus"
export Jwt__Audience="WhereIsMyBus"

# Allowed CORS Origins
export Cors__AllowedOrigins__0="https://whereismybus.yourdomain.com"
```

### 2. Database Deployment

1. On your production SQL Server instance, ensure the database user has schema creation and read/write permissions on `WhereIsMyBus`.
2. Run database migration scripts in order:
   - `database/001_schema.sql` (Creates all tables, constraints, foreign keys, and indexes)
   - `database/002_seed.sql` (Initial operator, routes, and stops)
3. Change default seed account passwords immediately upon first deployment.

### 3. Backend API Publish & Systemd Service

1. Publish the API in Release mode:
   ```bash
   dotnet publish src/BusTrack.Api/BusTrack.Api.csproj -c Release -o /var/www/bustrack-api
   ```
2. Configure a Linux `systemd` service (`/etc/systemd/system/bustrack-api.service`):
   ```ini
   [Unit]
   Description=BusTrack .NET Web API
   After=network.target

   [Service]
   WorkingDirectory=/var/www/bustrack-api
   ExecStart=/usr/bin/dotnet /var/www/bustrack-api/BusTrack.Api.dll
   Restart=always
   RestartSec=10
   SyslogIdentifier=bustrack-api
   User=www-data
   Environment=ASPNETCORE_ENVIRONMENT=Production
   Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
   Environment=Kestrel__Endpoints__Http__Url=http://127.0.0.1:5088

   [Install]
   WantedBy=multi-user.target
   ```
3. Enable and start the service:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable bustrack-api
   sudo systemctl start bustrack-api
   ```

### 4. Frontend Production Build

1. Compile the React TypeScript application:
   ```bash
   cd client
   npm install --frozen-lockfile
   npm run build
   ```
2. The production bundle will be output to `client/dist`. Copy it to `/var/www/bustrack-client`.

### 5. Reverse Proxy & WebSocket Configuration (Nginx)

Configure Nginx with SSL/TLS (Let's Encrypt Certbot) and WebSocket proxying for SignalR:

```nginx
server {
    listen 80;
    server_name whereismybus.yourdomain.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name whereismybus.yourdomain.com;

    ssl_certificate /etc/letsencrypt/live/whereismybus.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/whereismybus.yourdomain.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    # Frontend Single Page App
    root /var/www/bustrack-client;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;
    }

    # API Endpoints
    location /api/ {
        proxy_pass http://127.0.0.1:5088;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # SignalR Real-Time WebSocket Hub
    location /hubs/ {
        proxy_pass http://127.0.0.1:5088;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
```

### 6. Health Check & Monitoring

- **Health Probe**: Monitor endpoint `GET /api/health`.
  - Returns `200 OK` with JSON `{ "status": "ok", "database": true, "operationsLive": true }`.
  - If database connection drops, returns `{ "status": "degraded", "database": false }`.
- **Application Logs**: Monitored via `journalctl -u bustrack-api -f` or forwarded to central observability platforms (Datadog, Prometheus/Grafana, Azure Application Insights).

### 7. Production Readiness Verification Checklist

- [x] **Automated Tests**: Unit and integration tests passing (`dotnet test -c Release`).
- [x] **JWT Secret**: Configured with a cryptographically random 256-bit key in production environment variables.
- [x] **CORS Policy**: Configured to allow only trusted production domain origins.
- [x] **Rate Limiting**: Active on authentication endpoints to prevent brute-force attacks.
- [x] **Security Headers**: HSTS, Content-Security-Policy, X-Frame-Options DENY, X-Content-Type-Options nosniff enabled.
- [x] **Database Passwords**: Replaced development SA passwords with a dedicated, least-privileged application user.
- [x] **Swagger UI**: Disabled in production (`app.Environment.IsDevelopment()`).
- [x] **SignalR WebSockets**: Nginx/reverse proxy configured with `Upgrade` and `Connection "upgrade"` headers.
- [x] **Backups**: Automated daily SQL Server database backups with point-in-time recovery.

---

## Local Development Setup

1. **Start SQL Server (Docker)**:
   ```bash
   docker compose up -d
   ```
2. **Start the API**:
   ```bash
   dotnet run --project src/BusTrack.Api
   ```
   *The API listens on `http://localhost:5088` and Swagger is available at `http://localhost:5088/swagger`.*
3. **Start the UI**:
   ```bash
   cd client
   npm install
   npm run dev
   ```
   *Open `http://localhost:4317`.*

---

## Default Accounts & Corridors

| Role | Username / Login | Password | Capabilities |
|---|---|---|---|
| **Dispatch Admin** | `admin@whereismybus.local` | `Admin@12345` | Master data CRUD, start daily operations, bulk CSV import, audit logs |
| **Driver** | `DRV-RAHUL` | `Driver@12345` | Driver console for route 101, start/end trip, GPS telemetry |
| **Driver** | `DRV-AMIT` | `Driver@12345` | Secondary driver console |
| **Passenger** | `meera@whereismybus.local` | `Passenger@12345` | Search trips, track live buses, save favourites, read alerts |

*Passengers can also search and track buses anonymously without creating an account.*
