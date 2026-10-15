# Where is my bus

A bus tracking platform for a Haryana corridor: passengers search a trip, drivers run it, and dispatch owns the master data. The sample service is route 101, Hisar → Barwala → Tohana → Fatehabad, on bus HR39-1020 (fleet 102) at 8:00 AM with driver Rahul.

## Stack

- SQL Server schema in `database/`
- ASP.NET Core 8 API with JWT auth, EF Core, and SignalR
- React + Vite passenger, driver, and dispatch UI

## Run it

1. Start SQL Server (Docker):

```bash
docker compose up -d
```

The API expects this local connection string:

`Server=127.0.0.1,1433;Database=WhereIsMyBus;User Id=sa;Password=BusTrack_Sql123!;TrustServerCertificate=True;Encrypt=True`

Change the password in `docker-compose.yml` and `src/BusTrack.Api/appsettings.json` together. This password is for local development only.

2. Start the API (it creates `WhereIsMyBus`, runs the SQL scripts, and seeds accounts):

```bash
dotnet run --project src/BusTrack.Api
```

The API listens on `http://localhost:5088`. Swagger is at `http://localhost:5088/swagger` in Development.

3. Start the UI:

```bash
cd client
npm install
npm run dev
```

Open `http://localhost:4317`.

To create the database yourself, open `database/WhereIsMyBus.sql` in SSMS or Azure Data Studio and execute it. That one script creates `WhereIsMyBus`, the tables, and the sample corridor, including the four demo accounts. `001_schema.sql` and `002_seed.sql` are the same script split in two. Starting the API runs them as well, and skips any rows that are already there.

## Sign in

| Who | Login | Password |
| --- | --- | --- |
| Dispatch admin | `admin@whereismybus.local` | `Admin@12345` |
| Driver Rahul | `DRV-RAHUL` | `Driver@12345` |
| Driver Amit | `DRV-AMIT` | `Driver@12345` |
| Passenger Meera | `meera@whereismybus.local` | `Passenger@12345` |

Passengers can search and track without an account. An account is required to save a favourite and read alerts. New passengers can register. Driver and admin accounts are created by dispatch.

## Walk the sample day

1. Sign in as admin and open Dispatch. The corridor is already loaded.
2. Press **Start operations**. Today's trips are created and drivers can leave the depot. After that, each new service day opens its own trips, including when the API restarts.
3. Sign in as `DRV-RAHUL`. The console shows bus HR39-1020 on route 101.
4. Press **Start trip** to send phone GPS, or **Play the Hisar–Fatehabad run** to move the bus along the route without a handset.
5. As a passenger, search Hisar → Fatehabad. Bus 102 is listed with status, the current stop, the next stop, fare, and ETA.
6. Open `/track/102`. The map updates over SignalR as new GPS arrives. No refresh.
7. When the bus reaches Fatehabad — or the driver presses **End trip** — the passenger sees **Trip completed**.
8. Meera's favourite is already bus 102, so she gets an alert when that trip starts and when it finishes.

ETAs use the remaining distance along the route at 42 km/h. Demo playback advances the marker about 6.6 km per minute, so the 117 km corridor finishes in roughly 18 minutes. The minutes on screen still come from the road speed. A trip more than three hours away from its printed departure is labelled outside the printed slot on the dispatch report.

## Modules

| Module | Where it lives |
| --- | --- |
| Identity and access | `api/auth`, JWT roles Admin, Driver, Passenger |
| Transport master data | Operators, buses, drivers |
| Route and stop management | Stops, ordered route stops, generated geometry |
| Schedule and fare | Weekly schedules, distance fares |
| Bus operations | Today's trips, start and end |
| GPS and live tracking | `GpsPings` plus the SignalR hub `/hubs/tracking` |
| ETA and delay | Route matching, next stop, minutes late versus the timetable |
| Passenger search | `GET /api/search` |
| Passenger journey | Bus detail, `/track/{fleet}`, favourites |
| Notifications | In-app alerts for saved buses |
| Administration | Dispatch screens and **Start operations** |
| Monitoring and reporting | Live board and today's trip report |
