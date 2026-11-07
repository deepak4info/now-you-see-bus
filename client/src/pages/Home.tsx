import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  Activity,
  ArrowLeftRight,
  ArrowRight,
  BusFront,
  CalendarDays,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  CircleCheck,
  Clock,
  HelpCircle,
  MapPin,
  Navigation,
  Phone,
  Radio,
  Route as RouteIcon,
  Search,
  ShieldCheck,
  Smartphone,
  Sparkles,
  Users,
  Volume2,
  Wifi,
  X,
  type LucideIcon,
} from "lucide-react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { MapView, type BusPin } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { TrackingNotice, computeTrackingStatus } from "@/components/TrackingNotice";
import { BusDigitalTwinViewer } from "@/components/BusDigitalTwinViewer";
import { SoundToggle } from "@/components/SoundToggle";
import { Button } from "@/components/ui/button";
import { resolveUrl, useQuery } from "@/lib/api";
import { ago, formatDateWithWeekday, formatEta, getIndiaDate, isDateOnly } from "@/lib/format";
import { useBusLive } from "@/lib/live";
import { transitVoice } from "@/lib/voice";
import { getRecentSearch, saveRecentSearch, type RecentSearch } from "@/lib/recentSearch";
import type { PublicRoute, SearchResponse, StopOption, TrackSnapshot } from "@/lib/types";
import { cn } from "@/lib/utils";

const EMPTY_PATH: TrackSnapshot["path"] = [];
const EMPTY_STOPS: TrackSnapshot["stops"] = [];

const POPULAR_CORRIDORS = [
  { from: "Hisar", to: "Fatehabad", tag: "NH-9 Express" },
  { from: "Sirsa", to: "Delhi", tag: "Intercity" },
  { from: "Fatehabad", to: "Chandigarh", tag: "Capital Link" },
  { from: "Rohtak", to: "Delhi", tag: "Frequent" },
];

const FAQS = [
  {
    q: "Do I need to create an account or log in to track buses?",
    a: "No. Live bus tracking, 3D digital twin inspection, schedule lookups, and route maps are 100% free and require zero login. Just open the app and find your bus immediately.",
  },
  {
    q: "How accurate is the live ETA and bus location?",
    a: "Every Haryana Roadways bus transmits GPS coordinates, speed, and heading every 3 to 5 seconds. Our telemetry engine snaps raw coordinates to real highway curves (via OSRM) and computes arrival times based on live speed and historical depot delays.",
  },
  {
    q: "Does this work offline when mobile internet is slow or unavailable?",
    a: "Yes! WhereIsMyBus is a Progressive Web App (PWA). Once installed or opened, all frequent routes, depot timetables, and inquiry numbers are cached locally on your device, allowing you to check schedules even with zero network.",
  },
  {
    q: "How does the Crowd & Seat Availability Sensor work?",
    a: "Automated door optical sensors tally passengers boarding and alighting at each bus stand. The system calculates real-time occupancy percentage so commuters can see if seats are available before the bus arrives.",
  },
  {
    q: "Can I hear stop arrival announcements in Hindi?",
    a: "Yes. Our built-in transit voice engine announces upcoming stations and live delays in both Hindi and English. Simply tap the Sound icon in the top bar to enable voice assistance.",
  },
];

