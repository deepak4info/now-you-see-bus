import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  ArrowLeftRight,
  ArrowRight,
  BusFront,
  CalendarDays,
  CheckCircle2,
  ChevronDown,
  Clock,
  Compass,
  HelpCircle,
  MapPin,
  Navigation,
  Phone,
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
  { from: "Hisar", to: "Fatehabad" },
  { from: "Sirsa", to: "Delhi" },
  { from: "Fatehabad", to: "Chandigarh" },
  { from: "Rohtak", to: "Delhi" },
  { from: "Hisar", to: "Sirsa" },
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
  const navigate = useNavigate();

  useEffect(() => {
    if (liveBusesQuery.data) setLiveBuses(liveBusesQuery.data);
  }, [liveBusesQuery.data]);

  // Determine active route corridor
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

    transitVoice.speak({
      en: `Searching buses from ${nextSearch.from} to ${nextSearch.to}.`,
      hi: `${nextSearch.from} से ${nextSearch.to} की बसें खोजी जा रही हैं।`,
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
    <div className="mx-auto max-w-5xl px-4 pb-20 pt-6 sm:px-6">
      {/* ─────────────────────────────────────────────────────────────
          1. SIMPLE, WELCOMING HERO & SEARCH
          Clean, centered, frictionless search card
          ───────────────────────────────────────────────────────────── */}
      <section className="text-center pt-2 pb-8 sm:pt-6 sm:pb-10">
        <div className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50/80 px-3.5 py-1 text-xs font-semibold text-emerald-800 shadow-2xs">
          <span className="flex h-2 w-2 rounded-full bg-emerald-500 animate-pulse" />
          <span>Haryana Roadways · Live Bus Tracker</span>
        </div>

        <h1 className="mt-3 font-display text-3xl font-extrabold tracking-tight text-ink sm:text-4xl lg:text-5xl">
          Where is your bus?
        </h1>
        <p className="mt-2 text-sm text-muted-foreground sm:text-base max-w-xl mx-auto">
          Find live bus locations, departure times, and routes across Haryana and Delhi NCR.
        </p>

        {/* The Clean Search Box */}
        <div className="mt-6 mx-auto max-w-3xl rounded-3xl border border-border/80 bg-card p-4 sm:p-6 shadow-md shadow-emerald-950/5 text-left">
          <form onSubmit={search} className="grid gap-3">
            <div className="relative grid grid-cols-1 gap-2.5 sm:grid-cols-2 sm:gap-x-8">
              <StopField
                key={`from-${stopFieldsVersion}`}
                label="From (Boarding Stand)"
                value={from}
                stops={stops.data ?? []}
                loading={stops.loading}
                onChange={setFrom}
                icon={BusFront}
              />

              {/* Swap Button */}
              <button
                type="button"
                aria-label="Swap stops"
                className="mx-auto grid h-8 w-8 place-items-center rounded-full border border-border bg-card text-muted-foreground shadow-2xs transition-transform hover:scale-110 hover:border-emerald-500 hover:text-emerald-700 sm:absolute sm:left-1/2 sm:top-[34px] sm:z-10 sm:-translate-x-1/2 cursor-pointer"
                onClick={() => {
                  setFrom(to);
                  setTo(from);
                  setStopFieldsVersion((v) => v + 1);
                  setError("");
                }}
              >
                <ArrowLeftRight className="h-3.5 w-3.5" />
              </button>

              <StopField
                key={`to-${stopFieldsVersion}`}
                label="To (Destination Stand)"
                value={to}
                stops={stops.data ?? []}
                loading={stops.loading}
                onChange={setTo}
                icon={MapPin}
              />

              {/* Travel Date */}
              <label
                htmlFor="travel-date"
                className="flex min-h-[66px] min-w-0 items-center gap-3 rounded-2xl border border-border/80 bg-background/50 px-3.5 py-2 transition-colors focus-within:border-emerald-500 focus-within:ring-2 focus-within:ring-emerald-500/15 cursor-pointer"
              >
                <CalendarDays className="h-5 w-5 shrink-0 text-emerald-700" />
                <span className="grid min-w-0 flex-1 gap-0.5">
                  <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                    Journey Date
                  </span>
                  <input
                    id="travel-date"
                    type="date"
                    required
                    value={travelDate}
                    onChange={(e) => setTravelDate(e.target.value)}
                    className="h-6 w-full border-0 bg-transparent p-0 text-xs font-bold text-ink outline-none cursor-pointer"
                  />
                  <span className="truncate text-[11px] text-muted-foreground">
                    {travelDate === getIndiaDate()
                      ? `${formatDateWithWeekday(travelDate)} · Today`
                      : formatDateWithWeekday(travelDate)}
                  </span>
                </span>
              </label>

              {/* Search Button */}
              <Button
                type="submit"
                size="lg"
                className="h-full min-h-[58px] w-full rounded-2xl bg-emerald-700 text-sm font-bold text-white shadow-sm hover:bg-emerald-800 transition-all sm:min-h-[66px] cursor-pointer"
              >
                <Search className="h-4 w-4 mr-1.5" />
                Search Buses
              </Button>
            </div>

            {stops.error && <p className="text-xs text-destructive text-center">{stops.error}</p>}
            {error && <p className="text-xs text-destructive text-center">{error}</p>}

            {/* Popular Route Shortcuts */}
            <div className="flex flex-wrap items-center justify-center gap-1.5 border-t border-border/60 pt-3">
              <span className="text-[11px] font-semibold text-muted-foreground mr-1">
                Quick Routes:
              </span>
              {POPULAR_CORRIDORS.map((c, i) => (
                <button
                  key={i}
                  type="button"
                  onClick={() => searchFor(c.from, c.to, travelDate)}
                  className="inline-flex items-center gap-1 rounded-full border border-border/80 bg-muted/40 px-3 py-1 text-xs font-medium text-ink transition-colors hover:border-emerald-500 hover:bg-emerald-50 hover:text-emerald-900 cursor-pointer"
                >
                  <span>{c.from} ➔ {c.to}</span>
                </button>
              ))}
            </div>
          </form>
        </div>

        {/* Simple Trust Pills */}
        <div className="mt-4 flex flex-wrap items-center justify-center gap-x-5 gap-y-1.5 text-xs text-muted-foreground">
          <span className="inline-flex items-center gap-1">
            <CheckCircle2 className="h-3.5 w-3.5 text-emerald-600" /> No login needed
          </span>
          <span className="inline-flex items-center gap-1">
            <Wifi className="h-3.5 w-3.5 text-emerald-600" /> Live GPS updates
          </span>
          <span className="inline-flex items-center gap-1">
            <Smartphone className="h-3.5 w-3.5 text-emerald-600" /> Works on any phone
          </span>
          <Link to="/enquiry-numbers" className="inline-flex items-center gap-1 text-emerald-700 hover:underline font-semibold">
            <Phone className="h-3.5 w-3.5" /> Stand helpline numbers
          </Link>
        </div>
      </section>

      {/* ─────────────────────────────────────────────────────────────
          2. BUSES ON THE ROAD (REAL-TIME LIVE FLEET)
          Clean, simple cards showing running buses
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-10">
        <div className="flex items-center justify-between gap-3 mb-4">
          <div>
            <h2 className="font-display text-xl sm:text-2xl font-bold text-ink flex items-center gap-2">
              <span className="h-2.5 w-2.5 rounded-full bg-emerald-500 animate-pulse" />
              Live Buses on Road
            </h2>
            <p className="text-xs text-muted-foreground mt-0.5">
              Buses currently operating across Haryana Roadways corridors
            </p>
          </div>

          {liveBuses.length > 0 && (
            <span className="rounded-full bg-emerald-100 px-3 py-1 text-xs font-bold text-emerald-800">
              {liveBuses.length} Active Bus{liveBuses.length > 1 ? "es" : ""}
            </span>
          )}
        </div>

        {liveBusesQuery.loading && (
          <p className="text-xs text-muted-foreground py-4 text-center">Checking for live buses…</p>
        )}

        {!liveBusesQuery.loading && liveBuses.length === 0 && (
          <div className="rounded-2xl border border-border/80 bg-card p-5 text-center shadow-xs">
            <div className="mx-auto grid h-10 w-10 place-items-center rounded-xl bg-emerald-50 text-emerald-700 mb-2">
              <BusFront className="h-5 w-5" />
            </div>
            <h3 className="font-bold text-sm text-ink">No buses are on the road right now</h3>
            <p className="mt-1 text-xs text-muted-foreground max-w-md mx-auto">
              Scheduled services are running as per timetable. Search your boarding stand above to check upcoming departures and fares.
            </p>
          </div>
        )}

        {liveBuses.length > 0 && (
          <div className="grid gap-3 sm:grid-cols-2">
            {liveBuses.map((bus) => (
              <div
                key={`${bus.fleetNumber}-${bus.routeId}`}
                className="rounded-2xl border border-border/80 bg-card p-4 shadow-xs transition-all hover:border-emerald-500/50 hover:shadow-sm"
              >
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <span className="inline-block rounded-md bg-emerald-100/80 px-2 py-0.5 text-[10px] font-bold text-emerald-800 uppercase">
                      Route {bus.routeCode}
                    </span>
                    <h3 className="font-display text-lg font-bold text-ink mt-1">Bus {bus.fleetNumber}</h3>
                    <p className="text-xs text-muted-foreground truncate">{bus.routeName}</p>
                  </div>
                  <StatusBadge status={bus.status} />
                </div>

                <div className="mt-3 grid grid-cols-2 gap-2 text-xs border-t border-border/50 pt-2.5">
                  <div>
                    <span className="text-muted-foreground text-[10px] block">Current Stop</span>
                    <span className="font-semibold text-ink truncate block">{bus.currentStop ?? "Between stands"}</span>
                  </div>
                  <div>
                    <span className="text-muted-foreground text-[10px] block">Next Stop</span>
                    <span className="font-semibold text-ink truncate block">
                      {bus.nextStop ?? "—"}
                      {bus.etaMinutes != null ? ` (${formatEta(bus.etaMinutes)})` : ""}
                    </span>
                  </div>
                </div>

                <div className="mt-3 flex items-center justify-between border-t border-border/50 pt-2 text-xs">
                  <span className="text-muted-foreground">
                    Speed: <b>{bus.speedKmph != null ? `${bus.speedKmph} km/h` : "Halted"}</b>
                  </span>
                  <Button variant="outline" size="sm" asChild className="rounded-full text-xs h-7 px-3">
                    <Link to={`/track/${bus.fleetNumber}?routeId=${bus.routeId}`}>
                      Track on Map <ArrowRight className="h-3 w-3 ml-1" />
                    </Link>
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {/* ─────────────────────────────────────────────────────────────
          3. CLEAN HIGHWAY CORRIDOR MAP
          Visual road line + active buses
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-10 overflow-hidden rounded-3xl border border-border/80 bg-card shadow-xs">
        <TrackingNotice status={trackingStatus} />

        {/* Route Selector Header */}
        <div className="border-b border-border/70 bg-card px-4 py-3 sm:px-5 flex flex-wrap items-center justify-between gap-2">
          <div>
            <div className="flex items-center gap-1.5 text-xs font-bold text-emerald-800 uppercase">
              <RouteIcon className="h-4 w-4" />
              <span>Route Corridors</span>
            </div>
            <p className="font-display text-sm font-bold text-ink mt-0.5">
              {activeRoute ? `Route ${activeRoute.code}: ${activeRoute.name}` : "Haryana Highway Network"}
            </p>
          </div>

          {/* Corridor Selection Pills */}
          <div className="flex flex-wrap items-center gap-1.5">
            {routes.slice(0, 4).map((r) => {
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
                    "rounded-full px-3 py-1 text-xs font-semibold transition-all cursor-pointer",
                    isSelected
                      ? "bg-emerald-700 text-white shadow-2xs"
                      : "border border-border/80 bg-white text-muted-foreground hover:bg-muted hover:text-ink"
                  )}
                >
                  {r.origin} ➔ {r.destination}
                </button>
              );
            })}
          </div>
        </div>

        {/* Map View */}
        <div className="relative h-[320px] sm:h-[380px] w-full bg-muted/40">
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

        {/* Map Summary Footer */}
        {activeRoute && (
          <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border/70 bg-card px-4 py-2.5 text-xs text-muted-foreground sm:px-5">
            <div className="flex items-center gap-4">
              <span><b>From:</b> {activeRoute.origin}</span>
              <span><b>To:</b> {activeRoute.destination}</span>
              {activeRoute.distanceKm && <span><b>Distance:</b> {activeRoute.distanceKm} km</span>}
            </div>
            <Link
              to={`/track/${mapTrip?.fleetNumber ?? "SCHED"}?${mapTrackQuery || ""}`}
              className="text-emerald-700 font-semibold hover:underline inline-flex items-center gap-1"
            >
              Full Route Details <ArrowRight className="h-3 w-3" />
            </Link>
          </div>
        )}
      </section>

      {/* ─────────────────────────────────────────────────────────────
          4. SIMPLE 3-STEP GUIDE
          Clean, human, practical
          ───────────────────────────────────────────────────────────── */}
      <section className="mb-10 text-center">
        <h2 className="font-display text-xl font-bold text-ink">How to track in 3 easy steps</h2>
        <div className="mt-4 grid gap-3 sm:grid-cols-3 text-left">
          <div className="rounded-2xl border border-border/80 bg-card p-4 shadow-2xs">
            <span className="grid h-8 w-8 place-items-center rounded-lg bg-emerald-100 text-emerald-800 text-xs font-bold mb-2">1</span>
            <h3 className="font-bold text-sm text-ink">Select your bus stands</h3>
            <p className="mt-1 text-xs text-muted-foreground">Pick your departure and destination to see all direct and connecting buses.</p>
          </div>

          <div className="rounded-2xl border border-border/80 bg-card p-4 shadow-2xs">
            <span className="grid h-8 w-8 place-items-center rounded-lg bg-emerald-100 text-emerald-800 text-xs font-bold mb-2">2</span>
            <h3 className="font-bold text-sm text-ink">Check live bus location</h3>
            <p className="mt-1 text-xs text-muted-foreground">See the bus moving on the highway with live speed, delays, and next stop ETA.</p>
          </div>

          <div className="rounded-2xl border border-border/80 bg-card p-4 shadow-2xs">
            <span className="grid h-8 w-8 place-items-center rounded-lg bg-emerald-100 text-emerald-800 text-xs font-bold mb-2">3</span>
            <h3 className="font-bold text-sm text-ink">Reach the stand on time</h3>
            <p className="mt-1 text-xs text-muted-foreground">No more long, blind waits on dusty highway stands. Board calmly and travel safe.</p>
          </div>
        </div>
      </section>

      {/* ─────────────────────────────────────────────────────────────
          5. QUICK BUS STAND HELPLINES
          ───────────────────────────────────────────────────────────── */}
      <section className="rounded-2xl border border-emerald-200 bg-emerald-50/50 p-5 text-center sm:p-6">
        <h2 className="font-display text-lg font-bold text-emerald-950">Need bus stand enquiry numbers?</h2>
        <p className="mt-1 text-xs text-muted-foreground max-w-md mx-auto">
          Official contact numbers for Haryana Roadways counters at Fatehabad, Hisar, Sirsa, Delhi ISBT, Rohtak, and Chandigarh.
        </p>
        <div className="mt-3 flex justify-center">
          <Button variant="outline" size="sm" asChild className="rounded-full bg-white text-xs">
            <Link to="/enquiry-numbers">
              <Phone className="h-3.5 w-3.5 mr-1.5 text-emerald-700" /> View All Bus Stand Numbers
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
      className="relative flex min-h-[66px] min-w-0 items-center gap-2.5 rounded-2xl border border-border/80 bg-background/50 px-3 py-2 transition-colors focus-within:border-emerald-500 focus-within:ring-2 focus-within:ring-emerald-500/15"
    >
      <Icon className="h-5 w-5 shrink-0 text-emerald-700" />
      <div className="grid min-w-0 flex-1 gap-0.5">
        <label htmlFor={id} className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
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
          placeholder="Type city or bus stand..."
          aria-busy={loading}
          className="h-6 w-full border-0 bg-transparent p-0 text-xs font-bold text-ink outline-none placeholder:font-normal placeholder:text-muted-foreground"
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
          className="grid h-7 w-7 shrink-0 place-items-center rounded-full text-muted-foreground hover:bg-muted hover:text-ink cursor-pointer"
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
        <ChevronDown className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
      )}

      {open && (
        <div
          id={`${id}-suggestions`}
          role="listbox"
          className="absolute left-0 right-0 top-full z-50 mt-1 max-h-72 overflow-y-auto rounded-2xl border border-border/80 bg-card p-2 shadow-xl"
        >
          <h3 className="px-2 pb-1.5 text-[10px] font-bold uppercase tracking-wider text-muted-foreground">
            {loading
              ? "Loading stands…"
              : searchText
                ? `Results for “${inputValue.trim()}”`
                : "Popular Bus Stands"}
          </h3>
          {loading ? (
            <p className="p-3 text-xs text-muted-foreground">Loading bus stands…</p>
          ) : suggestions.length ? (
            suggestions.map((stop, index) => (
              <button
                key={stop.id}
                id={`${id}-option-${stop.id}`}
                type="button"
                role="option"
                aria-selected={stop.name === value}
                className={cn(
                  "flex w-full items-center justify-between gap-2.5 rounded-xl px-2.5 py-2 text-left text-xs transition-colors hover:bg-emerald-50 cursor-pointer",
                  index === activeIndex ? "bg-emerald-50 text-emerald-900 font-semibold" : ""
                )}
                onMouseEnter={() => setActiveIndex(index)}
                onClick={() => selectStop(stop)}
              >
                <span className="flex min-w-0 items-start gap-2">
                  <MapPin className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-700" />
                  <span className="min-w-0">
                    <span className="block font-medium text-ink">{stop.name}</span>
                    <span className="block truncate text-[10px] text-muted-foreground">
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
            <div className="p-3 text-center">
              <p className="text-xs font-medium text-ink">No bus stands found</p>
              <p className="mt-0.5 text-[10px] text-muted-foreground">Try another city or spelling.</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
