import { useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { ArrowRight, BusFront, Compass, MapPin, Navigation, Route as RouteIcon, Search } from "lucide-react";
import { MapView, type BusPin } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { TrackingNotice, computeTrackingStatus } from "@/components/TrackingNotice";
import { Button } from "@/components/ui/button";
import { resolveUrl, useQuery } from "@/lib/api";
import { formatEta, getIndiaDate } from "@/lib/format";
import type { PublicRoute, TrackSnapshot } from "@/lib/types";
import { cn } from "@/lib/utils";

export function LiveRoutesPage() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const routesQuery = useQuery<PublicRoute[]>("/api/routes");
  const routes = routesQuery.data ?? [];
  const liveBusesQuery = useQuery<TrackSnapshot[]>("/api/live");
  const [liveBuses, setLiveBuses] = useState<TrackSnapshot[]>([]);

  useEffect(() => {
    if (liveBusesQuery.data) setLiveBuses(liveBusesQuery.data);
  }, [liveBusesQuery.data]);

  useEffect(() => {
    let active = true;
    const conn = new HubConnectionBuilder().withUrl(resolveUrl("/hubs/tracking")).withAutomaticReconnect().build();
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
    conn.start()
      .then(() => {
        if (!active) return conn.stop();
        return conn.invoke("JoinLive");
      })
      .catch(() => undefined);
    return () => {
      active = false;
      if (conn.state === "Connected") void conn.stop();
    };
  }, []);

  const routeIdParam = params.get("routeId");
  const selectedRouteId = routeIdParam ? Number(routeIdParam) : null;

  const activeRoute = routes.find((r) => r.id === selectedRouteId)
    ?? routes.find((r) => r.liveCount > 0)
    ?? routes[0]
    ?? null;
  const activeRouteId = activeRoute?.id ?? null;

  const routeSnapshotQuery = useQuery<TrackSnapshot>(
    activeRouteId ? `/api/routes/${activeRouteId}/snapshot` : null,
  );

  const routeSnapshot = routeSnapshotQuery.data;
  const runningBusesOnRoute = liveBuses.filter((b) => b.routeId === activeRouteId);

  const mapPath = (routeSnapshot?.path && routeSnapshot.path.length > 0)
    ? routeSnapshot.path
    : (activeRoute?.path && activeRoute.path.length > 0 ? activeRoute.path : []);

  const mapStops = (routeSnapshot?.stops && routeSnapshot.stops.length > 0)
    ? routeSnapshot.stops
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
      })) ?? []);

  const mapBuses: BusPin[] = (runningBusesOnRoute.length > 0 ? runningBusesOnRoute : liveBuses)
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

  if (mapBuses.length === 0 && routeSnapshot?.recordedAt != null && routeSnapshot.latitude != null && routeSnapshot.longitude != null) {
    mapBuses.push({
      latitude: routeSnapshot.latitude,
      longitude: routeSnapshot.longitude,
      heading: routeSnapshot.heading,
      label: routeSnapshot.fleetNumber,
      fleetNumber: routeSnapshot.fleetNumber,
      routeName: routeSnapshot.routeName,
      routeCode: routeSnapshot.routeCode,
      speedKmph: routeSnapshot.speedKmph,
      status: routeSnapshot.status,
      variant: routeSnapshot.status === "Running" ? "live" : "last-known",
      recordedAt: routeSnapshot.recordedAt,
    });
  }

  const trackingStatus = computeTrackingStatus({
    isOnline: true,
    tripStatus: routeSnapshot?.status ?? (runningBusesOnRoute.length > 0 ? "Running" : "Scheduled"),
    recordedAt: routeSnapshot?.recordedAt,
    hasCoordinates: routeSnapshot?.latitude != null && routeSnapshot?.longitude != null,
  });

  return (
    <div className="mx-auto max-w-6xl px-4 py-8">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <span className="inline-flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-pine">
            <span className="pulse" /> Haryana Roadways Live Network
          </span>
          <h1 className="mt-1 font-display text-3xl sm:text-4xl">Live Bus Routes & Corridors</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Explore live bus positions, route paths along highways, and station schedules.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span className="flex items-center gap-2 rounded-full border bg-card px-3.5 py-1.5 text-xs font-medium text-pine">
            <span className="pulse" /> {liveBuses.length} Active Bus{liveBuses.length === 1 ? "" : "es"} on Road
          </span>
        </div>
      </div>

      {/* Marquee Notice */}
      <div className="mt-6">
        <TrackingNotice status={trackingStatus} />
      </div>

      <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[360px_1fr]">
        {/* Left Column: Routes List */}
        <div className="space-y-4">
          <h2 className="text-sm font-bold uppercase tracking-wider text-muted-foreground flex items-center gap-2">
            <RouteIcon size={16} className="text-pine" /> Available Routes ({routes.length})
          </h2>
          {routesQuery.loading && <p className="text-sm text-muted-foreground">Loading route corridors…</p>}
          <div className="space-y-2.5">
            {routes.map((r) => {
              const isSelected = r.id === activeRoute?.id;
              return (
                <div
                  key={r.id}
                  onClick={() => setParams({ routeId: String(r.id) })}
                  className={cn(
                    "cursor-pointer rounded-2xl border p-4 transition-all",
                    isSelected
                      ? "border-pine bg-card shadow-sm ring-2 ring-pine/20"
                      : "border-border/70 bg-card hover:border-primary/40 hover:bg-muted/30"
                  )}
                >
                  <div className="flex items-start justify-between gap-2">
                    <div>
                      <span className="rounded-md bg-pine/10 px-2 py-0.5 text-xs font-bold text-pine">
                        Route {r.code}
                      </span>
                      <h3 className="mt-1.5 font-display text-lg text-ink font-semibold">
                        {r.origin} → {r.destination}
                      </h3>
                    </div>
                    {r.liveCount > 0 ? (
                      <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-100 dark:bg-emerald-950/60 px-2.5 py-1 text-xs font-bold text-emerald-800 dark:text-emerald-300">
                        <span className="h-2 w-2 rounded-full bg-emerald-500 animate-pulse" />
                        {r.liveCount} LIVE
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1 rounded-full border border-border/80 px-2 py-0.5 text-[11px] font-medium text-muted-foreground">
                        Scheduled
                      </span>
                    )}
                  </div>

                  <p className="mt-2 text-xs text-muted-foreground line-clamp-2">
                    Via: {r.stops.map((s) => s.name).join(" · ")}
                  </p>

                  <div className="mt-3 flex items-center justify-between border-t border-border/60 pt-2.5 text-xs text-muted-foreground">
                    <span>{r.stops.length} stations · {r.distanceKm ? `${r.distanceKm} km` : ""}</span>
                    <span className="font-semibold text-pine flex items-center gap-1">
                      {isSelected ? "Viewing map" : "View route"} <ArrowRight size={13} />
                    </span>
                  </div>
                </div>
              );
            })}
          </div>
        </div>

        {/* Right Column: Live Map & Selected Route Details */}
        <div className="flex flex-col overflow-hidden rounded-2xl border border-border/70 bg-card shadow-sm">
          <div className="flex min-h-[76px] flex-wrap items-center justify-between gap-3 border-b border-border/70 bg-card px-5 py-3">
            <div>
              <div className="flex items-center gap-2">
                <span className="text-[11px] font-bold uppercase tracking-wider text-muted-foreground flex items-center gap-1">
                  <Navigation size={12} /> Live Highway Map
                </span>
                {activeRoute && (
                  <span className="rounded-full bg-pine/10 px-2 py-0.5 text-[10px] font-bold uppercase text-pine">
                    Route {activeRoute.code}
                  </span>
                )}
              </div>
              <h2 className="mt-0.5 font-display text-xl text-ink">
                {activeRoute ? activeRoute.name : "Select a route"}
              </h2>
            </div>
            {activeRoute && (
              <Button size="sm" asChild>
                <Link to={`/search?from=${encodeURIComponent(activeRoute.origin)}&to=${encodeURIComponent(activeRoute.destination)}&date=${getIndiaDate()}`}>
                  Search buses <ArrowRight size={14} className="ml-1" />
                </Link>
              </Button>
            )}
          </div>

          <div className="relative min-h-[460px] flex-1 bg-muted/40">
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

          {activeRoute && (
            <div className="border-t border-border/70 bg-card p-4 sm:p-5 space-y-3">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap items-center gap-4 text-xs">
                  <span><b>Origin:</b> {activeRoute.origin} Bus Stand</span>
                  <span><b>Destination:</b> {activeRoute.destination}</span>
                  {activeRoute.distanceKm && <span><b>Distance:</b> {activeRoute.distanceKm} km</span>}
                </div>
                <span className="text-xs font-semibold text-pine">
                  {mapBuses.length > 0 ? `${mapBuses.length} bus(es) active` : "Timetable active"}
                </span>
              </div>
              <div className="flex flex-wrap items-center gap-2 pt-1 border-t border-border/50">
                <span className="text-xs font-medium text-muted-foreground">Route stations:</span>
                {activeRoute.stops.map((stop, idx) => (
                  <span key={stop.stopId} className="inline-flex items-center text-xs font-medium text-foreground">
                    {idx > 0 && <span className="mx-1.5 text-muted-foreground/60">→</span>}
                    {stop.name}
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
