import { useState } from "react";
import {
  Building2,
  BusFront,
  CalendarDays,
  ChevronDown,
  ChevronUp,
  Clock,
  MapPin,
  RotateCcw,
  Route as RouteIcon,
  SlidersHorizontal,
  X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { formatDateWithWeekday, isDateOnly } from "@/lib/format";
import type { BusTrackingItem, StopOnTrip, TrackingFilterOptions } from "@/lib/types";
import { cn } from "@/lib/utils";

export type TrackingTabKey = "live" | "recent" | "upcoming";

export type TrackingFilterValues = {
  busNumber: string;
  routeId: string;
  date: string;
  operator: string;
  stop: string;
  busType: string;
  timeOfDay: string;
  liveStatus: string;
  recentStatus: string;
  daysAhead: string;
};

export const DEFAULT_TRACKING_FILTERS: TrackingFilterValues = {
  busNumber: "",
  routeId: "",
  date: "",
  operator: "",
  stop: "",
  busType: "",
  timeOfDay: "",
  liveStatus: "",
  recentStatus: "",
  daysAhead: "6",
};

const TIME_OF_DAY_LABELS: Record<string, string> = {
  morning: "Morning (12 AM – 12 PM)",
  afternoon: "Afternoon (12 PM – 5 PM)",
  evening: "Evening (5 PM – 9 PM)",
  night: "Night (9 PM – 12 AM)",
};

const LIVE_STATUS_LABELS: Record<string, string> = {
  ontime: "On time",
  delayed: "Delayed",
  early: "Early",
};

const DAYS_AHEAD_LABELS: Record<string, string> = {
  "0": "Today only",
  "2": "Next 3 days",
  "6": "Next 7 days",
  "13": "Next 14 days",
};

/**
 * Builds query parameters sent to /api/tracking/{live|recent|upcoming} for the given tab and filter state.
 */
export function buildTrackingFilterParams(
  filters: TrackingFilterValues,
  tab: TrackingTabKey,
): URLSearchParams {
  const params = new URLSearchParams();
  const busNumber = filters.busNumber.trim();
  const stop = filters.stop.trim();

  if (busNumber) params.set("busNumber", busNumber);
  if (filters.routeId) params.set("routeId", filters.routeId);
  if (isDateOnly(filters.date)) params.set("date", filters.date);
  if (filters.operator) {
    if (/^\d+$/.test(filters.operator)) {
      params.set("operatorId", filters.operator);
    } else {
      params.set("operator", filters.operator);
    }
  }
  if (stop) params.set("stop", stop);
  if (filters.busType) params.set("busType", filters.busType);
  if (filters.timeOfDay) params.set("timeOfDay", filters.timeOfDay);

  if (tab === "live" && filters.liveStatus) {
    params.set("status", filters.liveStatus);
  } else if (tab === "recent" && filters.recentStatus) {
    params.set("status", filters.recentStatus);
  } else if (tab === "upcoming" && !filters.date) {
    params.set("daysAhead", filters.daysAhead || "6");
  }

  return params;
}

export function hasActiveTrackingFilters(filters: TrackingFilterValues, tab: TrackingTabKey): boolean {
  if (
    filters.busNumber.trim()
    || filters.routeId
    || filters.date
    || filters.operator
    || filters.stop.trim()
    || filters.busType
    || filters.timeOfDay
  ) {
    return true;
  }
  if (tab === "live" && filters.liveStatus) return true;
  if (tab === "recent" && filters.recentStatus) return true;
  if (tab === "upcoming" && !filters.date && filters.daysAhead !== DEFAULT_TRACKING_FILTERS.daysAhead) return true;
  return false;
}

/**
 * Client-side predicate mirroring backend filtering for instant responsiveness across Live, Recent, and Upcoming tabs,
 * SignalR updates, and legacy-backend fallbacks.
 */
export function matchesTrackingItemFilters(
  item: BusTrackingItem,
  filters: TrackingFilterValues,
  options: TrackingFilterOptions,
  tab: TrackingTabKey,
  opts?: { checkStop?: boolean; extraStops?: StopOnTrip[] },
): boolean {
  const busTerm = filters.busNumber.trim().toLowerCase();
  if (busTerm) {
    const stripped = busTerm.startsWith("bus") ? busTerm.slice(3).trim() : busTerm;
    const normalized = stripped || busTerm;
    const compact = normalized.replace(/[\s-]+/g, "") || busTerm;

    const candidates = [item.fleetNumber, item.registrationNumber, item.busName];
    const matched = candidates.some((field) => {
      if (!field) return false;
      const lower = field.toLowerCase();
      const fieldCompact = lower.replace(/[\s-]+/g, "");
      return lower.includes(busTerm) || lower.includes(normalized) || fieldCompact.includes(compact);
    });
    if (!matched) return false;
  }

  if (filters.routeId && String(item.routeId) !== filters.routeId) {
    return false;
  }

  if (filters.date && item.serviceDate !== filters.date) {
    return false;
  }

  if (tab === "upcoming" && !filters.date && filters.daysAhead && item.serviceDate) {
    const daysAheadNum = Number(filters.daysAhead);
    if (Number.isFinite(daysAheadNum) && daysAheadNum >= 0) {
      const minDate = getIstDateOffset(0);
      const maxDate = getIstDateOffset(daysAheadNum);
      if (item.serviceDate < minDate || item.serviceDate > maxDate) {
        return false;
      }
    }
  }

  if (filters.operator) {
    const selectedOp = options.operators.find((o) => String(o.id) === filters.operator);
    if (selectedOp) {
      const expectedName = selectedOp.name.trim().toLowerCase();
      if ((item.operatorName ?? "").trim().toLowerCase() !== expectedName) return false;
    } else if (!/^\d+$/.test(filters.operator)) {
      const expectedName = filters.operator.trim().toLowerCase();
      if ((item.operatorName ?? "").trim().toLowerCase() !== expectedName) return false;
    }
  }

  if (filters.busType && (item.busType ?? "").trim().toLowerCase() !== filters.busType.trim().toLowerCase()) {
    return false;
  }

  if (filters.timeOfDay) {
    const hour = parseHour24(item.departureTime || item.actualDepartureTime || "");
    if (hour === null || !matchesTimeOfDayHour(hour, filters.timeOfDay)) return false;
  }

  if (tab === "live" && filters.liveStatus) {
    const delay = item.delayMinutes;
    if (filters.liveStatus === "ontime" && delay != null && (delay > 2 || delay < -2)) return false;
    if (filters.liveStatus === "delayed" && (delay == null || delay <= 2)) return false;
    if (filters.liveStatus === "early" && (delay == null || delay >= -2)) return false;
  }

  if (tab === "recent" && filters.recentStatus) {
    if ((item.status ?? "").trim().toLowerCase() !== filters.recentStatus.trim().toLowerCase()) {
      return false;
    }
  }

  const stopTerm = filters.stop.trim().toLowerCase();
  if (stopTerm && opts?.checkStop) {
    const stopFields = [
      item.originStop,
      item.destinationStop,
      item.currentLocation,
      item.nextStop,
      item.routeName,
      item.routeCode,
      ...(opts.extraStops?.flatMap((s) => [s.name, s.code]) ?? []),
    ];
    if (!stopFields.some((val) => val?.toLowerCase().includes(stopTerm))) {
      return false;
    }
  }

  return true;
}

export function matchesLiveItemFilters(
  item: BusTrackingItem,
  filters: TrackingFilterValues,
  options: TrackingFilterOptions,
  opts?: { checkStop?: boolean; extraStops?: StopOnTrip[] },
): boolean {
  return matchesTrackingItemFilters(item, filters, options, "live", opts);
}

function getIstDateOffset(offsetDays: number): string {
  const istMs = Date.now() + (5 * 60 + 30) * 60_000 + offsetDays * 86_400_000;
  const d = new Date(istMs);
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, "0");
  const day = String(d.getUTCDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function parseHour24(value: string): number | null {
  if (!value) return null;
  const trimmed = value.trim().toUpperCase();
  const isPm = trimmed.endsWith("PM");
  const isAm = trimmed.endsWith("AM");
  const raw = trimmed.replace(/\s*(AM|PM)$/, "");
  const [hPart] = raw.split(":");
  const h = Number(hPart);
  if (!Number.isFinite(h)) return null;
  if (isPm && h < 12) return h + 12;
  if (isAm && h === 12) return 0;
  return h;
}

function matchesTimeOfDayHour(hour: number, windowKey: string): boolean {
  switch (windowKey) {
    case "morning":
      return hour >= 0 && hour < 12;
    case "afternoon":
      return hour >= 12 && hour < 17;
    case "evening":
      return hour >= 17 && hour < 21;
    case "night":
      return hour >= 21 && hour < 24;
    default:
      return true;
  }
}

type BusTrackingFiltersProps = {
  activeTab: TrackingTabKey;
  filters: TrackingFilterValues;
  options: TrackingFilterOptions;
  loading: boolean;
  resultSummary: string;
  onChange: (next: TrackingFilterValues, immediate?: boolean) => void;
  onClearAll: () => void;
};

export function BusTrackingFilters({
  activeTab,
  filters,
  options,
  loading,
  resultSummary,
  onChange,
  onClearAll,
}: BusTrackingFiltersProps) {
  const [showMore, setShowMore] = useState(false);

  const secondaryCount = [
    filters.stop.trim(),
    filters.busType,
    filters.timeOfDay,
    activeTab === "live" ? filters.liveStatus : activeTab === "recent" ? filters.recentStatus : "",
    activeTab === "upcoming" && !filters.date && filters.daysAhead !== DEFAULT_TRACKING_FILTERS.daysAhead
      ? filters.daysAhead
      : "",
  ].filter(Boolean).length;

  const hasFilters = hasActiveTrackingFilters(filters, activeTab);

  const activeChips: { key: string; label: string; onRemove: () => void }[] = [];
  if (filters.busNumber.trim()) {
    activeChips.push({
      key: "busNumber",
      label: `Bus: ${filters.busNumber.trim()}`,
      onRemove: () => onChange({ ...filters, busNumber: "" }, true),
    });
  }
  if (filters.routeId) {
    const route = options.routes.find((r) => String(r.id) === filters.routeId);
    activeChips.push({
      key: "routeId",
      label: route ? `Route: ${route.code}` : `Route #${filters.routeId}`,
      onRemove: () => onChange({ ...filters, routeId: "" }, true),
    });
  }
  if (filters.operator) {
    const op = options.operators.find((o) => String(o.id) === filters.operator);
    activeChips.push({
      key: "operator",
      label: `Operator: ${op ? op.name : filters.operator}`,
      onRemove: () => onChange({ ...filters, operator: "" }, true),
    });
  }
  if (filters.date) {
    activeChips.push({
      key: "date",
      label: `Date: ${isDateOnly(filters.date) ? formatDateWithWeekday(filters.date) : filters.date}`,
      onRemove: () => onChange({ ...filters, date: "" }, true),
    });
  }
  if (filters.stop.trim()) {
    activeChips.push({
      key: "stop",
      label: `Stop: ${filters.stop.trim()}`,
      onRemove: () => onChange({ ...filters, stop: "" }, true),
    });
  }
  if (filters.busType) {
    activeChips.push({
      key: "busType",
      label: `Type: ${filters.busType}`,
      onRemove: () => onChange({ ...filters, busType: "" }, true),
    });
  }
  if (filters.timeOfDay) {
    activeChips.push({
      key: "timeOfDay",
      label: TIME_OF_DAY_LABELS[filters.timeOfDay] ?? filters.timeOfDay,
      onRemove: () => onChange({ ...filters, timeOfDay: "" }, true),
    });
  }
  if (activeTab === "live" && filters.liveStatus) {
    activeChips.push({
      key: "liveStatus",
      label: `Status: ${LIVE_STATUS_LABELS[filters.liveStatus] ?? filters.liveStatus}`,
      onRemove: () => onChange({ ...filters, liveStatus: "" }, true),
    });
  }
  if (activeTab === "recent" && filters.recentStatus) {
    activeChips.push({
      key: "recentStatus",
      label: `Status: ${filters.recentStatus}`,
      onRemove: () => onChange({ ...filters, recentStatus: "" }, true),
    });
  }
  if (activeTab === "upcoming" && !filters.date && filters.daysAhead !== DEFAULT_TRACKING_FILTERS.daysAhead) {
    activeChips.push({
      key: "daysAhead",
      label: `Window: ${DAYS_AHEAD_LABELS[filters.daysAhead] ?? filters.daysAhead}`,
      onRemove: () => onChange({ ...filters, daysAhead: DEFAULT_TRACKING_FILTERS.daysAhead }, true),
    });
  }

  return (
    <div
      className="mt-5 rounded-2xl border border-border/70 bg-card/90 p-3.5 shadow-xs sm:p-4"
      role="search"
      aria-label="Filter bus tracking and schedules"
    >
      {/* Primary Filters: Bus number, Route, Operator, Date */}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div className="min-w-0">
          <label
            htmlFor="tracking-filter-bus"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
          >
            Bus number
          </label>
          <div className="relative">
            <BusFront
              className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              aria-hidden="true"
            />
            <input
              id="tracking-filter-bus"
              type="text"
              autoComplete="off"
              placeholder="Fleet or reg. no. (e.g. HR-62)"
              value={filters.busNumber}
              onChange={(e) => onChange({ ...filters, busNumber: e.target.value })}
              className="h-10 w-full min-w-0 rounded-xl border border-input bg-background/60 pl-9 pr-8 text-sm text-ink outline-none transition placeholder:text-muted-foreground focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
            />
            {filters.busNumber && (
              <button
                type="button"
                aria-label="Clear bus number filter"
                onClick={() => onChange({ ...filters, busNumber: "" }, true)}
                className="absolute right-2 top-1/2 -translate-y-1/2 rounded-full p-1 text-muted-foreground hover:bg-muted hover:text-ink"
              >
                <X className="h-3.5 w-3.5" aria-hidden="true" />
              </button>
            )}
          </div>
        </div>

        <div className="min-w-0">
          <label
            htmlFor="tracking-filter-route"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
          >
            Route
          </label>
          <div className="relative">
            <RouteIcon
              className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              aria-hidden="true"
            />
            <select
              id="tracking-filter-route"
              value={filters.routeId}
              onChange={(e) => onChange({ ...filters, routeId: e.target.value }, true)}
              className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 pl-9 pr-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
            >
              <option value="">All routes</option>
              {options.routes.map((route) => (
                <option key={route.id} value={String(route.id)}>
                  {route.code} · {route.name}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="min-w-0">
          <label
            htmlFor="tracking-filter-operator"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
          >
            Operator&apos;s name
          </label>
          <div className="relative">
            <Building2
              className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              aria-hidden="true"
            />
            <select
              id="tracking-filter-operator"
              value={filters.operator}
              onChange={(e) => onChange({ ...filters, operator: e.target.value }, true)}
              className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 pl-9 pr-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
            >
              <option value="">All operators</option>
              {options.operators.map((op) => (
                <option key={op.id > 0 ? op.id : op.name} value={op.id > 0 ? String(op.id) : op.name}>
                  {op.name}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="min-w-0">
          <label
            htmlFor="tracking-filter-date"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
          >
            Date
          </label>
          <div className="flex h-10 items-center gap-1.5 rounded-xl border border-input bg-background/60 px-3 transition focus-within:border-primary/60 focus-within:ring-2 focus-within:ring-ring">
            <CalendarDays className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <input
              id="tracking-filter-date"
              type="date"
              value={filters.date}
              onChange={(e) => onChange({ ...filters, date: e.target.value }, true)}
              className="min-w-0 flex-1 border-0 bg-transparent text-sm text-ink outline-none"
            />
            {filters.date && (
              <button
                type="button"
                aria-label="Clear date filter"
                onClick={() => onChange({ ...filters, date: "" }, true)}
                className="shrink-0 rounded-full p-1 text-muted-foreground hover:bg-muted hover:text-ink"
              >
                <X className="h-3.5 w-3.5" aria-hidden="true" />
              </button>
            )}
          </div>
        </div>
      </div>

      {/* Expandable Secondary Filters ("other etc") */}
      {showMore && (
        <div
          id="tracking-more-filters"
          className="mt-3 grid grid-cols-1 gap-3 border-t border-border/60 pt-3 sm:grid-cols-2 lg:grid-cols-4"
        >
          <div className="min-w-0">
            <label
              htmlFor="tracking-filter-stop"
              className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
            >
              Stop or place
            </label>
            <div className="relative">
              <MapPin
                className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <input
                id="tracking-filter-stop"
                type="text"
                autoComplete="off"
                placeholder="Any stop (e.g. Hisar, Bhuna)"
                value={filters.stop}
                onChange={(e) => onChange({ ...filters, stop: e.target.value })}
                className="h-10 w-full min-w-0 rounded-xl border border-input bg-background/60 pl-9 pr-8 text-sm text-ink outline-none transition placeholder:text-muted-foreground focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
              />
              {filters.stop && (
                <button
                  type="button"
                  aria-label="Clear stop filter"
                  onClick={() => onChange({ ...filters, stop: "" }, true)}
                  className="absolute right-2 top-1/2 -translate-y-1/2 rounded-full p-1 text-muted-foreground hover:bg-muted hover:text-ink"
                >
                  <X className="h-3.5 w-3.5" aria-hidden="true" />
                </button>
              )}
            </div>
          </div>

          <div className="min-w-0">
            <label
              htmlFor="tracking-filter-bus-type"
              className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
            >
              Bus type
            </label>
            <select
              id="tracking-filter-bus-type"
              value={filters.busType}
              onChange={(e) => onChange({ ...filters, busType: e.target.value }, true)}
              className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 px-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
            >
              <option value="">All bus types</option>
              {options.busTypes.map((type) => (
                <option key={type} value={type}>
                  {type}
                </option>
              ))}
            </select>
          </div>

          <div className="min-w-0">
            <label
              htmlFor="tracking-filter-time"
              className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
            >
              Departure time
            </label>
            <div className="relative">
              <Clock
                className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <select
                id="tracking-filter-time"
                value={filters.timeOfDay}
                onChange={(e) => onChange({ ...filters, timeOfDay: e.target.value }, true)}
                className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 pl-9 pr-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
              >
                <option value="">Any time</option>
                <option value="morning">Morning (12 AM – 12 PM)</option>
                <option value="afternoon">Afternoon (12 PM – 5 PM)</option>
                <option value="evening">Evening (5 PM – 9 PM)</option>
                <option value="night">Night (9 PM – 12 AM)</option>
              </select>
            </div>
          </div>

          <div className="min-w-0">
            {activeTab === "live" && (
              <>
                <label
                  htmlFor="tracking-filter-live-status"
                  className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
                >
                  Punctuality
                </label>
                <select
                  id="tracking-filter-live-status"
                  value={filters.liveStatus}
                  onChange={(e) => onChange({ ...filters, liveStatus: e.target.value }, true)}
                  className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 px-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
                >
                  <option value="">All live statuses</option>
                  <option value="ontime">On time</option>
                  <option value="delayed">Delayed (&gt; 2 min)</option>
                  <option value="early">Early (&gt; 2 min)</option>
                </select>
              </>
            )}

            {activeTab === "recent" && (
              <>
                <label
                  htmlFor="tracking-filter-recent-status"
                  className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
                >
                  Journey status
                </label>
                <select
                  id="tracking-filter-recent-status"
                  value={filters.recentStatus}
                  onChange={(e) => onChange({ ...filters, recentStatus: e.target.value }, true)}
                  className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 px-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring"
                >
                  <option value="">Completed &amp; cancelled</option>
                  <option value="Completed">Completed only</option>
                  <option value="Cancelled">Cancelled only</option>
                </select>
              </>
            )}

            {activeTab === "upcoming" && (
              <>
                <label
                  htmlFor="tracking-filter-days"
                  className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-foreground"
                >
                  Schedule window
                </label>
                <select
                  id="tracking-filter-days"
                  value={filters.date ? "custom" : filters.daysAhead}
                  disabled={Boolean(filters.date)}
                  onChange={(e) => onChange({ ...filters, daysAhead: e.target.value }, true)}
                  className="h-10 w-full min-w-0 truncate rounded-xl border border-input bg-background/60 px-3 text-sm text-ink outline-none transition focus-visible:border-primary/60 focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {filters.date && <option value="custom">Selected date only</option>}
                  <option value="0">Today only</option>
                  <option value="2">Next 3 days</option>
                  <option value="6">Next 7 days</option>
                  <option value="13">Next 14 days</option>
                </select>
              </>
            )}
          </div>
        </div>
      )}

      {/* Filter Toolbar: More filters toggle, active filter chips, Clear all, and result count */}
      <div className="mt-3 flex flex-wrap items-center justify-between gap-2 border-t border-border/50 pt-3">
        <div className="flex flex-wrap items-center gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            aria-expanded={showMore}
            aria-controls="tracking-more-filters"
            onClick={() => setShowMore((prev) => !prev)}
            className={cn(
              "h-8 rounded-full px-3 text-xs",
              (showMore || secondaryCount > 0) && "border-primary/40 bg-primary/5 text-primary",
            )}
          >
            <SlidersHorizontal className="h-3.5 w-3.5" aria-hidden="true" />
            More filters
            {secondaryCount > 0 && (
              <span className="ml-0.5 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[10px] font-bold text-primary-foreground">
                {secondaryCount}
              </span>
            )}
            {showMore ? (
              <ChevronUp className="h-3.5 w-3.5" aria-hidden="true" />
            ) : (
              <ChevronDown className="h-3.5 w-3.5" aria-hidden="true" />
            )}
          </Button>

          {activeChips.map((chip) => (
            <span
              key={chip.key}
              className="inline-flex items-center gap-1 rounded-full border border-primary/25 bg-primary/10 py-1 pl-2.5 pr-1.5 text-xs font-medium text-primary"
            >
              <span className="max-w-[200px] truncate">{chip.label}</span>
              <button
                type="button"
                aria-label={`Remove ${chip.label} filter`}
                onClick={chip.onRemove}
                className="rounded-full p-0.5 hover:bg-primary/15"
              >
                <X className="h-3 w-3" aria-hidden="true" />
              </button>
            </span>
          ))}

          {hasFilters && (
            <button
              type="button"
              onClick={onClearAll}
              className="inline-flex items-center gap-1 rounded-full px-2 py-1 text-xs font-medium text-muted-foreground transition-colors hover:text-primary"
            >
              <RotateCcw className="h-3 w-3" aria-hidden="true" />
              Clear all
            </button>
          )}
        </div>

        <p className="text-xs text-muted-foreground" aria-live="polite">
          {loading ? "Updating results…" : resultSummary}
        </p>
      </div>
    </div>
  );
}
