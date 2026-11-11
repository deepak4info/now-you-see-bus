import { Link, useParams, useSearchParams } from "react-router-dom";
import { useEffect, useState } from "react";
import { Star } from "lucide-react";
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
import { api, useQuery } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { ago, formatClock, formatDateWithWeekday, formatEta, getIndiaDate, isDateOnly, rupee } from "@/lib/format";
import { useBusLive } from "@/lib/live";
import type { SearchResponse, StopOption, TrackSnapshot } from "@/lib/types";

export function BusPage() {
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
  const live = useBusLive(
    fleet,
    seed,
    requestedRouteId ?? seed?.routeId,
    !tripId && date === getIndiaDate() && seed?.status !== "Completed" && seed?.status !== "Cancelled",
  );
  const { user } = useAuth();
  const stopOptions = useQuery<StopOption[]>("/api/stops");
  const [note, setNote] = useState("");
  const [saving, setSaving] = useState(false);

  // Network & offline route caching
  const isOnline = useNetworkStatus();
  const [cachedSnapshot, setCachedSnapshot] = useState<TrackSnapshot | null>(() => getRouteFromCache(fleet));

  // Ticker to recompute freshness
  const [, setTick] = useState(0);
  useEffect(() => {
    const timer = window.setInterval(() => setTick((t) => t + 1), 10000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (live && live.stops && live.stops.length > 0) {
      saveRouteToCache(fleet, live);
      setCachedSnapshot(live);
    }
  }, [fleet, live]);

  const effectiveLive = live ?? cachedSnapshot;

  if (query.loading && !effectiveLive) return <p className="mx-auto max-w-6xl px-4 py-10 text-muted-foreground">Loading bus {fleet}…</p>;
  if (!effectiveLive) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-10">
        <p>{query.error ?? (hasJourney
          ? `No result was found for bus ${fleet} on the selected route.`
          : "This bus is not on today's board.")}</p>
        <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
      </div>
    );
  }

  const effectiveFrom = from || effectiveLive.stops[0]?.name || "";
  const effectiveTo = to || effectiveLive.stops.at(-1)?.name || "";
  const findStop = (name: string) => effectiveLive.stops.find(
    (stop) => stop.name.trim().toLocaleLowerCase() === name.trim().toLocaleLowerCase(),
  ) ?? null;
  const selectedFromStop = findStop(effectiveFrom);
  const selectedToStop = findStop(effectiveTo);
  const invalidFromStop = !!from && !selectedFromStop;
  const invalidToStop = !!to && !selectedToStop;
  const invalidStopOrder = !!selectedFromStop && !!selectedToStop && selectedFromStop.sequence >= selectedToStop.sequence;

  async function save() {
    if (!effectiveLive) return;
    if (invalidFromStop || invalidToStop || invalidStopOrder) {
      setNote(invalidFromStop || invalidToStop
        ? `This route does not include ${invalidFromStop ? from : ""}${invalidFromStop && invalidToStop ? " or " : ""}${invalidToStop ? to : ""}.`
        : `${selectedFromStop?.name} must come before ${selectedToStop?.name} on this route.`);
      return;
    }
    setSaving(true);
    setNote("");
    try {
      await api("/api/favorites", {
        method: "POST",
        body: JSON.stringify({
          busId: effectiveLive.busId,
          routeId: effectiveLive.routeId,
          fromStopId: selectedFromStop?.stopId ?? effectiveLive.stops[0]?.stopId ?? null,
          toStopId: selectedToStop?.stopId ?? effectiveLive.stops.at(-1)?.stopId ?? null,
          label: `${effectiveFrom} → ${effectiveTo} · Bus ${effectiveLive.fleetNumber}`,
        }),
      });
      setNote("Saved to favourites. You will get an alert when this bus starts or finishes.");
    } catch (err) {
      setNote(err instanceof Error ? err.message : "Could not save");
    } finally {
      setSaving(false);
    }
  }

  if (invalidFromStop || invalidToStop || invalidStopOrder) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-10">
        <p>{invalidFromStop || invalidToStop
          ? `This route does not include ${invalidFromStop ? `"${from}"` : ""}${invalidFromStop && invalidToStop ? " or " : ""}${invalidToStop ? `"${to}"` : ""}.`
          : `${selectedFromStop?.name} must come before ${selectedToStop?.name} on this route.`}</p>
        <p className="mt-2 text-sm text-muted-foreground">Use stops that belong to this service: {effectiveLive.stops.map((stop) => stop.name).join(" → ")}</p>
        <Button className="mt-3" variant="outline" asChild><Link to={`/search?from=${encodeURIComponent(effectiveLive.stops[0]?.name ?? "")}&to=${encodeURIComponent(effectiveLive.stops.at(-1)?.name ?? "")}`}>Search this route</Link></Button>
      </div>
    );
  }

  const trackParams = new URLSearchParams(qs);
  trackParams.set("routeId", String(effectiveLive.routeId));
  const track = `/track/${effectiveLive.fleetNumber}?${trackParams.toString()}`;

  // Evaluate tracking status
  const trackingStatus = computeTrackingStatus({
    isOnline,
    tripStatus: effectiveLive.status,
    recordedAt: effectiveLive.recordedAt,
    hasCoordinates: effectiveLive.latitude != null && effectiveLive.longitude != null,
  });

  // GPS and route fallback pin logic:
  // Fresh GPS: live marker. Lost GPS: last known. Otherwise: scheduled route only!
  const busPin: BusPin | null = (() => {
    if (effectiveLive.latitude == null || effectiveLive.longitude == null) return null;

    if (trackingStatus === "live") {
      return {
        latitude: effectiveLive.latitude,
        longitude: effectiveLive.longitude,
        heading: effectiveLive.heading,
        label: effectiveLive.fleetNumber,
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
        variant: "last-known",
        recordedAt: effectiveLive.recordedAt,
      };
    }

    return null;
  })();

  const routeDistance = effectiveLive.stops.at(-1)?.distanceKm ?? 0;
  const routeDuration = getRouteDurationMinutes(effectiveLive.stops);
  const selectedBoardingStop = stopOptions.data?.find(
    (stop) => stop.name.toLocaleLowerCase() === from.toLocaleLowerCase(),
  );

  return (
    <div className="flex flex-col">
      {/* 1. Scrolling Marquee Notice when live tracking is unavailable, stale, signal-lost, completed, not-started, or offline */}
      <TrackingNotice status={trackingStatus} />

      <div className="mx-auto grid max-w-6xl gap-6 px-4 py-8 lg:grid-cols-[0.9fr_1.1fr] w-full">
        <section className="rounded-2xl border bg-card p-5 space-y-5">
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-xs uppercase tracking-wider text-muted-foreground">{effectiveLive.operatorName} · Route {effectiveLive.routeCode}</p>
              <h1 className="font-display text-4xl">Bus {effectiveLive.fleetNumber}</h1>
              <p className="mt-1">{effectiveLive.registrationNumber} · {effectiveLive.busType}</p>
            </div>
            <StatusBadge status={effectiveLive.status} />
          </div>

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

          <dl className="grid grid-cols-2 gap-3 text-sm">
            <div><dt className="text-muted-foreground">Departure</dt><dd className="font-medium">{formatClock(effectiveLive.departureTime)}</dd></div>
            <div><dt className="text-muted-foreground">Fare {from && to ? `${from}–${to}` : ""}</dt><dd className="font-medium">{rupee(effectiveLive.fare)}</dd></div>
            <div><dt className="text-muted-foreground">Driver</dt><dd className="font-medium">{effectiveLive.driverName ?? "Unassigned"}</dd></div>
            <div><dt className="text-muted-foreground">Next stop</dt><dd className="font-medium">{effectiveLive.nextStop ?? "—"} {effectiveLive.etaMinutes != null ? `· ${formatEta(effectiveLive.etaMinutes)}` : ""}</dd></div>
          </dl>

          <section className="border-t pt-5">
            <h2 className="text-lg font-semibold text-ink">Bus route</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">
              {routeDistance.toFixed(0)} km{routeDuration != null ? ` · ${formatEta(routeDuration)}` : ""}
            </p>
            <p className="mt-3 text-sm leading-7 text-ink">
              {effectiveLive.stops.map((stop, index) => (
                <span key={stop.stopId}>
                  {index > 0 && <span className="px-1.5 text-muted-foreground">→</span>}
                  <span className={stop.name.toLocaleLowerCase() === from.toLocaleLowerCase() ? "rounded bg-gold/30 px-1 font-semibold" : ""}>
                    {stop.name}
                  </span>
                </span>
              ))}
            </p>
          </section>

          <section className="border-t pt-5">
            <h2 className="text-lg font-semibold text-ink">Boarding point</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">
              {selectedBoardingStop?.city || selectedFromStop?.name || from} · {formatDateWithWeekday(date)}
            </p>
            <div className="mt-5">
              <StopTimeline stops={effectiveLive.stops} serviceDate={date} boardingStop={from} />
            </div>
          </section>

          <div className="flex flex-wrap gap-2 pt-2">
            <Button asChild><Link to={track}>Track bus</Link></Button>
            {user && (user.role === "Passenger" || user.role === "Admin") ? (
              <Button variant="outline" onClick={() => void save()} disabled={saving}>
                <Star className="h-4 w-4" /> Save
              </Button>
            ) : (
              <Button variant="outline" asChild><Link to={`/login?next=${encodeURIComponent(window.location.pathname + window.location.search)}`}>Sign in to save</Link></Button>
            )}
          </div>
          {note && <p className="text-sm text-muted-foreground">{note}</p>}
        </section>

        <section className="min-h-[420px] overflow-hidden rounded-2xl border bg-card">
          <div className="relative h-full min-h-[420px]">
            {/* Always show the scheduled route as foundation */}
            <MapView
              path={effectiveLive.path}
              stops={effectiveLive.stops}
              bus={busPin}
              focusBus={trackingStatus === "live"}
              className="h-full min-h-[420px] w-full"
            />

            {/* Real-time Status Overlay Chip */}
            <div
              role="status"
              className="absolute left-3 top-3 z-[1000] flex max-w-[calc(100%-1.5rem)] items-center gap-2 rounded-lg bg-card/95 px-3 py-2 text-xs shadow-md backdrop-blur border"
            >
              {trackingStatus === "live" ? (
                <>
                  <span className="pulse" />
                  <span>GPS location · updated {ago(effectiveLive.recordedAt)}</span>
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
        </section>
      </div>
    </div>
  );
}

function getRouteDurationMinutes(stops: TrackSnapshot["stops"]) {
  if (stops.length < 2) return null;
  const toMinutes = (time: string) => {
    const [hours, minutes] = time.split(":").map(Number);
    return hours * 60 + minutes;
  };
  const first = toMinutes(stops[0].scheduledTime);
  let last = toMinutes(stops.at(-1)!.scheduledTime);
  if (last < first) last += 24 * 60;
  return last - first;
}
