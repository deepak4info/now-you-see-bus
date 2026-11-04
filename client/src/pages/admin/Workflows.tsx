import { useState } from "react";
import { 
  Network, 
  MapPin, 
  Bus, 
  CalendarClock, 
  FileSpreadsheet, 
  ShieldCheck, 
  ArrowRight, 
  Check, 
  Copy, 
  Activity, 
  Database,
  Radio,
  Server
} from "lucide-react";
import { Button } from "@/components/ui/button";

type Workflow = {
  id: string;
  title: string;
  shortDesc: string;
  icon: typeof Network;
  steps: {
    number: string;
    actor: string;
    action: string;
    detail: string;
    status: string;
  }[];
  mermaid: string;
};

const workflows: Workflow[] = [
  {
    id: "architecture",
    title: "1. System Architecture & Gateway",
    shortDesc: "End-to-end communication across React, ASP.NET Core 8 Web API, SignalR, and SQL Server.",
    icon: Network,
    steps: [
      {
        number: "01",
        actor: "Clients",
        action: "Passenger / Driver / Admin Request",
        detail: "Passengers browse on React web, drivers send mobile telemetry, dispatch manages master data.",
        status: "HTTPS / REST"
      },
      {
        number: "02",
        actor: "Security Layer",
        action: "Security Headers & Rate Limiting",
        detail: "Injects CSP, HSTS, X-Frame-Options: DENY. Evaluates fixed-window rate limiter (10 req/min for auth).",
        status: "Middleware"
      },
      {
        number: "03",
        actor: "Authentication",
        action: "JWT Verification & Revocation Check",
        detail: "Cryptographic signature validation and real-time active user database verification via OnTokenValidated.",
        status: "HMAC-SHA256"
      },
      {
        number: "04",
        actor: "Real-time Hub",
        action: "SignalR WebSocket Streaming",
        detail: "Clients subscribe to 'bus-{fleet}' or 'live' group to stream GPS coordinates without polling.",
        status: "WebSockets"
      },
      {
        number: "05",
        actor: "Storage & Services",
        action: "SQL Server & OSRM Engine",
        detail: "EF Core connects to db_ad0bfd_busdb. OSRM calculates high-resolution highway geometry.",
        status: "db_ad0bfd_busdb"
      }
    ],
    mermaid: `flowchart TD
    P["Passenger Web (React)"] --> SEC["Security & Rate Limiter"]
    D["Driver Console"] --> SEC
    A["Dispatch Control Room"] --> SEC
    SEC --> JWT["JWT Verification (OnTokenValidated)"]
    JWT --> API["ASP.NET Core 8 API"]
    API <--> HUB["SignalR Hub (/hubs/tracking)"]
    API <--> SQL[("SQL Server (db_ad0bfd_busdb)")]
    API <--> OSRM["Project OSRM Routing"]`
  },
  {
    id: "passenger",
    title: "2. Passenger Search & Live Tracking",
    shortDesc: "From corridor search to interactive Leaflet map tracking and dynamic arrival ETAs.",
    icon: MapPin,
    steps: [
      {
        number: "01",
        actor: "Passenger",
        action: "Enter Origin & Destination",
        detail: "Selects departure stop and arrival stop (e.g., Hisar to Fatehabad).",
        status: "User Input"
      },
      {
        number: "02",
        actor: "Stop Normalizer",
        action: "Phonetic Transliteration & Suffix Stripping",
        detail: "Normalizes Indian Romanization ('Hissar' -> 'Hisar') and strips suffixes ('Bus Stand', 'Bypass').",
        status: "Normalizer"
      },
      {
        number: "03",
        actor: "Search Engine",
        action: "Match Active Routes & Schedules",
        detail: "Filters routes where origin stop precedes destination stop in SequenceNo order.",
        status: "SQL Query"
      },
      {
        number: "04",
        actor: "Tracking View",
        action: "Open /track/:fleet & Connect WebSocket",
        detail: "Mounts Leaflet map, renders highway polyline, and joins SignalR room 'bus-{fleet}'.",
        status: "SignalR Room"
      },
      {
        number: "05",
        actor: "Live Progress",
        action: "Dynamic ETAs & Stop Status",
        detail: "Calculates minutes to boarding stop and destination at 42 km/h road speed with delay badge.",
        status: "Live Updates"
      }
    ],
    mermaid: `flowchart TD
    Start(["Passenger Visits Site"]) --> Input["Search From & To Stops"]
    Input --> Match["Normalizer Matches Stops"]
    Match --> Query["Query Schedules & Live Trips"]
    Query --> Cards["Display Live Buses & ETAs"]
    Cards --> OpenTrack["Open /track/{fleet}"]
    OpenTrack --> Map["Render Leaflet Map & SignalR Stream"]`
  },
  {
    id: "driver",
    title: "3. Driver Trip Telemetry & Lifecycle",
    shortDesc: "Driver authentication, daily roster load, GPS location ingestion, and station arrival logs.",
    icon: Bus,
    steps: [
      {
        number: "01",
        actor: "Driver Portal",
        action: "Sign In with Driver ID",
        detail: "Driver authenticates with unique Driver ID (e.g., DRV-RAHUL) and password.",
        status: "Driver Auth"
      },
      {
        number: "02",
        actor: "Roster Screen",
        action: "Load Assigned Bus & Today's Trips",
        detail: "Displays fleet HR39-1020, corridor stops, timetable, and departure slot.",
        status: "Roster Active"
      },
      {
        number: "03",
        actor: "Start Run",
        action: "Start Handset GPS or Simulator",
        detail: "Driver clicks 'Start Trip'. Handset GPS begins streaming or simulation mode runs corridor at 42 km/h.",
        status: "Running"
      },
      {
        number: "04",
        actor: "Map Projection",
        action: "Point Projection & Delay Calculation",
        detail: "API projects raw lat/lon to nearest polyline chord, updates distance-along-route, and calculates delay.",
        status: "Telemetry"
      },
      {
        number: "05",
        actor: "Station Arrival",
        action: "Record Checkpoints & End Trip",
        detail: "Driver confirms arrival/departure at intermediate depots and ends trip at destination.",
        status: "Completed"
      }
    ],
    mermaid: `flowchart TD
    Login["Driver Signs in (DRV-RAHUL)"] --> Roster["Load Assigned Bus & Schedule"]
    Roster --> Start["Click 'Start Trip'"]
    Start --> GPS["Stream GPS or Corridor Simulation"]
    GPS --> Project["API Projects Point to Highway Polyline"]
    Project --> Hub["SignalR Broadcasts Live Location"]
    Hub --> End["Confirm Depots & End Trip"]`
  },
  {
    id: "dispatch",
    title: "4. Dispatch Operations & Schedule Materialization",
    shortDesc: "Transforming weekly timetable templates into active service runs for the day.",
    icon: CalendarClock,
    steps: [
      {
        number: "01",
        actor: "Supervisor",
        action: "Open Control Room",
        detail: "Dispatch opens Admin Overview to monitor fleet readiness and scheduled corridors.",
        status: "Admin Portal"
      },
      {
        number: "02",
        actor: "Start Ops",
        action: "Click 'Start Operations'",
        detail: "Sets SystemSetting 'OperationsLive = true' in database.",
        status: "Operations Live"
      },
      {
        number: "03",
        actor: "Calendar Logic",
        action: "Evaluate ServiceDays Binary Pattern",
        detail: "Evaluates DayOfWeek against 7-digit binary mask (e.g. '1111111' runs Mon-Sun).",
        status: "Day Index 0..6"
      },
      {
        number: "04",
        actor: "Trip Generator",
        action: "Materialize Today's Trips",
        detail: "Inserts trip records with status 'Scheduled', bus, driver, and departure timestamps.",
        status: "Trips Created"
      },
      {
        number: "05",
        actor: "Audit Trail",
        action: "Log Operations Action",
        detail: "Writes audit record: 'StartOperations: Created N trips' with admin user attribution.",
        status: "Audit Saved"
      }
    ],
    mermaid: `flowchart LR
    Admin["Dispatch Admin"] --> Click["Click 'Start Operations'"]
    Click --> SetLive["Set OperationsLive = true"]
    SetLive --> Filter["Filter Schedules (ServiceDays == '1')"]
    Filter --> Create["Materialize Today's Trips (Scheduled)"]
    Create --> Audit["Record Action in AuditLogs"]`
  },
  {
    id: "bulkupload",
    title: "5. Bulk CSV Upload & Validation",
    shortDesc: "Multi-entity spreadsheet import with dry-run validation, duplicate checks, and formula injection defense.",
    icon: FileSpreadsheet,
    steps: [
      {
        number: "01",
        actor: "Admin Upload",
        action: "Upload CSV Spreadsheet",
        detail: "Selects CSV containing stops, routes, buses, drivers, schedules, or fares.",
        status: "File Upload"
      },
      {
        number: "02",
        actor: "Sanitizer",
        action: "Neutralize CSV Formula Injection",
        detail: "SanitizeCsvCell prepends apostrophe to values starting with '=', '@', '+', or non-numeric '-'.",
        status: "Anti-CVI"
      },
      {
        number: "03",
        actor: "Dry-run Validator",
        action: "Validate Business Rules & Overlaps",
        detail: "Checks duplicate codes, licence numbers, valid lat/lon, and schedule collision times.",
        status: "Validation"
      },
      {
        number: "04",
        actor: "Preview Screen",
        action: "Review Session & Warnings",
        detail: "Admin reviews total rows, valid entities, warnings, and unmapped references.",
        status: "Preview"
      },
      {
        number: "05",
        actor: "Database Commit",
        action: "Transactional Upsert & Highway Rebuild",
        detail: "Commits clean records to SQL Server and triggers OSRM route geometry reconstruction.",
        status: "Committed"
      }
    ],
    mermaid: `flowchart TD
    File["Admin Uploads CSV"] --> Sanitize["Neutralize Formula Injection (=, +, -, @)"]
    Sanitize --> Validate["Validate Rules, Duplicates & Overlaps"]
    Validate --> Preview["Session Preview (Errors & Warnings)"]
    Preview --> Confirm["Admin Confirms Commit"]
    Confirm --> Upsert["Transactional SQL Upsert"]
    Upsert --> Geo["Reconstruct Highway Road Geometry"]`
  },
  {
    id: "security",
    title: "6. Authentication & Security Middleware",
    shortDesc: "Defensive controls protecting public and authenticated endpoints.",
    icon: ShieldCheck,
    steps: [
      {
        number: "01",
        actor: "Security Headers",
        action: "Inject Defensive HTTP Headers",
        detail: "Appends X-Content-Type-Options: nosniff, X-Frame-Options: DENY, CSP, and HSTS.",
        status: "HTTP Headers"
      },
      {
        number: "02",
        actor: "Rate Limiter",
        action: "Fixed Window Request Limiter",
        detail: "Enforces 10 requests per minute on /api/auth/login and /api/auth/register (Status 429).",
        status: "10 req/min"
      },
      {
        number: "03",
        actor: "CORS Policy",
        action: "Restricted Production Origin",
        detail: "Blocks arbitrary wildcard (*) cross-origin access in production environments.",
        status: "CORS Whitelist"
      },
      {
        number: "04",
        actor: "Token Lifetime",
        action: "Real-time User Invalidation Hook",
        detail: "OnTokenValidated verifies user.IsActive directly against database on every request.",
        status: "Instant Lockout"
      },
      {
        number: "05",
        actor: "Authorization",
        action: "Role-Based Access Control (RBAC)",
        detail: "Restricts /api/admin to Admin role and /api/driver to Driver role (Status 403).",
        status: "RBAC Enforced"
      }
    ],
    mermaid: `flowchart TD
    Req["Incoming Request"] --> Headers["Inject Security Headers & CORS"]
    Headers --> Rate["Check Rate Limiter (Auth: 10/min)"]
    Rate --> JWT["Validate JWT Signature & Expiry"]
    JWT --> ActiveDB["OnTokenValidated: User.IsActive == true?"]
    ActiveDB -- "Inactive" --> Err["Reject Token (401)"]
    ActiveDB -- "Active" --> Role["Enforce Role (Admin / Driver / Passenger)"]`
  }
];

