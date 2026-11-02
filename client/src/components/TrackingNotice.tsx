import { useEffect, useState } from "react";
import type { TrackSnapshot } from "@/lib/types";

export type TrackingStatus =
  | "live"
  | "not-started"
  | "gps-unavailable"
  | "signal-lost"
  | "stale"
  | "completed"
  | "offline";

export const TRACKING_MESSAGES: Record<TrackingStatus, string> = {
  live: "Your bus is currently being tracked live.",
  "not-started": "Your bus has not started its journey yet.",
  "gps-unavailable": "Live bus location is unavailable. Showing scheduled route.",
  "signal-lost": "GPS signal lost. Showing the last known location.",
  stale: "Bus location has not been updated recently.",
  completed: "Your bus journey has been completed.",
  offline: "You are offline. Showing previously loaded route.",
};

export const TRACKING_STATUS_LABELS: Record<TrackingStatus, string> = {
  live: "Live Tracking",
  "not-started": "Not Started Yet",
  "gps-unavailable": "Live Tracking Unavailable",
  "signal-lost": "Signal Lost",
  stale: "Location Stale",
  completed: "Trip Completed",
  offline: "Offline",
};

/**
 * Computes tracking status based on network, trip state, and GPS ping freshness.
 *
 * Rules:
 * - If browser is offline -> "offline"
 * - If trip is Completed -> "completed"
 * - If trip is not Running -> "not-started"
 * - If trip is Running but has no coordinates or recorded timestamp -> "gps-unavailable"
 * - If GPS fix is older than 10 minutes (600s) -> "stale"
 * - If GPS fix is between 2 minutes (120s) and 10 minutes (600s) -> "signal-lost"
 * - If GPS fix is within 2 minutes (<= 120s) -> "live"
 */
export function computeTrackingStatus({
  isOnline = true,
  tripStatus,
  recordedAt,
  hasCoordinates,
  now = Date.now(),
}: {
  isOnline?: boolean;
  tripStatus: string;
  recordedAt?: string | null;
  hasCoordinates: boolean;
  now?: number;
}): TrackingStatus {
  if (!isOnline) {
    return "offline";
  }

  const normalizedStatus = (tripStatus || "").trim().toLowerCase();
  if (normalizedStatus === "completed") {
    return "completed";
  }

  if (normalizedStatus !== "running") {
    return "not-started";
  }

  if (!hasCoordinates || !recordedAt) {
    return "gps-unavailable";
  }

  const recordedTime = new Date(recordedAt).getTime();
  if (isNaN(recordedTime)) {
    return "gps-unavailable";
  }

  const ageSeconds = (now - recordedTime) / 1000;

  if (ageSeconds > 600) {
    return "stale";
  }

  if (ageSeconds > 120) {
    return "signal-lost";
  }

  return "live";
}

/**
 * Hook to track whether the client currently has network connectivity.
 */
export function useNetworkStatus(): boolean {
  const [isOnline, setIsOnline] = useState<boolean>(() => {
    return typeof navigator !== "undefined" && typeof navigator.onLine === "boolean"
      ? navigator.onLine
      : true;
  });

  useEffect(() => {
    const handleOnline = () => setIsOnline(true);
    const handleOffline = () => setIsOnline(false);

    window.addEventListener("online", handleOnline);
    window.addEventListener("offline", handleOffline);

    return () => {
      window.removeEventListener("online", handleOnline);
      window.removeEventListener("offline", handleOffline);
    };
  }, []);

  return isOnline;
}

const CACHE_KEY_PREFIX = "wimb_route_cache_";

/**
 * Persists a snapshot to localStorage for offline fallback.
 */
export function saveRouteToCache(fleet: string, data: TrackSnapshot): void {
  try {
    if (typeof window !== "undefined" && window.localStorage && fleet && data?.stops?.length) {
      localStorage.setItem(`${CACHE_KEY_PREFIX}${fleet.toLowerCase()}`, JSON.stringify(data));
    }
  } catch {
    // Gracefully handle storage quota or private browsing exceptions
  }
}

/**
 * Retrieves a previously cached snapshot for a fleet when offline or on network failure.
 */
