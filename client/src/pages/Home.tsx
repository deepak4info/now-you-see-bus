import { useEffect, useRef, useState, type FormEvent } from "react";
import { ArrowLeftRight, ArrowRight, Bot, BusFront, CalendarDays, ChevronDown, CircleCheck, MapPin, Navigation, Phone, Route as RouteIcon, Search, Sparkles, X, type LucideIcon } from "lucide-react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { MapView, type BusPin } from "@/components/MapView";
import { BusTrackingSection } from "@/components/BusTrackingSection";
import { StatusBadge } from "@/components/StatusBadge";
import { TrackingNotice, computeTrackingStatus } from "@/components/TrackingNotice";
import { Button } from "@/components/ui/button";
import { resolveUrl, useQuery } from "@/lib/api";
import { formatDateWithWeekday, formatEta, getIndiaDate, isDateOnly } from "@/lib/format";
import { useI18n } from "@/lib/i18n";
import { useBusLive } from "@/lib/live";
import { getRecentSearch, saveRecentSearch, type RecentSearch } from "@/lib/recentSearch";
import type { PublicRoute, SearchResponse, StopOption, TrackSnapshot } from "@/lib/types";
import { cn } from "@/lib/utils";

const EMPTY_PATH: TrackSnapshot["path"] = [];
const EMPTY_STOPS: TrackSnapshot["stops"] = [];