export function WorkflowsPage() {
  const [selected, setSelected] = useState<Workflow>(workflows[0]);
  const [copied, setCopied] = useState(false);

  function copyMermaid() {
    navigator.clipboard.writeText(selected.mermaid);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  return (
    <div className="space-y-6">
      <div>
        <p className="text-xs uppercase tracking-[0.18em] text-pine">Architecture & Workflows</p>
        <h1 className="font-display text-3xl">System Flowcharts</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Visual operational pipelines, runtime communication channels, and security architectures powering Where Is My Bus.
        </p>
      </div>

      {/* Tabs */}
      <div className="flex flex-wrap gap-2 border-b pb-3">
        {workflows.map((wf) => {
          const Icon = wf.icon;
          const isActive = wf.id === selected.id;
          return (
            <button
              key={wf.id}
              onClick={() => setSelected(wf)}
              className={`flex items-center gap-2 rounded-lg border px-3 py-2 text-xs font-medium transition-colors ${
                isActive
                  ? "border-pine bg-pine text-white"
                  : "border-border bg-card text-foreground hover:bg-muted"
              }`}
            >
              <Icon className="h-4 w-4" />
              <span>{wf.title.split(". ")[1]}</span>
            </button>
          );
        })}
      </div>

      {/* Selected Workflow Overview */}
      <div className="rounded-xl border bg-card p-5">
        <div className="flex flex-col justify-between gap-4 sm:flex-row sm:items-center">
          <div>
            <h2 className="text-xl font-bold">{selected.title}</h2>
            <p className="mt-1 text-sm text-muted-foreground">{selected.shortDesc}</p>
          </div>
          <Button variant="outline" size="sm" onClick={copyMermaid} className="flex items-center gap-2">
            {copied ? <Check className="h-4 w-4 text-emerald-600" /> : <Copy className="h-4 w-4" />}
            <span>{copied ? "Copied Mermaid" : "Copy Mermaid Code"}</span>
          </Button>
        </div>

        {/* Visual Step Pipeline */}
        <div className="mt-6 grid gap-3 md:grid-cols-5">
          {selected.steps.map((s, idx) => (
            <div key={idx} className="relative flex flex-col justify-between rounded-lg border bg-muted/40 p-3.5">
              <div>
                <div className="flex items-center justify-between">
                  <span className="font-mono text-xs font-bold text-pine">{s.number}</span>
                  <span className="rounded bg-background px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
                    {s.status}
                  </span>
                </div>
                <p className="mt-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">{s.actor}</p>
                <p className="mt-0.5 text-sm font-bold text-foreground">{s.action}</p>
                <p className="mt-2 text-xs text-muted-foreground leading-relaxed">{s.detail}</p>
              </div>

              {idx < selected.steps.length - 1 && (
                <div className="hidden md:block absolute -right-3 top-1/2 -translate-y-1/2 z-10 text-muted-foreground/60">
                  <ArrowRight className="h-4 w-4" />
                </div>
              )}
            </div>
          ))}
        </div>
      </div>

      {/* Mermaid Code Display Block */}
      <div className="rounded-xl border bg-ink p-5 text-paper">
        <div className="flex items-center justify-between border-b border-white/10 pb-3">
          <div className="flex items-center gap-2">
            <Radio className="h-4 w-4 text-gold" />
            <span className="font-mono text-xs text-gold">Mermaid Flowchart Syntax (GitHub Ready)</span>
          </div>
          <button
            onClick={copyMermaid}
            className="text-xs text-white/70 hover:text-white flex items-center gap-1"
          >
            {copied ? <Check className="h-3.5 w-3.5 text-emerald-400" /> : <Copy className="h-3.5 w-3.5" />}
            {copied ? "Copied" : "Copy"}
          </button>
        </div>
        <pre className="mt-3 overflow-x-auto font-mono text-xs text-emerald-300 leading-relaxed">
          {selected.mermaid}
        </pre>
      </div>

      {/* Production Infrastructure Card */}
      <div className="grid gap-4 sm:grid-cols-3">
        <div className="rounded-xl border bg-card p-4">
          <div className="flex items-center gap-2 text-pine font-semibold text-sm">
            <Database className="h-4 w-4" />
            <span>Database Backend</span>
          </div>
          <p className="mt-1 font-mono text-xs text-foreground">sql8006.site4now.net</p>
          <p className="text-xs text-muted-foreground">Catalog: db_ad0bfd_busdb (18 tables, transactional)</p>
        </div>

        <div className="rounded-xl border bg-card p-4">
          <div className="flex items-center gap-2 text-pine font-semibold text-sm">
            <Activity className="h-4 w-4" />
            <span>Real-time Telemetry</span>
          </div>
          <p className="mt-1 font-mono text-xs text-foreground">SignalR /hubs/tracking</p>
          <p className="text-xs text-muted-foreground">Broadcasts GPS pings & road projections</p>
        </div>

        <div className="rounded-xl border bg-card p-4">
          <div className="flex items-center gap-2 text-pine font-semibold text-sm">
            <Server className="h-4 w-4" />
            <span>Road Speed & ETAs</span>
          </div>
          <p className="mt-1 font-mono text-xs text-foreground">42.0 km/h Road Velocity</p>
          <p className="text-xs text-muted-foreground">OSRM Highway geometry projection (NH-9)</p>
        </div>
      </div>
    </div>
  );
}
