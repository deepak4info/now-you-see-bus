import { useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { LiveJourneyPanel } from "@/components/LiveJourneyPanel";
import { MapView, type BusPin } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { StopTimeline } from "@/components/StopTimeline";
import {
  ScheduledRouteCard,
  TrackingNotice,
  computeTrackingStatus,
  getRouteFromCache,
  saveRouteToCache,
  useNetworkStatus,
} from "@/components/TrackingNotice";
import { Button } from "@/components/ui/button";
import { useQuery } from "@/lib/api";
import { ago, formatClock, formatEta, getIndiaDate, isDateOnly, rupee } from "@/lib/format";
import { useBusLive } from "@/lib/live";
import type { DelayAlertEvent, SearchResponse, TrackSnapshot } from "@/lib/types";

export function TrackPage() {
  const { fleet = "" } = useParams();
  const [params] = useSearchParams();
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const routeId = params.get("routeId") ?? "";
  const tripId = params.get("tripId") ?? "";
  const dateParam = params.get("date");
  const date = isDateOnly(dateParam) ? dateParam : getIndiaDate();
  const qs = new URLSearchParams();
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  if (routeId) qs.set("routeId", routeId);
  if (tripId) qs.set("tripId", tripId);
  qs.set("date", date);
  const hasJourney = !!from && !!to;
  const searchPath = hasJourney
    ? `/api/search?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&date=${date}`
    : null;
  const searchQuery = useQuery<SearchResponse>(searchPath);
  const parsedRouteId = Number(routeId);
  const requestedRouteId = Number.isInteger(parsedRouteId) && parsedRouteId > 0 ? parsedRouteId : null;
  const searchSnapshot = searchQuery.data?.results.find((result) =>
    result.fleetNumber.toLocaleLowerCase() === fleet.toLocaleLowerCase()
      && (requestedRouteId == null || result.routeId === requestedRouteId),
  ) ?? null;
  const trackQuery = useQuery<TrackSnapshot>(
    fleet && (!hasJourney || (searchQuery.data != null && searchSnapshot == null)) ? `/api/track/${fleet}?${qs.toString()}` : null,
  );
  const query = hasJourney && (searchSnapshot != null || !trackQuery.data) ? searchQuery : trackQuery;
  const seed = searchSnapshot ?? trackQuery.data;
  const [delayAlert, setDelayAlert] = useState<DelayAlertEvent | null>(null);
  const live = useBusLive(
    fleet,
    seed,
    requestedRouteId ?? seed?.routeId,
    !tripId && date === getIndiaDate() && seed?.status !== "Completed" && seed?.status !== "Cancelled",
    (alert) => setDelayAlert(alert),
  );

  // Network connectivity & offline cached route recovery
  const isOnline = useNetworkStatus();
  const [cachedSnapshot, setCachedSnapshot] = useState<TrackSnapshot | null>(() => getRouteFromCache(fleet));

  // Ticker to periodically recompute GPS freshness (e.g. signal-lost after 2min, stale after 10min)
  const [, setTick] = useState(0);
  useEffect(() => {
    const timer = window.setInterval(() => setTick((t) => t + 1), 10000);
    return () => window.clearInterval(timer);
  }, []);

  // Cache latest valid route snapshot for offline retrieval
  useEffect(() => {
    if (live && live.stops && live.stops.length > 0) {
      saveRouteToCache(fleet, live);
      setCachedSnapshot(live);
    }
  }, [fleet, live]);

  const effectiveLive = live ?? cachedSnapshot;

  if (query.loading && !effectiveLive) {
    return <p className="px-4 py-10 text-muted-foreground">Opening the live map…</p>;
  }

  if (!effectiveLive) {
    return (
      <div className="px-4 py-10">
        <p>{query.error ?? "No live trip for this bus."}</p>
        <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
      </div>
    );
  }

  const findStop = (name: string) => effectiveLive.stops.find(
    (stop) => stop.name.trim().toLocaleLowerCase() === name.trim().toLocaleLowerCase(),
  );
  const fromStop = from ? findStop(from) : effectiveLive.stops[0];
  const toStop = to ? findStop(to) : effectiveLive.stops.at(-1);
  const invalidFrom = !!from && !fromStop;
  const invalidTo = !!to && !toStop;
  const invalidOrder = !!fromStop && !!toStop && fromStop.sequence >= toStop.sequence;

  if (invalidFrom || invalidTo || invalidOrder) {
    const message = invalidFrom || invalidTo
      ? `This route does not include ${invalidFrom ? `"${from}"` : ""}${invalidFrom && invalidTo ? " or " : ""}${invalidTo ? `"${to}"` : ""}.`
      : `${fromStop?.name} must come before ${toStop?.name} on this route.`;
    return (
      <div className="mx-auto max-w-3xl px-4 py-10">
        <p>{message}</p>
        <p className="mt-2 text-sm text-muted-foreground">
          Stops on this service: {effectiveLive.stops.map((stop) => stop.name).join(" → ")}
        </p>
        <Button className="mt-3" variant="outline" asChild>
          <Link to={`/search?from=${encodeURIComponent(effectiveLive.stops[0]?.name ?? "")}&to=${encodeURIComponent(effectiveLive.stops.at(-1)?.name ?? "")}`}>
            Search this route
          </Link>
        </Button>
      </div>
    );
  }

  // Determine current tracking status based on network, trip state, and GPS ping age
  const trackingStatus = computeTrackingStatus({
    isOnline,
    tripStatus: effectiveLive.status,
    recordedAt: effectiveLive.recordedAt,
    hasCoordinates: effectiveLive.latitude != null && effectiveLive.longitude != null,
  });

  // GPS and route fallback logic:
  // - GPS available: Live bus marker + route
  // - GPS signal lost: Last known location + warning
  // - Driver GPS disabled / Driver has not started / GPS data stale / Trip completed / Offline:
  //   Scheduled route ONLY (NEVER display a scheduled route as if it were the bus's live location)
  const bus: BusPin | null = (() => {
    if (effectiveLive.latitude == null || effectiveLive.longitude == null) return null;

    if (trackingStatus === "live") {
      return {
        latitude: effectiveLive.latitude,
        longitude: effectiveLive.longitude,
        heading: effectiveLive.heading,
        label: effectiveLive.fleetNumber,
        fleetNumber: effectiveLive.fleetNumber,
        routeCode: effectiveLive.routeCode,
        routeName: effectiveLive.routeName,
        speedKmph: effectiveLive.speedKmph,
        status: effectiveLive.status,
        variant: "live",
        recordedAt: effectiveLive.recordedAt,
      };
    }

    if (trackingStatus === "signal-lost") {
      return {
        latitude: effectiveLive.latitude,
        longitude: effectiveLive.longitude,
        heading: effectiveLive.heading,
        label: effectiveLive.fleetNumber,
        fleetNumber: effectiveLive.fleetNumber,
        routeCode: effectiveLive.routeCode,
        routeName: effectiveLive.routeName,
        speedKmph: effectiveLive.speedKmph,
        status: effectiveLive.status,
        variant: "last-known",
        recordedAt: effectiveLive.recordedAt,
      };
    }

    return null;
  })();

  const reachesYou = effectiveLive.status === "Running" && !effectiveLive.boardingPassed && effectiveLive.etaToYouMinutes != null;

  return (
    <div className="flex flex-col">
      {/* 1. Scrolling Marquee Notice (Visible whenever live tracking is unavailable, stale, signal-lost, completed, not-started, or offline) */}
      <TrackingNotice status={trackingStatus} />

      <div className="grid min-h-[calc(100vh-4rem)] lg:grid-cols-[380px_1fr]">
        <aside className="order-2 border-t bg-card/95 p-5 lg:order-1 lg:border-r lg:border-t-0 space-y-5">
          <div className="flex items-center justify-between gap-3">
            <div>
              <p className="text-xs uppercase tracking-wider text-muted-foreground">/track/{effectiveLive.fleetNumber}</p>
              <h1 className="font-display text-4xl">Bus {effectiveLive.fleetNumber}</h1>
            </div>
            <StatusBadge status={effectiveLive.status} />
          </div>

          <p className="text-sm text-muted-foreground">{effectiveLive.registrationNumber} · Route {effectiveLive.routeCode}</p>

          {/* Fallback Planned Route Card when live tracking is unavailable or non-live */}
          {trackingStatus !== "live" && (
            <ScheduledRouteCard
              origin={from || effectiveLive.stops[0]?.name}
              destination={to || effectiveLive.stops.at(-1)?.name}
              stops={effectiveLive.stops}
              status={trackingStatus}
            />
          )}

          {/* Journey Delay & ETA Status Card */}
          <div className="rounded-xl border bg-card p-4 shadow-xs">
            <div className="flex items-center justify-between border-b pb-3">
              <div>
                <p className="text-xs uppercase tracking-wider text-muted-foreground font-semibold">Bus journey status</p>
                <p className="text-xl font-bold text-ink">
                  {effectiveLive.delayStatus || (effectiveLive.delayMinutes && effectiveLive.delayMinutes > 3 ? `${effectiveLive.delayMinutes} min delayed` : "On time")}
                </p>
              </div>
              <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                effectiveLive.delayMinutes && effectiveLive.delayMinutes > 3
                  ? "bg-red-500/10 text-red-700 border border-red-500/20"
                  : "bg-emerald-500/10 text-emerald-800 border border-emerald-500/20"
              }`}>
                {effectiveLive.status === "Running" ? "● Live on road" : effectiveLive.status === "Completed" ? "✓ Completed" : "⏱ Scheduled"}
              </span>
            </div>
            <div className="mt-3 grid grid-cols-2 gap-3 text-xs">
              <div>
                <span className="text-muted-foreground">Scheduled departure</span>
                <p className="text-sm font-semibold text-foreground">{formatClock(effectiveLive.departureTime)}</p>
              </div>
              <div>
                <span className="text-muted-foreground">
                  {effectiveLive.actualDepartureTime ? "Actual departure" : "Estimated departure"}
                </span>
                <p className="text-sm font-semibold text-foreground">
                  {formatClock(effectiveLive.actualDepartureTime || effectiveLive.estimatedDepartureTime || effectiveLive.departureTime)}
                </p>
              </div>
            </div>
          </div>

          {trackingStatus === "live" && (
            <p className="flex items-center gap-2 text-sm text-pine font-medium">
              <span className="pulse" /> Live · updated {ago(effectiveLive.recordedAt)}
              {effectiveLive.simulation && " · demo playback"}
            </p>
          )}

          {trackingStatus === "signal-lost" && (
            <p className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-2 text-xs font-medium text-amber-900">
              ⚠️ Signal lost · Last GPS update was {ago(effectiveLive.recordedAt)}. Marker indicates last known position.
            </p>
          )}

          {trackingStatus === "completed" && (
            <p className="rounded-xl bg-ink px-4 py-3 text-paper">
              Trip completed. The bus has reached {effectiveLive.currentStop ?? "the last stop"}.
            </p>
          )}

          <div>
            <p className="text-xs uppercase tracking-wider text-muted-foreground">Right now</p>
            <p className="font-display text-3xl">{effectiveLive.currentStop ?? "Not on the road"}</p>
            {effectiveLive.nextStop && (
              <p className="text-muted-foreground">Next {effectiveLive.nextStop} · {formatEta(effectiveLive.etaMinutes)}</p>
            )}
          </div>

          <div className="rounded-2xl bg-paper px-4 py-3">
            <p className="text-xs uppercase tracking-wider text-muted-foreground">
              {reachesYou ? `Until it reaches ${from || "you"}` : `Until ${to || effectiveLive.stops.at(-1)?.name || "the last stop"}`}
            </p>
            <p className="font-display text-5xl leading-none">
              {reachesYou ? formatEta(effectiveLive.etaToYouMinutes) : formatEta(effectiveLive.etaToDestinationMinutes)}
            </p>
            {effectiveLive.fare != null && <p className="mt-2 text-sm">Fare {rupee(effectiveLive.fare)}</p>}
            {effectiveLive.simulation && (
              <p className="mt-2 text-xs text-muted-foreground">
                Playback is sped up so the whole corridor is visible. ETAs still use a 42 km/h road speed.
              </p>
            )}
          </div>

          <div>
            <StopTimeline stops={effectiveLive.stops} />
          </div>

          <LiveJourneyPanel
            snapshot={effectiveLive}
            from={from}
            to={to}
            date={date}
            delayAlert={delayAlert}
          />

          <Button className="w-full" variant="outline" asChild>
            <Link to={`/bus/${effectiveLive.fleetNumber}?${new URLSearchParams({ from, to, routeId: String(effectiveLive.routeId), date }).toString()}`}>
              Trip details
            </Link>
          </Button>
        </aside>

        <div className="relative order-1 h-[52vh] lg:order-2 lg:h-auto">
          {/* Always show the scheduled route polyline & all stops as the foundation */}
          <MapView
            path={effectiveLive.path}
            stops={effectiveLive.stops}
            bus={bus}
            focusBus={trackingStatus === "live"}
            className="h-full w-full"
          />

          {/* Real-time Status Overlay Chip */}
          <div
            role="status"
            className="absolute left-3 top-3 z-[1000] flex max-w-[calc(100%-1.5rem)] items-center gap-2 rounded-lg bg-card/95 px-3 py-2 text-xs shadow-md backdrop-blur border"
          >
            {trackingStatus === "live" ? (
              <>
                <span className="pulse" />
                <span>
                  GPS location · updated {ago(effectiveLive.recordedAt)}
                  {effectiveLive.speedKmph != null ? ` · ${Math.round(effectiveLive.speedKmph)} km/h` : ""}
                </span>
              </>
            ) : trackingStatus === "signal-lost" ? (
              <>
                <span className="h-2 w-2 rounded-full bg-amber-500" />
                <span className="font-medium text-amber-900 dark:text-amber-200">
                  GPS signal lost · Last known location ({ago(effectiveLive.recordedAt)})
                </span>
              </>
            ) : trackingStatus === "stale" ? (
              <>
                <span className="h-2 w-2 rounded-full bg-amber-500" />
                <span className="text-amber-800 dark:text-amber-300">
                  GPS data stale · Showing scheduled route
                </span>
              </>
            ) : trackingStatus === "not-started" ? (
              <>
                <span className="h-2 w-2 rounded-full bg-blue-500" />
                <span>Bus journey not started · Showing scheduled route</span>
              </>
            ) : trackingStatus === "completed" ? (
              <>
                <span className="h-2 w-2 rounded-full bg-slate-500" />
                <span>Trip completed · Showing route</span>
              </>
            ) : trackingStatus === "offline" ? (
              <>
                <span className="h-2 w-2 rounded-full bg-red-500" />
                <span className="font-medium text-red-800 dark:text-red-300">
                  Offline · Showing previously loaded route
                </span>
              </>
            ) : (
              <>
                <span className="h-2 w-2 rounded-full bg-amber-500" />
                <span>Live GPS location unavailable · Showing scheduled route</span>
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