export function HomePage() {
  const { t } = useI18n();
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
    (recentBusSeed?.serviceDate ?? travelDate) === getIndiaDate(),
  );
  const routesQuery = useQuery<PublicRoute[]>("/api/routes");
  const routes = routesQuery.data ?? [];
  const [selectedRouteId, setSelectedRouteId] = useState<number | null>(() => {
    const p = homeParams.get("routeId");
    return p ? Number(p) : null;
  });

  const liveBusesQuery = useQuery<TrackSnapshot[]>("/api/live");
  const [liveBuses, setLiveBuses] = useState<TrackSnapshot[]>([]);
  const [latestLiveUpdate, setLatestLiveUpdate] = useState<TrackSnapshot | null>(null);
  const [liveUpdatesError, setLiveUpdatesError] = useState("");
  const [from, setFrom] = useState(() => homeParams.get("from") ?? recentSearch.from);
  const [to, setTo] = useState(() => homeParams.get("to") ?? recentSearch.to);
  const [error, setError] = useState("");
  const [stopFieldsVersion, setStopFieldsVersion] = useState(0);
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
    const conn = new HubConnectionBuilder().withUrl(resolveUrl("/hubs/tracking")).withAutomaticReconnect().build();
    conn.on("location", (snapshot: TrackSnapshot) => {
      if (active) setLatestLiveUpdate(snapshot);
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
      if (conn.state === "Connected") {
        void conn.stop().catch((err: unknown) => console.error("Could not stop live bus updates.", err));
      }
    };
  }, []);

  function searchFor(origin: string, destination: string, date: string) {
    saveRecentSearch(origin, destination);
    const nextSearch = { from: origin, to: destination };
    setRecentSearch(nextSearch);
    const params = new URLSearchParams({ from: origin, to: destination, date });
    navigate(`/search?${params.toString()}`);
  }

  function search(event: FormEvent) {
    event.preventDefault();
    const availableStops = stops.data ?? [];
    if (!availableStops.some((stop) => stop.name === from) || !availableStops.some((stop) => stop.name === to)) {
      setError("Choose a boarding stop and destination from the results.");
      return;
    }
    if (from === to) {
      setError("Pick two different stops.");
      return;
    }
    searchFor(from, to, travelDate);
  }

  const recentTrackParams = recentBusSeed
    ? new URLSearchParams({
      from: recentSearch.from,
      to: recentSearch.to,
      routeId: String(recentBusSeed.routeId),
      date: travelDate,
    })
    : null;

  const liveForRoute = activeRouteId != null
    ? (live?.routeId === activeRouteId ? live : null)
      ?? liveBuses.find((b) => b.routeId === activeRouteId && b.status === "Running" && b.latitude != null)
    : (live ?? liveBuses.find((b) => b.status === "Running" && b.latitude != null));

  const mapTrip = liveForRoute
    ?? (recentBusSeed?.routeId === activeRouteId ? recentBusSeed : null)
    ?? routeSnapshotQuery.data
    ?? recentBusSeed
    ?? liveBuses[0]
    ?? null;

  const mapPath = (mapTrip?.path && mapTrip.path.length > 0)
    ? mapTrip.path
    : (activeRoute?.path && activeRoute.path.length > 0 ? activeRoute.path : EMPTY_PATH);

  const mapStops = (mapTrip?.stops && mapTrip.stops.length > 0)
    ? mapTrip.stops
    : (activeRoute?.stops?.map((s, idx) => ({
        stopId: s.stopId,
        name: s.name,
        code: s.code,
        sequence: s.sequenceNo,
        latitude: s.latitude,
        longitude: s.longitude,
        distanceKm: s.distanceKm,
        scheduledTime: "08:00",
        status: idx === 0 ? "Origin" : idx === activeRoute.stops.length - 1 ? "Destination" : "Scheduled",
        etaMinutes: null,
      })) ?? EMPTY_STOPS);

  const mapBuses: BusPin[] = liveBuses
    .filter((b) => b.latitude != null && b.longitude != null)
    .map((b) => ({
      latitude: b.latitude!,
      longitude: b.longitude!,
      heading: b.heading,
      label: b.fleetNumber,
      fleetNumber: b.fleetNumber,
      routeName: b.routeName,
      routeCode: b.routeCode,
      speedKmph: b.speedKmph,
      status: b.status,
      variant: "live" as const,
      recordedAt: b.recordedAt,
    }));

  if (mapBuses.length === 0 && mapTrip?.recordedAt != null && mapTrip.latitude != null && mapTrip.longitude != null) {
    mapBuses.push({
      latitude: mapTrip.latitude,
      longitude: mapTrip.longitude,
      heading: mapTrip.heading,
      label: mapTrip.fleetNumber,
      fleetNumber: mapTrip.fleetNumber,
      routeName: mapTrip.routeName,
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
    <div className="mx-auto grid max-w-6xl grid-cols-1 gap-y-7 px-4 pb-12 pt-7 md:gap-y-9 md:pb-16 md:pt-9 lg:grid-cols-2 lg:gap-x-8">
      <div className="contents">
        <section className="order-1 py-1 lg:col-start-1 lg:row-start-1 lg:self-center">
          <span className="inline-flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-primary">
            <span className="pulse" /> {t("searchHeroKicker")}
          </span>
          <h1 className="mt-2 font-display text-4xl leading-tight tracking-tight text-ink sm:text-5xl">
            Find your bus. <span className="text-primary">Follow the route.</span>
          </h1>
          <p className="mt-2 max-w-2xl text-sm leading-6 text-muted-foreground sm:text-base">
            Live bus locations and route information across Haryana, all in one place.
          </p>
          <button
            type="button"
            onClick={() => document.getElementById("journey-search")?.scrollIntoView({ behavior: "smooth", block: "center" })}
            className="mt-3 inline-flex items-center gap-2 text-sm font-semibold text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            Plan a journey <ArrowRight aria-hidden="true" className="h-4 w-4" />
          </button>
          <Link
            to="/enquiry-numbers"
            className="ml-4 inline-flex items-center gap-2 text-sm font-semibold text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Phone aria-hidden="true" className="h-4 w-4" /> Bus stand numbers
          </Link>
          <Link
            to="/assistant"
            className="ml-4 inline-flex items-center gap-1.5 rounded-full bg-pine/10 hover:bg-pine/20 px-3 py-1 text-xs font-semibold text-pine border border-pine/20 transition-all hover:scale-105"
          >
            <Bot className="h-3.5 w-3.5" />
            AI Travel Assistant ✨
          </Link>
        </section>

        <section className="order-3 relative z-0 flex min-h-[420px] flex-col overflow-hidden rounded-2xl border border-border/70 bg-card shadow-sm sm:min-h-[520px] lg:col-span-2 lg:row-start-2">
          {/* 1. Live Network Corridors Selector */}
          {routes.length > 0 && (
            <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border/70 bg-muted/30 px-4 py-2 sm:px-5">
              <div className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-wider text-pine">
                <RouteIcon size={14} aria-hidden="true" /> Live Network Corridors
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
                        "inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs transition-all whitespace-nowrap cursor-pointer",
                        isSelected
                          ? "bg-pine text-white shadow-xs font-semibold ring-2 ring-pine/30"
                          : "border border-border/80 bg-card text-muted-foreground hover:bg-muted hover:text-ink font-medium"
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
                          isSelected ? "bg-white/20 text-white" : "bg-emerald-100 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300"
                        )}>
                          {r.liveCount} live
                        </span>
                      )}
                    </button>
                  );
                })}
              </div>
            </div>
          )}

          {/* 2. Scrolling Marquee Notice (when live tracking unavailable/scheduled) */}
          <TrackingNotice status={trackingStatus} />

          {/* 3. Live Map Header */}
          <div className="flex min-h-[76px] flex-wrap items-center justify-between gap-3 border-b border-border/70 bg-card px-4 py-3 sm:px-5">
            <div>
              <div className="flex items-center gap-2">
                <p className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
                  <Navigation size={13} aria-hidden="true" /> Live Road Map
                </p>
                {activeRoute && (
                  <span className="rounded-full bg-pine/10 px-2 py-0.5 text-[10px] font-bold uppercase text-pine">
                    Route {activeRoute.code} · {activeRoute.distanceKm ? `${activeRoute.distanceKm} km` : ""}
                  </span>
                )}
              </div>
              <p className="mt-1 truncate font-display text-lg sm:text-xl">
                {mapTrip?.fleetNumber && mapTrip.fleetNumber !== "SCHED"
                  ? `Bus ${mapTrip.fleetNumber} · ${mapTrip.routeName || activeRoute?.name}`
                  : (activeRoute ? activeRoute.name : "Explore bus routes")}
              </p>
              <p className="mt-0.5 text-xs text-muted-foreground">
                Route: {mapStops.map((s) => s.name).join(" → ")}
                {mapTrip?.nextStop ? ` · Next: ${mapTrip.nextStop}` : ""}
                {mapTrip?.etaMinutes != null ? ` · ${formatEta(mapTrip.etaMinutes)}` : ""}
              </p>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {mapTrip && <StatusBadge status={mapTrip.status} />}
              {mapTrip?.fleetNumber && mapTrip.fleetNumber !== "SCHED" ? (
                <Button variant="outline" size="sm" asChild>
                  <Link to={`/track/${mapTrip.fleetNumber}?${mapTrackQuery || ""}`}>
                    Track bus <ArrowRight aria-hidden="true" className="h-4 w-4" />
                  </Link>
                </Button>
              ) : (
                <Button variant="outline" size="sm" onClick={() => searchFor(activeRoute?.origin || from, activeRoute?.destination || to, travelDate)}>
                  View schedules <ArrowRight aria-hidden="true" className="h-4 w-4" />
                </Button>
              )}
            </div>
          </div>

          {/* 4. Map Container */}
          <div className="relative min-h-[336px] flex-1 bg-muted/40 sm:min-h-[436px]">
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
            {mapPath.length === 0 && mapStops.length === 0 && !mapTrip && (
              <div className="pointer-events-none absolute left-4 top-4 max-w-xs rounded-xl border border-border/70 bg-card/95 px-4 py-3 shadow-sm backdrop-blur">
                <p className="text-sm font-semibold text-ink">{recentSearchQuery.loading ? "Finding your route…" : "Loading route…"}</p>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">
                  Showing Haryana Roadways live corridor map.
                </p>
              </div>
            )}
            {recentSearchQuery.error && mapTrip && (
              <p className="absolute left-4 top-4 rounded-lg bg-card/95 px-3 py-2 text-sm text-destructive shadow-sm">
                {recentSearchQuery.error}
              </p>
            )}
          </div>

          {/* 5. Route Summary Footer */}
          {activeRoute && (
            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border/70 bg-card/80 px-4 py-3 text-xs text-muted-foreground sm:px-5">
              <div className="flex flex-wrap items-center gap-3 sm:gap-5">
                <span><b>Starting:</b> {activeRoute.origin} Bus Stand</span>
                <span><b>Destination:</b> {activeRoute.destination}</span>
                {activeRoute.distanceKm && <span><b>Distance:</b> {activeRoute.distanceKm} km</span>}
              </div>
              <div className="flex items-center gap-2">
                <span className="flex h-2 w-2 rounded-full bg-emerald-500" />
                <span className="font-medium text-foreground">
                  {mapBuses.length > 0 ? `${mapBuses.length} bus(es) active` : "Scheduled route active"}
                </span>
              </div>
            </div>
          )}
        </section>
      </div>

      <section id="journey-search" className="order-2 rounded-2xl border border-border/70 bg-card p-4 shadow-sm sm:p-5 lg:col-start-2 lg:row-start-1" aria-labelledby="journey-search-heading">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <p className="text-xs font-semibold uppercase tracking-[0.16em] text-primary">Find a service</p>
            <h2 id="journey-search-heading" className="mt-1 font-display text-2xl text-ink">Plan your journey</h2>
          </div>
          <span className="hidden items-center gap-2 pb-1 text-xs text-muted-foreground sm:flex">
            <MapPin aria-hidden="true" className="h-4 w-4 text-primary" /> Search stops across Haryana
          </span>
        </div>
        <form onSubmit={search} className="mt-4 grid gap-3">
          <div className="relative grid w-full grid-cols-1 gap-2 sm:grid-cols-2 sm:gap-x-10">
            <StopField key={`from-${stopFieldsVersion}`} label={t("searchFromLabel")} value={from} stops={stops.data ?? []} loading={stops.loading} onChange={setFrom} icon={BusFront} />
            <button
              type="button"
              aria-label="Swap boarding stop and destination"
              className="mx-auto grid h-9 w-9 place-items-center rounded-full border border-border bg-card text-muted-foreground transition-colors hover:border-primary/40 hover:bg-primary/5 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring sm:absolute sm:left-1/2 sm:top-[38px] sm:z-10 sm:-translate-x-1/2"
              onClick={() => {
                setFrom(to);
                setTo(from);
                setStopFieldsVersion((version) => version + 1);
                setError("");
              }}
            >
              <ArrowLeftRight aria-hidden="true" className="h-4 w-4" />
            </button>
            <StopField key={`to-${stopFieldsVersion}`} label={t("searchToLabel")} value={to} stops={stops.data ?? []} loading={stops.loading} onChange={setTo} icon={MapPin} />
            <label htmlFor="travel-date" className="flex min-h-[76px] min-w-0 items-center gap-3 rounded-xl border border-border/80 bg-background/50 px-3 py-3 transition-colors focus-within:border-primary/50 focus-within:ring-2 focus-within:ring-primary/15 sm:px-4">
              <CalendarDays aria-hidden="true" className="h-6 w-6 shrink-0 text-ink" />
              <span className="grid min-w-0 flex-1 gap-1">
                <span className="text-xs font-medium text-muted-foreground">{t("searchDateLabel")}</span>
                <input
                  id="travel-date"
                  type="date"
                  required
                  aria-label="Date of journey"
                  value={travelDate}
                  onChange={(event) => setTravelDate(event.target.value)}
                  className="h-8 w-full min-w-0 border-0 bg-transparent p-0 text-sm font-semibold text-ink outline-none focus-visible:ring-2 focus-visible:ring-ring"
                />
                <span className="truncate text-xs text-muted-foreground">
                  {travelDate === getIndiaDate() ? `${formatDateWithWeekday(travelDate)} · Today` : formatDateWithWeekday(travelDate)}
                </span>
              </span>
            </label>
            <Button type="submit" size="lg" className="h-full min-h-[64px] w-full rounded-xl text-base shadow-md shadow-primary/10 transition-colors sm:min-h-[76px]">
              <Search aria-hidden="true" className="h-5 w-5" />
              {t("findBusesBtn")}
            </Button>
          </div>
          {stops.error && <p className="text-sm text-destructive">{stops.error}</p>}
          {error && <p className="text-sm text-destructive">{error}</p>}
          <div className="flex flex-wrap items-center justify-end gap-2 border-t border-border/60 pt-3">
            <p className="mr-auto text-xs text-muted-foreground">Recent route:</p>
            <button
              type="button"
              onClick={() => searchFor(recentSearch.from, recentSearch.to, travelDate)}
              className="inline-flex items-center gap-2 rounded-full bg-primary/5 px-3 py-1.5 text-sm font-medium text-primary transition-colors hover:bg-primary/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {recentSearch.from} <ArrowRight aria-hidden="true" className="h-3.5 w-3.5" /> {recentSearch.to}
            </button>
          </div>
        </form>
      </section>

      <BusTrackingSection
        liveUpdate={latestLiveUpdate}
        liveUpdatesError={liveUpdatesError}
        onFindBusClick={() => document.getElementById("journey-search")?.scrollIntoView({ behavior: "smooth" })}
      />
      <section className="order-5 lg:col-span-2 lg:row-start-4" aria-labelledby="how-it-works">
        <div className="mb-5">
          <p className="text-xs font-semibold uppercase tracking-[0.18em] text-pine">Simple journey planning</p>
          <h2 id="how-it-works" className="mt-1 font-display text-3xl sm:text-4xl">How it works</h2>
        </div>
        <div className="grid gap-3 md:grid-cols-3">
          <Step n="01" icon={MapPin} title="Choose your stops" body="Pick where you’re boarding and where you’re going to see matching bus services." />
          <Step n="02" icon={Navigation} title="Follow your bus" body="Check the route, upcoming stops, and live location when the service is running." />
          <Step n="03" icon={CircleCheck} title="Travel with confidence" body="Compare departure times and fares before you set off." />
        </div>
      </section>
    </div>
  );
}