export function getRouteFromCache(fleet: string): TrackSnapshot | null {
  try {
    if (typeof window !== "undefined" && window.localStorage && fleet) {
      const raw = localStorage.getItem(`${CACHE_KEY_PREFIX}${fleet.toLowerCase()}`);
      if (raw) {
        return JSON.parse(raw) as TrackSnapshot;
      }
    }
  } catch {
    // Ignore JSON parse errors
  }
  return null;
}

/**
 * Scrolling Marquee Notice Component
 * Implemented with pure CSS animation (supporting prefers-reduced-motion and hover-to-pause).
 * Returns null when tracking is fully live.
 */
export function TrackingNotice({
  status,
  customMessage,
  className = "",
}: {
  status: TrackingStatus;
  customMessage?: string;
  className?: string;
}) {
  if (status === "live") return null;

  const message = customMessage ?? TRACKING_MESSAGES[status];

  const iconEmoji =
    status === "offline"
      ? "📡"
      : status === "signal-lost"
      ? "⚠️"
      : status === "stale"
      ? "⏳"
      : status === "completed"
      ? "🏁"
      : "ℹ️";

  return (
    <div
      className={`tracking-marquee ${className}`}
      role="status"
      aria-live="polite"
      title={message}
    >
      <div className="tracking-marquee__content">
        <span className="mr-2" aria-hidden="true">{iconEmoji}</span>
        <span>{message}</span>
      </div>
    </div>
  );
}

/**
 * Fallback Route Overview Card
 * Displays the route foundation, origin, destination, and stop chain
 * whenever live tracking is unavailable or in a non-live state.
 */
export function ScheduledRouteCard({
  origin,
  destination,
  stops,
  status,
  className = "",
}: {
  origin?: string;
  destination?: string;
  stops: { name: string; sequence?: number }[];
  status: TrackingStatus;
  className?: string;
}) {
  if (status === "live") return null;

  const startName = origin || stops[0]?.name || "Depot";
  const endName = destination || stops.at(-1)?.name || "Terminus";
  const stopChain = stops.map((s) => s.name).join(" → ");

  return (
    <div className={`rounded-2xl border border-amber-300/80 bg-amber-50/90 p-4 text-amber-950 shadow-sm dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-100 ${className}`}>
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-amber-200/80 pb-3 dark:border-amber-900/60">
        <div>
          <h2 className="font-display text-xl font-bold tracking-tight text-ink">
            {startName} → {endName}
          </h2>
          <p className="mt-0.5 text-xs font-medium text-amber-800 dark:text-amber-300">
            {status === "offline"
              ? "Showing previously loaded route information."
              : status === "signal-lost"
              ? "Live GPS signal temporarily lost. Showing last known location."
              : status === "stale"
              ? "Location data is stale. Showing scheduled bus route."
              : status === "not-started"
              ? "Trip has not started yet. Showing scheduled bus route."
              : status === "completed"
              ? "Bus journey is complete. Showing route."
              : "Live location is currently unavailable. Showing the scheduled bus route."}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="inline-flex items-center rounded-full bg-amber-200/90 px-2.5 py-0.5 text-xs font-semibold text-amber-900 dark:bg-amber-900/80 dark:text-amber-200">
            {TRACKING_STATUS_LABELS[status]}
          </span>
          <span className="inline-flex items-center rounded-full bg-emerald-100 px-2.5 py-0.5 text-xs font-semibold text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300">
            Scheduled route
          </span>
        </div>
      </div>

      <div className="mt-3 space-y-2 text-xs sm:text-sm">
        <div>
          <span className="font-semibold text-amber-900 dark:text-amber-200">Route: </span>
          <span className="leading-relaxed text-amber-950 dark:text-amber-100">{stopChain}</span>
        </div>
        <div className="grid grid-cols-1 gap-1 text-xs text-amber-800 dark:text-amber-300 sm:grid-cols-2">
          <p>
            <span className="font-semibold text-amber-900 dark:text-amber-200">Starting point: </span>
            {startName}
          </p>
          <p>
            <span className="font-semibold text-amber-900 dark:text-amber-200">Destination: </span>
            {endName}
          </p>
        </div>
      </div>
    </div>
  );
}