export function HomePage() {
  const [homeParams] = useSearchParams();
  const [recentSearch, setRecentSearch] = useState<RecentSearch>(
    () => getRecentSearch() ?? { from: "Hisar", to: "Fatehabad" },
  );
  const [travelDate, setTravelDate] = useState(() => {
    const date = homeParams.get("date");
    return isDateOnly(date) ? date : getIndiaDate();
  });
  const stops = useQuery<StopOption[]>("/api/stops");
  const recentSearchQuery = useQuery<SearchResponse>(
    `/api/search?from=${encodeURIComponent(recentSearch.from)}&to=${encodeURIComponent(recentSearch.to)}&date=${travelDate}`,
  );
  const recentBusSeed = recentSearchQuery.data?.results[0] ?? null;
  const live = useBusLive(
    recentBusSeed?.fleetNumber ?? null,
    recentBusSeed,
    recentBusSeed?.routeId,
    travelDate === getIndiaDate(),
  );
  const routesQuery = useQuery<PublicRoute[]>("/api/routes");
  const routes = routesQuery.data ?? [];
  const [selectedRouteId, setSelectedRouteId] = useState<number | null>(() => {
    const p = homeParams.get("routeId");
    return p ? Number(p) : null;
  });

  const liveBusesQuery = useQuery<TrackSnapshot[]>("/api/live");
  const [liveBuses, setLiveBuses] = useState<TrackSnapshot[]>([]);
  const [liveUpdatesError, setLiveUpdatesError] = useState("");
  const [from, setFrom] = useState(() => homeParams.get("from") ?? recentSearch.from);
  const [to, setTo] = useState(() => homeParams.get("to") ?? recentSearch.to);
  const [error, setError] = useState("");
  const [stopFieldsVersion, setStopFieldsVersion] = useState(0);
  const [openFaqIndex, setOpenFaqIndex] = useState<number | null>(0);
  const navigate = useNavigate();

  useEffect(() => {
    if (liveBusesQuery.data) setLiveBuses(liveBusesQuery.data);
  }, [liveBusesQuery.data]);

  // Determine active route corridor: selected by user, or one with running bus, or matching search, or first route
  const activeRoute = routes.find((r) => r.id === selectedRouteId)
    ?? routes.find((r) => r.liveCount > 0)
    ?? routes.find((r) => r.id === recentBusSeed?.routeId)
    ?? routes[0]
    ?? null;
  const activeRouteId = activeRoute?.id ?? null;

  const routeSnapshotQuery = useQuery<TrackSnapshot>(
    activeRouteId ? `/api/routes/${activeRouteId}/snapshot` : null,
  );

  useEffect(() => {
    let active = true;
    const conn = new HubConnectionBuilder()
      .withUrl(resolveUrl("/hubs/tracking"))
      .withAutomaticReconnect()
      .build();

    conn.on("location", (snapshot: TrackSnapshot) => {
      setLiveBuses((current) => {
        const index = current.findIndex(
          (row) => row.fleetNumber === snapshot.fleetNumber && row.routeId === snapshot.routeId,
        );
        if (snapshot.status !== "Running") {
          return index < 0 ? current : current.filter((_, rowIndex) => rowIndex !== index);
        }
        if (index < 0) return [...current, snapshot];
        const next = current.slice();
        next[index] = snapshot;
        return next;
      });
    });

    conn.onreconnected(() => {
      if (active) {
        void conn.invoke("JoinLive").catch((err: unknown) => {
          if (active) setLiveUpdatesError(err instanceof Error ? err.message : "Could not reconnect to live updates.");
        });
      }
    });

    conn.start()
      .then(() => {
        if (!active) return conn.stop();
        return conn.invoke("JoinLive");
      })
      .then(() => {
        if (active) setLiveUpdatesError("");
      })
      .catch((err: unknown) => {
        if (active) setLiveUpdatesError(err instanceof Error ? err.message : "Could not connect to live updates.");
      });

    return () => {
      active = false;
      void conn.stop();
    };
  }, []);

  function searchFor(origin: string, destination: string, date: string) {
    if (!origin.trim() || !destination.trim()) {
      setError("Please select both boarding stop and destination.");
      return;
    }
    if (origin.trim().toLocaleLowerCase() === destination.trim().toLocaleLowerCase()) {
      setError("Boarding stop and destination must be different.");
      return;
    }
    setError("");
    const nextSearch = { from: origin.trim(), to: destination.trim() };
    saveRecentSearch(nextSearch.from, nextSearch.to);
    setRecentSearch(nextSearch);
    setFrom(nextSearch.from);
    setTo(nextSearch.to);

    // Speak announcement if voice enabled
    transitVoice.speak({
      en: `Searching live buses from ${nextSearch.from} to ${nextSearch.to}.`,
      hi: `${nextSearch.from} से ${nextSearch.to} के लिए बसें खोजी जा रही हैं।`,
    });

    navigate(`/search?from=${encodeURIComponent(nextSearch.from)}&to=${encodeURIComponent(nextSearch.to)}&date=${date}`);
  }

  function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    searchFor(from, to, travelDate);
  }

  const mapTrip = (activeRouteId && live?.routeId === activeRouteId ? live : null)
    ?? routeSnapshotQuery.data
    ?? live
    ?? recentBusSeed;

  const mapPath = (activeRouteId && live?.routeId === activeRouteId ? live?.path : null)
    ?? routeSnapshotQuery.data?.path
    ?? live?.path
    ?? recentBusSeed?.path
    ?? EMPTY_PATH;

  const mapStops = (activeRouteId && live?.routeId === activeRouteId ? live?.stops : null)
    ?? routeSnapshotQuery.data?.stops
    ?? live?.stops
    ?? recentBusSeed?.stops
    ?? EMPTY_STOPS;

  const recentTrackParams = recentBusSeed
    ? new URLSearchParams({
        from: recentSearch.from,
        to: recentSearch.to,
        routeId: String(recentBusSeed.routeId),
        date: travelDate,
      })
    : null;

  const mapBuses: BusPin[] = [];
  if (
    mapTrip?.fleetNumber &&
    mapTrip.fleetNumber !== "SCHED" &&
    mapTrip.latitude != null &&
    mapTrip.longitude != null
  ) {
    mapBuses.push({
      label: mapTrip.fleetNumber,
      fleetNumber: mapTrip.fleetNumber,
      latitude: mapTrip.latitude,
      longitude: mapTrip.longitude,
      heading: mapTrip.heading,
      routeCode: mapTrip.routeCode,
      speedKmph: mapTrip.speedKmph,
      status: mapTrip.status,
      variant: mapTrip.status === "Running" ? "live" : "last-known",
      recordedAt: mapTrip.recordedAt,
    });
  }

  const trackingStatus = computeTrackingStatus({
    isOnline: true,
    tripStatus: mapTrip?.status ?? "Scheduled",
    recordedAt: mapTrip?.recordedAt,
    hasCoordinates: mapTrip?.latitude != null && mapTrip?.longitude != null,
  });

  const mapTrackQuery = mapTrip
    ? mapTrip === live && recentTrackParams
      ? recentTrackParams.toString()
      : new URLSearchParams({ routeId: String(mapTrip.routeId) }).toString()
    : null;

  return (
    <div className="relative mx-auto max-w-7xl px-4 pb-20 pt-4 sm:px-6 lg:px-8">
      {/* ─────────────────────────────────────────────────────────────
          SECTION 1: HERO (DARPAN / HEALTHORA 3D DIGITAL TWIN DESIGN)
          Split Grid: 1.05fr (Story, Badge & Search) : 0.95fr (3D Model)
          ───────────────────────────────────────────────────────────── */}
      <section className="relative pt-2 pb-10 sm:pt-6 sm:pb-14">
        <div className="grid grid-cols-1 items-center gap-8 lg:grid-cols-[1.05fr_0.95fr] lg:gap-10">
          
          {/* Left Column: Pill Badge, High-Impact Typography, Search & Trust Proof */}
          <div className="flex flex-col">
            {/* Pill Badge with Pulsing Emerald Dot */}
            <div className="inline-flex w-fit items-center gap-2.5 rounded-full border border-emerald-200/80 bg-emerald-50/70 px-4 py-1.5 text-xs font-bold uppercase tracking-wider text-emerald-900 shadow-2xs backdrop-blur-md">
              <span className="relative flex h-2 w-2">
                <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75" />
                <span className="relative inline-flex rounded-full h-2 w-2 bg-emerald-500" />
              </span>
              <span>Haryana Roadways · Live Service Intelligence</span>
            </div>

            {/* Main Headline */}
            <h1 className="mt-4 font-display text-4xl font-extrabold tracking-tight text-ink sm:text-5xl lg:text-[3.2rem] lg:leading-[1.12]">
              Understand your bus journey on an{" "}
              <span className="bg-gradient-to-r from-emerald-600 via-teal-600 to-emerald-800 bg-clip-text text-transparent">
                interactive 3D Digital Twin
              </span>
              .
            </h1>

            {/* Subtitle */}
            <p className="mt-4 max-w-xl text-base leading-relaxed text-muted-foreground sm:text-lg">
              Real-time GPS telemetry, road-snapped highway routing, live seat availability, and bilingual voice announcements — simple, clear, and reliable for daily commuters.
            </p>

            {/* Interactive Journey Search Card */}
            <div id="journey-search" className="mt-7 rounded-3xl border border-border/80 bg-card p-5 shadow-lg shadow-black/5 sm:p-6">
              <div className="flex items-center justify-between pb-3">
                <div className="flex items-center gap-2">
                  <span className="grid h-7 w-7 place-items-center rounded-lg bg-emerald-100 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300">
                    <Search className="h-4 w-4" />
                  </span>
                  <span className="text-xs font-bold uppercase tracking-wider text-muted-foreground">
                    Quick Journey Search
                  </span>
                </div>
                <span className="text-xs font-medium text-emerald-700 dark:text-emerald-400">
                  50+ Depots Connected
                </span>
              </div>

              <form onSubmit={search} className="grid gap-3">
                <div className="relative grid w-full grid-cols-1 gap-2.5 sm:grid-cols-2 sm:gap-x-10">
                  <StopField
                    key={`from-${stopFieldsVersion}`}
                    label="From (Boarding)"
                    value={from}
                    stops={stops.data ?? []}
                    loading={stops.loading}
                    onChange={setFrom}
                    icon={BusFront}
                  />

                  {/* Swap Button */}
                  <button
                    type="button"
                    aria-label="Swap boarding and destination"
                    className="mx-auto grid h-9 w-9 place-items-center rounded-full border border-border bg-card text-muted-foreground shadow-2xs transition-transform hover:scale-110 hover:border-emerald-500 hover:text-emerald-700 sm:absolute sm:left-1/2 sm:top-[38px] sm:z-10 sm:-translate-x-1/2"
                    onClick={() => {
                      setFrom(to);
                      setTo(from);
                      setStopFieldsVersion((v) => v + 1);
                      setError("");
                    }}
                  >
                    <ArrowLeftRight className="h-4 w-4" />
                  </button>

                  <StopField
                    key={`to-${stopFieldsVersion}`}
                    label="To (Destination)"
                    value={to}
                    stops={stops.data ?? []}
                    loading={stops.loading}
                    onChange={setTo}
                    icon={MapPin}
                  />

                  {/* Date Picker */}
                  <label
                    htmlFor="travel-date"
                    className="flex min-h-[72px] min-w-0 items-center gap-3 rounded-2xl border border-border/80 bg-background/50 px-3.5 py-2.5 transition-colors focus-within:border-emerald-500 focus-within:ring-2 focus-within:ring-emerald-500/15"
                  >
                    <CalendarDays className="h-5 w-5 shrink-0 text-emerald-700" />
                    <span className="grid min-w-0 flex-1 gap-0.5">
                      <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                        Date of Travel
                      </span>
                      <input
                        id="travel-date"
                        type="date"
                        required
                        value={travelDate}
                        onChange={(e) => setTravelDate(e.target.value)}
                        className="h-7 w-full border-0 bg-transparent p-0 text-sm font-bold text-ink outline-none"
                      />
                      <span className="truncate text-[11px] text-muted-foreground">
                        {travelDate === getIndiaDate()
                          ? `${formatDateWithWeekday(travelDate)} · Today`
                          : formatDateWithWeekday(travelDate)}
                      </span>
                    </span>
                  </label>

                  {/* Submit Button */}
                  <Button
                    type="submit"
                    size="lg"
                    className="h-full min-h-[64px] w-full rounded-2xl bg-emerald-700 text-base font-bold text-white shadow-md shadow-emerald-700/20 hover:bg-emerald-800 transition-all sm:min-h-[72px]"
                  >
                    <Search className="h-5 w-5 mr-1" />
                    Find Live Buses
                  </Button>
                </div>

                {stops.error && <p className="text-xs text-destructive">{stops.error}</p>}
                {error && <p className="text-xs text-destructive">{error}</p>}

                {/* Popular Corridor Quick Select */}
                <div className="flex flex-wrap items-center gap-1.5 border-t border-border/70 pt-3">
                  <span className="text-[11px] font-semibold text-muted-foreground mr-1">
                    Popular Corridors:
                  </span>
                  {POPULAR_CORRIDORS.map((c, i) => (
                    <button
                      key={i}
                      type="button"
                      onClick={() => searchFor(c.from, c.to, travelDate)}
                      className="inline-flex items-center gap-1 rounded-full border border-border/80 bg-muted/40 px-2.5 py-1 text-xs font-medium text-ink transition-colors hover:border-emerald-400 hover:bg-emerald-50 hover:text-emerald-800"
                    >
                      <span>{c.from} ➔ {c.to}</span>
                      <span className="text-[10px] text-muted-foreground">({c.tag})</span>
                    </button>
                  ))}
                </div>
              </form>
            </div>

            {/* Trust Proof Bar (Matching Darpan's Trust Signals) */}
            <div className="mt-5 flex flex-wrap items-center gap-y-2 gap-x-5 text-xs text-muted-foreground">
              <span className="inline-flex items-center gap-1.5">
                <ShieldCheck className="h-4 w-4 text-emerald-600" /> No login needed
              </span>
              <span className="inline-flex items-center gap-1.5">
                <Wifi className="h-4 w-4 text-emerald-600" /> Sub-second SignalR stream
              </span>
              <span className="inline-flex items-center gap-1.5">
                <Volume2 className="h-4 w-4 text-emerald-600" /> Hindi & English voice
              </span>
              <span className="inline-flex items-center gap-1.5">
                <Smartphone className="h-4 w-4 text-emerald-600" /> Offline PWA ready
              </span>
            </div>
          </div>

          {/* Right Column: 3D Bus Digital Twin Viewer */}
          <div className="relative">
            <BusDigitalTwinViewer />
          </div>
        </div>
      </section>

      {/* ─────────────────────────────────────────────────────────────
          SECTION 2: LIVE NETWORK CORRIDORS & ACTIVE FLEET BAR
          ───────────────────────────────────────────────────────────── */}
      {routes.length > 0 && (
        <section className="mb-8 rounded-2xl border border-emerald-200/70 bg-emerald-50/40 p-3 sm:px-5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-emerald-950">
              <RouteIcon className="h-4 w-4 text-emerald-700" />
              <span>Live Highway Corridors</span>
            </div>
            <div className="flex flex-wrap items-center gap-1.5 overflow-x-auto py-0.5">
              {routes.map((r) => {
                const isSelected = r.id === activeRoute?.id;
                return (
                  <button
                    key={r.id}
                    type="button"
                    onClick={() => {
                      setSelectedRouteId(r.id);
                      setFrom(r.origin);
                      setTo(r.destination);
                    }}
                    className={cn(
                      "inline-flex items-center gap-1.5 rounded-full px-3.5 py-1 text-xs transition-all whitespace-nowrap cursor-pointer",
                      isSelected
                        ? "bg-emerald-700 text-white font-semibold shadow-xs ring-2 ring-emerald-500/30"
                        : "border border-border/80 bg-white text-muted-foreground hover:bg-emerald-50 hover:text-ink font-medium"
                    )}
                  >
                    {r.liveCount > 0 ? (
                      <span className="h-2 w-2 rounded-full bg-emerald-400 animate-pulse" />
                    ) : (
                      <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/40" />
                    )}
                    <span>Route {r.code}: {r.origin} → {r.destination}</span>
                    {r.liveCount > 0 && (
                      <span className={cn(
                        "rounded-full px-1.5 py-0.2 text-[9px] font-bold uppercase",
                        isSelected ? "bg-white/20 text-white" : "bg-emerald-100 text-emerald-800"
                      )}>
                        {r.liveCount} live
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          </div>
        </section>
      )}

      {/* ─────────────────────────────────────────────────────────────
          SECTION 3: LIVE ROAD MAP & PROGRESSION
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-14 overflow-hidden rounded-3xl border border-border/80 bg-card shadow-sm">
        <TrackingNotice status={trackingStatus} />

        <div className="flex min-h-[76px] flex-wrap items-center justify-between gap-3 border-b border-border/70 bg-card px-5 py-3 sm:px-6">
          <div>
            <div className="flex items-center gap-2">
              <p className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-wider text-emerald-700">
                <Navigation size={13} className="rotate-45" /> Live Road Map & Highway Projection
              </p>
              {activeRoute && (
                <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-[10px] font-bold uppercase text-emerald-800">
                  Route {activeRoute.code} · {activeRoute.distanceKm ? `${activeRoute.distanceKm} km` : ""}
                </span>
              )}
            </div>
            <p className="mt-1 truncate font-display text-lg sm:text-xl font-bold">
              {mapTrip?.fleetNumber && mapTrip.fleetNumber !== "SCHED"
                ? `Bus ${mapTrip.fleetNumber} · ${mapTrip.routeName || activeRoute?.name}`
                : (activeRoute ? activeRoute.name : "Explore bus routes")}
            </p>
            <p className="mt-0.5 text-xs text-muted-foreground">
              Route: {mapStops.map((s) => s.name).join(" → ")}
              {mapTrip?.nextStop ? ` · Next Stop: ${mapTrip.nextStop}` : ""}
              {mapTrip?.etaMinutes != null ? ` · ETA: ${formatEta(mapTrip.etaMinutes)}` : ""}
            </p>
          </div>

          <div className="flex shrink-0 items-center gap-2">
            {mapTrip && <StatusBadge status={mapTrip.status} />}
            {mapTrip?.fleetNumber && mapTrip.fleetNumber !== "SCHED" ? (
              <Button variant="outline" size="sm" asChild className="rounded-full">
                <Link to={`/track/${mapTrip.fleetNumber}?${mapTrackQuery || ""}`}>
                  Track in full view <ArrowRight className="h-4 w-4 ml-1" />
                </Link>
              </Button>
            ) : (
              <Button variant="outline" size="sm" onClick={() => searchFor(activeRoute?.origin || from, activeRoute?.destination || to, travelDate)} className="rounded-full">
                View timetable <ArrowRight className="h-4 w-4 ml-1" />
              </Button>
            )}
          </div>
        </div>

        {/* Map Canvas */}
        <div className="relative min-h-[380px] sm:min-h-[460px] w-full bg-muted/40">
          <MapView
            path={mapPath}
            stops={mapStops}
            buses={mapBuses}
            focusBus={mapBuses.length === 1}
            className="absolute inset-0 h-full w-full"
            onBusClick={(busPin) => {
              navigate(`/track/${encodeURIComponent(busPin.fleetNumber || busPin.label)}`);
            }}
          />
        </div>

        {/* Map Footer Info */}
        {activeRoute && (
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border/70 bg-card/80 px-5 py-3 text-xs text-muted-foreground sm:px-6">
            <div className="flex flex-wrap items-center gap-4">
              <span><b>Depot Origin:</b> {activeRoute.origin}</span>
              <span><b>Destination:</b> {activeRoute.destination}</span>
              {activeRoute.distanceKm && <span><b>Highway Distance:</b> {activeRoute.distanceKm} km</span>}
            </div>
            <div className="flex items-center gap-2">
              <span className="flex h-2 w-2 rounded-full bg-emerald-500" />
              <span className="font-semibold text-foreground">
                {mapBuses.length > 0 ? `${mapBuses.length} active live bus(es)` : "Scheduled timetable active"}
              </span>
            </div>
          </div>
        )}
      </section>

      {/* ─────────────────────────────────────────────────────────────
          SECTION 4: THREE SIMPLE STEPS (HEALTHORA / DARPAN MIRROR)
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-16">
        <div className="text-center max-w-2xl mx-auto mb-10">
          <div className="inline-flex items-center gap-2 rounded-full bg-emerald-100/70 px-3.5 py-1 text-xs font-bold uppercase tracking-wider text-emerald-800">
            <Sparkles className="h-3.5 w-3.5" />
            <span>Effortless Commuting</span>
          </div>
          <h2 className="mt-3 font-display text-3xl font-extrabold sm:text-4xl text-ink">
            Three simple steps to never wait blindly
          </h2>
          <p className="mt-2 text-sm text-muted-foreground sm:text-base">
            No more standing on dusty highway stands wondering where your bus is.
          </p>
        </div>

        <div className="grid gap-6 md:grid-cols-3">
          <div className="group rounded-3xl border border-border/80 bg-card p-6 shadow-xs transition-all hover:-translate-y-1 hover:border-emerald-500/50 hover:shadow-lg">
            <div className="flex items-center justify-between">
              <span className="grid h-12 w-12 place-items-center rounded-2xl bg-emerald-100 text-emerald-800 transition-colors group-hover:bg-emerald-700 group-hover:text-white">
                <MapPin className="h-6 w-6" />
              </span>
              <span className="font-display text-xs font-bold uppercase tracking-widest text-muted-foreground/60">
                Step 01
              </span>
            </div>
            <h3 className="mt-5 font-display text-xl font-bold text-ink">
              Search your route or bus number
            </h3>
            <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
              Select your departure and arrival bus stands from 50+ depots across Haryana and Delhi NCR. Instant timetables, halts, and official fare rates.
            </p>
          </div>

          <div className="group rounded-3xl border border-border/80 bg-card p-6 shadow-xs transition-all hover:-translate-y-1 hover:border-emerald-500/50 hover:shadow-lg">
            <div className="flex items-center justify-between">
              <span className="grid h-12 w-12 place-items-center rounded-2xl bg-emerald-100 text-emerald-800 transition-colors group-hover:bg-emerald-700 group-hover:text-white">
                <Activity className="h-6 w-6" />
              </span>
              <span className="font-display text-xs font-bold uppercase tracking-widest text-muted-foreground/60">
                Step 02
              </span>
            </div>
            <h3 className="mt-5 font-display text-xl font-bold text-ink">
              Inspect 3D Digital Twin & GPS Telemetry
            </h3>
            <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
              Watch your bus progress along the highway in real time. Inspect speed, satellite link, driver console, and remaining kilometers with zero guessing.
            </p>
          </div>

          <div className="group rounded-3xl border border-border/80 bg-card p-6 shadow-xs transition-all hover:-translate-y-1 hover:border-emerald-500/50 hover:shadow-lg">
            <div className="flex items-center justify-between">
              <span className="grid h-12 w-12 place-items-center rounded-2xl bg-emerald-100 text-emerald-800 transition-colors group-hover:bg-emerald-700 group-hover:text-white">
                <Volume2 className="h-6 w-6" />
              </span>
              <span className="font-display text-xs font-bold uppercase tracking-widest text-muted-foreground/60">
                Step 03
              </span>
            </div>
            <h3 className="mt-5 font-display text-xl font-bold text-ink">
              Listen to Voice Alerts & Track Offline
            </h3>
            <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
              Get spoken stop announcements in Hindi and English. Even when the cellular signal drops in rural areas, cached PWA timetables keep you informed.
            </p>
          </div>
        </div>
      </section>

      {/* ─────────────────────────────────────────────────────────────
          SECTION 5: HOW BUS TRACKING WORKS (TECH PIPELINE EXPLAINER)
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-16 rounded-3xl border border-emerald-200/80 bg-gradient-to-br from-emerald-950 via-emerald-900 to-teal-950 p-6 sm:p-10 text-white shadow-xl">
        <div className="max-w-2xl">
          <span className="inline-flex items-center gap-2 text-xs font-bold uppercase tracking-widest text-emerald-300">
            <Radio className="h-3.5 w-3.5" /> High-Throughput Telemetry Pipeline
          </span>
          <h2 className="mt-2 font-display text-3xl font-extrabold sm:text-4xl text-white">
            How bus tracking actually works under the hood
          </h2>
          <p className="mt-3 text-sm leading-relaxed text-emerald-100/80 sm:text-base">
            From vehicle hardware to commuter browser: four synchronized layers processing thousands of data points every second.
          </p>
        </div>

        <div className="mt-8 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <div className="rounded-2xl border border-white/10 bg-white/5 p-4.5 backdrop-blur-sm">
            <span className="font-mono text-xs font-bold text-emerald-400">01 · Hardware</span>
            <h4 className="mt-1 font-display text-base font-bold text-white">On-Bus GPS Unit</h4>
            <p className="mt-1 text-xs leading-relaxed text-emerald-200/70">
              High-sensitivity NavIC/GPS receivers broadcast coordinates, heading, and speed over 4G every 3 seconds.
            </p>
          </div>

          <div className="rounded-2xl border border-white/10 bg-white/5 p-4.5 backdrop-blur-sm">
            <span className="font-mono text-xs font-bold text-emerald-400">02 · Backend API</span>
            <h4 className="mt-1 font-display text-base font-bold text-white">.NET 8 Concurrent Engine</h4>
            <p className="mt-1 text-xs leading-relaxed text-emerald-200/70">
              OutOfProcess Kestrel handles thread-safe telemetry ingestion, rate-limiting, and SQL spatial updates.
            </p>
          </div>

          <div className="rounded-2xl border border-white/10 bg-white/5 p-4.5 backdrop-blur-sm">
            <span className="font-mono text-xs font-bold text-emerald-400">03 · Routing</span>
            <h4 className="mt-1 font-display text-base font-bold text-white">OSRM Road-Snapping</h4>
            <p className="mt-1 text-xs leading-relaxed text-emerald-200/70">
              Raw wandering coordinates are snapped to actual OpenStreetMap highway curvature without jagged jumps.
            </p>
          </div>

          <div className="rounded-2xl border border-white/10 bg-white/5 p-4.5 backdrop-blur-sm">
            <span className="font-mono text-xs font-bold text-emerald-400">04 · Delivery</span>
            <h4 className="mt-1 font-display text-base font-bold text-white">SignalR & 3D UI</h4>
            <p className="mt-1 text-xs leading-relaxed text-emerald-200/70">
              Persistent WebSocket channels push telemetry directly to the commuter's 3D twin with 18ms latency.
            </p>
          </div>
        </div>
      </section>

      {/* ─────────────────────────────────────────────────────────────
          SECTION 6: ACTIVE BUSES ON THE ROAD (REAL-TIME FLEET CARDS)
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-16">
        <div className="flex flex-wrap items-end justify-between gap-3 mb-6">
          <div>
            <p className="text-xs font-bold uppercase tracking-wider text-emerald-800">
              Real-Time Fleet Status
            </p>
            <h2 className="mt-1 font-display text-3xl font-extrabold text-ink sm:text-4xl">
              Buses currently on the road
            </h2>
            <p className="mt-1 text-sm text-muted-foreground">
              Live location, upcoming halt, and on-time status.
            </p>
          </div>
          <span className="flex items-center gap-2 rounded-full border border-emerald-300 bg-emerald-50 px-3.5 py-1 text-xs font-bold text-emerald-800">
            <span className="h-2 w-2 rounded-full bg-emerald-500 animate-pulse" />
            <span>Live SignalR Streaming</span>
          </span>
        </div>

        {liveBusesQuery.loading && (
          <p className="text-sm text-muted-foreground">Checking live bus telemetry…</p>
        )}
        {liveBusesQuery.error && (
          <p className="text-sm text-destructive">{liveBusesQuery.error}</p>
        )}
        {liveUpdatesError && (
          <p className="text-sm text-amber-600 mb-3">{liveUpdatesError}</p>
        )}

        {!liveBusesQuery.loading && !liveBusesQuery.error && liveBuses.length === 0 && (
          <div className="flex flex-col gap-4 rounded-3xl border border-border/80 bg-card p-6 shadow-xs sm:flex-row sm:items-center sm:p-8">
            <span className="grid h-14 w-14 shrink-0 place-items-center rounded-2xl bg-emerald-100 text-emerald-800">
              <BusFront size={28} />
            </span>
            <div className="min-w-0 flex-1">
              <h3 className="font-bold text-lg text-ink">No buses are running right now</h3>
              <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
                Scheduled departures are still available across all corridors. Search your stops to check timetables and fares.
              </p>
            </div>
            <Button
              variant="outline"
              className="shrink-0 rounded-full"
              onClick={() => document.getElementById("journey-search")?.scrollIntoView({ behavior: "smooth" })}
            >
              Search schedules <ArrowRight size={16} className="ml-1" />
            </Button>
          </div>
        )}

        {liveBuses.length > 0 && (
          <div className="grid gap-4 md:grid-cols-2">
            {liveBuses.map((busUpdate) => (
              <article
                key={`${busUpdate.fleetNumber}-${busUpdate.routeId}`}
                className="rounded-3xl border border-border/80 bg-card p-6 shadow-xs transition-all hover:border-emerald-500/50 hover:shadow-md"
              >
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <span className="text-xs font-bold uppercase tracking-wider text-muted-foreground">
                      {busUpdate.operatorName} · Route {busUpdate.routeCode}
                    </span>
                    <h3 className="mt-1 font-display text-2xl font-bold">Bus {busUpdate.fleetNumber}</h3>
                    <p className="text-xs text-muted-foreground mt-0.5">
                      {busUpdate.registrationNumber} · {busUpdate.routeName}
                    </p>
                  </div>
                  <StatusBadge status={busUpdate.status} />
                </div>

                <dl className="mt-5 grid grid-cols-2 gap-3 text-sm">
                  <div className="rounded-xl border border-border/60 bg-muted/20 p-2.5">
                    <dt className="text-xs text-muted-foreground">Current Halt</dt>
                    <dd className="font-bold text-ink mt-0.5">{busUpdate.currentStop ?? "Between stops"}</dd>
                  </div>
                  <div className="rounded-xl border border-border/60 bg-muted/20 p-2.5">
                    <dt className="text-xs text-muted-foreground">Next Halt</dt>
                    <dd className="font-bold text-ink mt-0.5">
                      {busUpdate.nextStop ?? "—"}
                      {busUpdate.etaMinutes != null ? ` · ${formatEta(busUpdate.etaMinutes)}` : ""}
                    </dd>
                  </div>
                  <div className="rounded-xl border border-border/60 bg-muted/20 p-2.5">
                    <dt className="text-xs text-muted-foreground">Schedule Variance</dt>
                    <dd className="font-bold text-emerald-700 mt-0.5">
                      {busUpdate.outsideSlot
                        ? "Special service"
                        : busUpdate.delayMinutes != null && busUpdate.delayMinutes > 2
                          ? `+${busUpdate.delayMinutes} min delay`
                          : "On time"}
                    </dd>
                  </div>
                  <div className="rounded-xl border border-border/60 bg-muted/20 p-2.5">
                    <dt className="text-xs text-muted-foreground">Last Ping</dt>
                    <dd className="font-bold text-ink mt-0.5">{ago(busUpdate.recordedAt)}</dd>
                  </div>
                </dl>

                <div className="mt-4 flex items-center justify-between">
                  <span className="text-xs text-muted-foreground font-mono">
                    Speed: {busUpdate.speedKmph != null ? `${busUpdate.speedKmph} km/h` : "Stationary"}
                  </span>
                  <Button variant="outline" size="sm" asChild className="rounded-full">
                    <Link to={`/track/${busUpdate.fleetNumber}?routeId=${busUpdate.routeId}`}>
                      Track on map <ArrowRight className="h-3.5 w-3.5 ml-1" />
                    </Link>
                  </Button>
                </div>
              </article>
            ))}
          </div>
        )}
      </section>

      {/* ─────────────────────────────────────────────────────────────
          SECTION 7: ACCORDION FAQS (MATCHING DARPAN / HEALTHORA)
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-16">
        <div className="text-center max-w-2xl mx-auto mb-8">
          <div className="inline-flex items-center gap-2 rounded-full bg-emerald-100/70 px-3.5 py-1 text-xs font-bold uppercase tracking-wider text-emerald-800">
            <HelpCircle className="h-3.5 w-3.5" />
            <span>Got Questions?</span>
          </div>
          <h2 className="mt-3 font-display text-3xl font-extrabold sm:text-4xl text-ink">
            Frequently asked questions
          </h2>
          <p className="mt-2 text-sm text-muted-foreground">
            Everything you need to know about live bus tracking across Haryana.
          </p>
        </div>

        <div className="mx-auto max-w-3xl space-y-3">
          {FAQS.map((faq, idx) => {
            const isOpen = openFaqIndex === idx;
            return (
              <div
                key={idx}
                className="overflow-hidden rounded-2xl border border-border/80 bg-card transition-colors hover:border-emerald-300"
              >
                <button
                  type="button"
                  onClick={() => setOpenFaqIndex(isOpen ? null : idx)}
                  className="flex w-full items-center justify-between gap-4 p-5 text-left font-display text-base font-bold text-ink"
                >
                  <span>{faq.q}</span>
                  <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-muted/60 text-muted-foreground">
                    {isOpen ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                  </span>
                </button>
                {isOpen && (
                  <div className="border-t border-border/60 px-5 pb-5 pt-3 text-sm leading-relaxed text-muted-foreground">
                    {faq.a}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </section>

      {/* ─────────────────────────────────────────────────────────────
          SECTION 8: BOTTOM CTA BANNER (DARPAN STYLE)
          ───────────────────────────────────────────────────────────── */}
      <section className="rounded-3xl border border-emerald-200 bg-gradient-to-r from-emerald-50 via-teal-50 to-emerald-50 p-8 text-center sm:p-12 shadow-sm">
        <h2 className="font-display text-3xl font-extrabold sm:text-4xl text-emerald-950">
          Ready to catch your next bus?
        </h2>
        <p className="mt-2 max-w-xl mx-auto text-sm text-muted-foreground sm:text-base">
          Start planning your journey now, check official depot contact numbers, or install our lightweight PWA for offline access.
        </p>

        <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
          <Button
            size="lg"
            className="rounded-full bg-emerald-700 px-6 font-bold text-white hover:bg-emerald-800"
            onClick={() => document.getElementById("journey-search")?.scrollIntoView({ behavior: "smooth" })}
          >
            <Search className="h-4 w-4 mr-1.5" /> Plan a Journey
          </Button>

          <Button variant="outline" size="lg" className="rounded-full" asChild>
            <Link to="/enquiry-numbers">
              <Phone className="h-4 w-4 mr-1.5 text-emerald-700" /> Bus Stand Enquiry Numbers
            </Link>
          </Button>
        </div>
      </section>
    </div>
  );
}

function StopField({
  label,
  value,
  stops,
  loading,
  onChange,
  icon: Icon,
}: {
  label: string;
  value: string;
  stops: StopOption[];
  loading: boolean;
  onChange: (value: string) => void;
  icon: LucideIcon;
}) {
  const id = label.startsWith("From") ? "boarding-stop" : "destination-stop";
  const fieldRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [inputValue, setInputValue] = useState(value);
  const [activeIndex, setActiveIndex] = useState(0);
  const searchText = inputValue.trim().toLocaleLowerCase();
  const popularNames = ["Hisar", "Fatehabad", "Bhuna", "Barwala", "Tohana", "Sirsa", "Delhi"];
  const popularNameSet = new Set(popularNames.map((name) => name.toLocaleLowerCase()));
  const popularStops = popularNames
    .map((name) => stops.find((stop) => stop.name.toLocaleLowerCase() === name.toLocaleLowerCase()))
    .filter((stop): stop is StopOption => stop !== undefined);
  const suggestions = searchText
    ? stops.filter((stop) =>
        [stop.name, stop.code, stop.city, stop.address].some((field) =>
          field?.toLocaleLowerCase().includes(searchText)
        )
      )
    : popularStops;

  useEffect(() => {
    if (value) setInputValue(value);
  }, [value]);

  useEffect(() => {
    if (!open) return;
    function dismiss(event: PointerEvent) {
      if (!fieldRef.current?.contains(event.target as Node)) setOpen(false);
    }
    document.addEventListener("pointerdown", dismiss);
    return () => document.removeEventListener("pointerdown", dismiss);
  }, [open]);

  function selectStop(stop: StopOption) {
    setInputValue(stop.name);
    onChange(stop.name);
    setOpen(false);
  }

  function handleKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Escape") {
      setOpen(false);
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      setOpen(true);
      if (suggestions.length) {
        setActiveIndex((index) => (index + (event.key === "ArrowDown" ? 1 : -1) + suggestions.length) % suggestions.length);
      }
      return;
    }
    if (event.key === "Enter" && open && suggestions[activeIndex]) {
      event.preventDefault();
      selectStop(suggestions[activeIndex]);
    }
  }

  return (
    <div
      ref={fieldRef}
      className="relative flex min-h-[72px] min-w-0 items-center gap-3 rounded-2xl border border-border/80 bg-background/50 px-3.5 py-2.5 transition-colors focus-within:border-emerald-500 focus-within:ring-2 focus-within:ring-emerald-500/15"
    >
      <Icon className="h-5 w-5 shrink-0 text-emerald-700" />
      <div className="grid min-w-0 flex-1 gap-0.5">
        <label htmlFor={id} className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
          {label}
        </label>
        <input
          id={id}
          type="text"
          role="combobox"
          autoComplete="off"
          aria-autocomplete="list"
          aria-expanded={open}
          aria-controls={`${id}-suggestions`}
          aria-activedescendant={open && suggestions[activeIndex] ? `${id}-option-${suggestions[activeIndex].id}` : undefined}
          placeholder="Search bus stand, city..."
          aria-busy={loading}
          className="h-7 w-full border-0 bg-transparent p-0 text-sm font-bold text-ink outline-none placeholder:font-normal placeholder:text-muted-foreground"
          value={inputValue}
          onFocus={() => {
            setOpen(true);
            setActiveIndex(0);
          }}
          onKeyDown={handleKeyDown}
          onChange={(event) => {
            const next = event.target.value;
            setInputValue(next);
            setActiveIndex(0);
            setOpen(true);
            if (next !== value) onChange("");
          }}
        />
      </div>
      {inputValue ? (
        <button
          type="button"
          aria-label={`Clear ${label.toLocaleLowerCase()} search`}
          className="grid h-7 w-7 shrink-0 place-items-center rounded-full text-muted-foreground hover:bg-muted hover:text-ink"
          onClick={() => {
            setInputValue("");
            onChange("");
            setActiveIndex(0);
            setOpen(true);
          }}
        >
          <X className="h-3.5 w-3.5" />
        </button>
      ) : (
        <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" />
      )}

      {open && (
        <div
          id={`${id}-suggestions`}
          role="listbox"
          className="absolute left-0 right-0 top-full z-50 mt-1 max-h-80 overflow-y-auto rounded-2xl border border-border/80 bg-card p-3 shadow-xl"
        >
          <h3 className="px-2 pb-2 text-xs font-bold uppercase tracking-wider text-muted-foreground">
            {loading
              ? "Loading stops…"
              : searchText
                ? `Search results for “${inputValue.trim()}”`
                : "Popular Bus Stands"}
          </h3>
          {loading ? (
            <p className="rounded-lg p-3 text-sm text-muted-foreground">Loading stops…</p>
          ) : suggestions.length ? (
            suggestions.map((stop, index) => (
              <button
                key={stop.id}
                id={`${id}-option-${stop.id}`}
                type="button"
                role="option"
                aria-selected={stop.name === value}
                className={cn(
                  "flex w-full items-center justify-between gap-3 rounded-xl px-3 py-2.5 text-left text-sm transition-colors hover:bg-emerald-50",
                  index === activeIndex ? "bg-emerald-50 text-emerald-900 font-semibold" : ""
                )}
                onMouseEnter={() => setActiveIndex(index)}
                onClick={() => selectStop(stop)}
              >
                <span className="flex min-w-0 items-start gap-2.5">
                  <MapPin className="mt-0.5 h-4 w-4 shrink-0 text-emerald-700" />
                  <span className="min-w-0">
                    <span className="block font-medium text-ink">{stop.name}</span>
                    <span className="block truncate text-xs text-muted-foreground">
                      {!searchText && popularNameSet.has(stop.name.toLocaleLowerCase())
                        ? "Depot stand"
                        : [stop.city, stop.address, stop.code].filter(Boolean).join(" · ")}
                    </span>
                  </span>
                </span>
                <span className="shrink-0 text-muted-foreground">→</span>
              </button>
            ))
          ) : (
            <div className="p-4 text-center">
              <p className="text-sm font-medium text-ink">No bus stands found</p>
              <p className="mt-1 text-xs text-muted-foreground">Try typing another city name.</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