function StopField({ label, value, stops, loading, onChange, icon: Icon }: {
  label: string;
  value: string;
  stops: StopOption[];
  loading: boolean;
  onChange: (value: string) => void;
  icon: LucideIcon;
}) {
  const id = label === "From" ? "boarding-stop" : "destination-stop";
  const fieldRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [inputValue, setInputValue] = useState(value);
  const [activeIndex, setActiveIndex] = useState(0);
  const searchText = inputValue.trim().toLocaleLowerCase();
  const popularNames = ["Hisar", "Fatehabad", "Bhuna", "Barwala", "Tohana"];
  const popularNameSet = new Set(popularNames.map((name) => name.toLocaleLowerCase()));
  const popularStops = popularNames
    .map((name) => stops.find((stop) => stop.name.toLocaleLowerCase() === name.toLocaleLowerCase()))
    .filter((stop): stop is StopOption => stop !== undefined);
  const suggestions = searchText
    ? stops.filter((stop) =>
      [stop.name, stop.code, stop.city, stop.address]
        .some((field) => field?.toLocaleLowerCase().includes(searchText)),
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
    <div ref={fieldRef} className="relative flex min-h-[76px] min-w-0 items-center gap-3 rounded-xl border bg-card px-3 py-3 sm:px-4">
      <Icon aria-hidden="true" className="h-6 w-6 shrink-0 text-ink" />
      <div className="grid min-w-0 flex-1 gap-1">
        <label htmlFor={id} className="text-xs font-medium text-muted-foreground">{label}</label>
        <input
          id={id}
          type="text"
          role="combobox"
          autoComplete="off"
          aria-autocomplete="list"
          aria-expanded={open}
          aria-controls={`${id}-suggestions`}
          aria-activedescendant={open && suggestions[activeIndex] ? `${id}-option-${suggestions[activeIndex].id}` : undefined}
          placeholder="Search city, bus stand..."
          aria-busy={loading}
          className="h-8 w-full min-w-0 border-0 bg-transparent p-0 text-sm font-semibold text-ink outline-none placeholder:font-normal placeholder:text-muted-foreground focus-visible:ring-2 focus-visible:ring-ring"
          value={inputValue}
          onFocus={() => { setOpen(true); setActiveIndex(0); }}
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
          aria-label={`Clear ${label.toLocaleLowerCase()} stop search`}
          className="grid h-8 w-8 shrink-0 place-items-center rounded-full text-muted-foreground hover:bg-muted hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          onClick={() => {
            setInputValue("");
            onChange("");
            setActiveIndex(0);
            setOpen(true);
          }}
        >
          <X aria-hidden="true" className="h-4 w-4" />
        </button>
      ) : (
        <ChevronDown aria-hidden="true" className="h-4 w-4 shrink-0 text-muted-foreground" />
      )}
      {open && (
        <div
          id={`${id}-suggestions`}
          role="listbox"
          aria-label={`${label} stop suggestions`}
          className="absolute left-0 right-0 top-full z-50 max-h-80 overflow-y-auto rounded-xl border bg-card p-3 shadow-xl sm:p-4"
        >
          <h3 className="px-2 pb-2 text-sm font-semibold text-ink">
            {loading
              ? "Loading stops…"
              : searchText
                ? `Search results for “${inputValue.trim()}”`
                : "Popular destinations"}
          </h3>
          {loading ? (
            <p className="rounded-lg border p-4 text-sm text-muted-foreground">Loading cities and stops…</p>
          ) : suggestions.length ? suggestions.map((stop, index) => (
            <button
              key={stop.id}
              id={`${id}-option-${stop.id}`}
              type="button"
              role="option"
              aria-selected={stop.name === value}
              className={`flex w-full items-center justify-between gap-3 border-t px-2 py-3 text-left text-sm transition-colors hover:bg-muted ${index === activeIndex ? "bg-muted" : ""}`}
              onMouseEnter={() => setActiveIndex(index)}
              onClick={() => selectStop(stop)}
            >
              <span className="flex min-w-0 items-start gap-3">
                <MapPin aria-hidden="true" className="mt-0.5 h-4 w-4 shrink-0 text-pine" />
                <span className="min-w-0">
                  <span className="block font-medium text-ink">{stop.name}</span>
                  <span className="mt-0.5 block truncate text-xs text-muted-foreground">
                    {!searchText && popularNameSet.has(stop.name.toLocaleLowerCase())
                      ? "Popular destination"
                      : [stop.city, stop.address, stop.code].filter(Boolean).join(" · ")}
                  </span>
                </span>
              </span>
              <span aria-hidden="true" className="shrink-0 text-lg text-muted-foreground">→</span>
            </button>
          )) : (
            <div className="rounded-lg border-t px-3 py-6 text-center">
              <MapPin aria-hidden="true" className="mx-auto h-5 w-5 text-muted-foreground" />
              <p className="mt-2 text-sm font-medium text-ink">No results found.</p>
              <p className="mt-1 text-xs text-muted-foreground">Try another city, stop, or stop code.</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function Step({ n, icon: Icon, title, body }: { n: string; icon: LucideIcon; title: string; body: string }) {
  return (
    <article className="group rounded-2xl border border-border/70 bg-card p-5 transition-all hover:-translate-y-1 hover:border-primary/30 hover:shadow-lg sm:p-6">
      <div className="flex items-center justify-between">
        <span className="grid h-11 w-11 place-items-center rounded-xl bg-primary/10 text-primary transition-colors group-hover:bg-primary group-hover:text-primary-foreground">
          <Icon size={20} aria-hidden="true" />
        </span>
        <span className="font-display text-sm tracking-widest text-muted-foreground/70">{n}</span>
      </div>
      <h3 className="mt-5 font-display text-xl">{title}</h3>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">{body}</p>
    </article>
  );
}
