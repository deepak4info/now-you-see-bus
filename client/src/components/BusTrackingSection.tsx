import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ArrowRight, BusFront, CalendarClock, Clock, History, MapPin, Navigation, RefreshCw, Route as RouteIcon, Share2 } from "lucide-react";
import { Link } from "react-router-dom";
import { shareLiveJourney, trackingItemToShareTarget } from "@/components/LiveJourneyPanel";
import {
  BusTrackingFilters,
  DEFAULT_TRACKING_FILTERS,
  buildTrackingFilterParams,
  hasActiveTrackingFilters,
  matchesTrackingItemFilters,
  type TrackingFilterValues,
  type TrackingTabKey,
} from "@/components/BusTrackingFilters";
import { StatusBadge } from "@/components/StatusBadge";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { ago, formatClock, formatEta, getIndiaDate } from "@/lib/format";
import type {
  BusTrackingItem,
  PaginatedBusTracking,
  StopOnTrip,
  TrackSnapshot,
  TrackingFilterOptions,
} from "@/lib/types";
import { cn } from "@/lib/utils";

export type { TrackingTabKey };

const PAGE_SIZE = 8;
const FILTER_FALLBACK_PAGE_SIZE = 50;

const EMPTY_FILTER_OPTIONS: TrackingFilterOptions = {
  operators: [],
  routes: [],
  busTypes: [],
};

type BusTrackingSectionProps = {
  liveUpdate?: TrackSnapshot | null;
  liveUpdatesError?: string;
  onFindBusClick?: () => void;
};

export function BusTrackingSection({
  liveUpdate,
  liveUpdatesError,
  onFindBusClick,
}: BusTrackingSectionProps) {
  const [activeTab, setActiveTab] = useState<TrackingTabKey>("live");

  const [filters, setFilters] = useState<TrackingFilterValues>(DEFAULT_TRACKING_FILTERS);
  const [debouncedText, setDebouncedText] = useState({ busNumber: "", stop: "" });
  const debounceTimerRef = useRef<number | null>(null);

  const [serverFilterOptions, setServerFilterOptions] = useState<TrackingFilterOptions | null>(null);
  const [serverFilterSupport, setServerFilterSupport] = useState(false);
  const [fallbackFilterOptions, setFallbackFilterOptions] = useState<TrackingFilterOptions>(EMPTY_FILTER_OPTIONS);

  const [liveItems, setLiveItems] = useState<BusTrackingItem[]>([]);
  const [liveUsedFallback, setLiveUsedFallback] = useState(false);
  const [liveLoading, setLiveLoading] = useState(true);
  const [liveError, setLiveError] = useState("");
  const liveSnapshotStopsRef = useRef<Map<string, StopOnTrip[]>>(new Map());

  const [recentItems, setRecentItems] = useState<BusTrackingItem[]>([]);
  const [recentPage, setRecentPage] = useState(1);
  const [recentTotal, setRecentTotal] = useState(0);
  const [recentHasMore, setRecentHasMore] = useState(false);
  const [recentLoading, setRecentLoading] = useState(false);
  const [recentLoadingMore, setRecentLoadingMore] = useState(false);
  const [recentError, setRecentError] = useState("");

  const [upcomingItems, setUpcomingItems] = useState<BusTrackingItem[]>([]);
  const [upcomingPage, setUpcomingPage] = useState(1);
  const [upcomingTotal, setUpcomingTotal] = useState(0);
  const [upcomingHasMore, setUpcomingHasMore] = useState(false);
  const [upcomingLoading, setUpcomingLoading] = useState(false);
  const [upcomingLoadingMore, setUpcomingLoadingMore] = useState(false);
  const [upcomingError, setUpcomingError] = useState("");

  const loadedKeysRef = useRef<{ live: string | null; recent: string | null; upcoming: string | null }>({
    live: null,
    recent: null,
    upcoming: null,
  });
  const liveReqIdRef = useRef(0);
  const recentReqIdRef = useRef(0);
  const upcomingReqIdRef = useRef(0);

  // Minute ticker keeps Upcoming departures accurate and refreshes ago() timestamps on Live cards.
  const [clockTick, setClockTick] = useState(0);
  useEffect(() => {
    const timer = window.setInterval(() => setClockTick((t) => t + 1), 30000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => () => {
    if (debounceTimerRef.current !== null) {
      window.clearTimeout(debounceTimerRef.current);
    }
  }, []);

  const handleFilterChange = useCallback((next: TrackingFilterValues, immediate = false) => {
    setFilters(next);
    if (debounceTimerRef.current !== null) {
      window.clearTimeout(debounceTimerRef.current);
      debounceTimerRef.current = null;
    }
    if (immediate) {
      setDebouncedText({ busNumber: next.busNumber, stop: next.stop });
      return;
    }
    debounceTimerRef.current = window.setTimeout(() => {
      debounceTimerRef.current = null;
      setDebouncedText({ busNumber: next.busNumber, stop: next.stop });
    }, 280);
  }, []);

  const handleClearFilters = useCallback(() => {
    handleFilterChange(DEFAULT_TRACKING_FILTERS, true);
  }, [handleFilterChange]);

  const appliedFilters = useMemo<TrackingFilterValues>(
    () => ({
      ...filters,
      busNumber: debouncedText.busNumber,
      stop: debouncedText.stop,
    }),
    [filters, debouncedText],
  );

  const liveQueryString = useMemo(
    () => buildTrackingFilterParams(appliedFilters, "live").toString(),
    [appliedFilters],
  );
  const recentQueryString = useMemo(
    () => buildTrackingFilterParams(appliedFilters, "recent").toString(),
    [appliedFilters],
  );
  const upcomingQueryString = useMemo(
    () => buildTrackingFilterParams(appliedFilters, "upcoming").toString(),
    [appliedFilters],
  );

  // Fetch authoritative filter dropdown metadata once on mount.
  useEffect(() => {
    let active = true;
    api<TrackingFilterOptions>("/api/tracking/filters")
      .then((data) => {
        if (!active || !data) return;
        setServerFilterSupport(true);
        setServerFilterOptions({
          operators: Array.isArray(data.operators) ? data.operators : [],
          routes: Array.isArray(data.routes) ? data.routes : [],
          busTypes: Array.isArray(data.busTypes) ? data.busTypes : [],
        });
      })
      .catch(() => {
        if (!active) return;
        setServerFilterSupport(false);
        // Fallback options are derived from loaded items below if unavailable.
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (serverFilterOptions) return;
    const allItems = [...liveItems, ...recentItems, ...upcomingItems];
    if (allItems.length === 0) return;
    setFallbackFilterOptions((prev) => mergeFilterOptionsFromItems(prev, allItems));
  }, [serverFilterOptions, liveItems, recentItems, upcomingItems]);

  const filterOptions = serverFilterOptions ?? fallbackFilterOptions;

  const recentClientFiltered = !serverFilterOptions && hasActiveTrackingFilters(filters, "recent");
  const upcomingClientFiltered = !serverFilterOptions && hasActiveTrackingFilters(filters, "upcoming");

  const recentFetchSize = !serverFilterOptions && hasActiveTrackingFilters(appliedFilters, "recent")
    ? FILTER_FALLBACK_PAGE_SIZE
    : PAGE_SIZE;
  const upcomingFetchSize = !serverFilterOptions && hasActiveTrackingFilters(appliedFilters, "upcoming")
    ? FILTER_FALLBACK_PAGE_SIZE
    : PAGE_SIZE;

  const loadLive = useCallback(async (queryString: string) => {
    const reqId = ++liveReqIdRef.current;
    loadedKeysRef.current.live = queryString;
    setLiveLoading(true);
    setLiveError("");
    const suffix = queryString ? `?${queryString}` : "";
    try {
      const data = await api<BusTrackingItem[]>(`/api/tracking/live${suffix}`);
      if (reqId !== liveReqIdRef.current) return;
      liveSnapshotStopsRef.current.clear();
      setLiveUsedFallback(false);
      setLiveItems(Array.isArray(data) ? data.filter((item) => item.status === "Running") : []);
    } catch {
      if (reqId !== liveReqIdRef.current) return;
      try {
        const legacyLive = await api<TrackSnapshot[]>("/api/live");
        if (reqId !== liveReqIdRef.current) return;
        const stopsMap = new Map<string, StopOnTrip[]>();
        const mapped = Array.isArray(legacyLive)
          ? legacyLive
              .filter((item) => item.status === "Running")
              .map((snap) => {
                const trackingItem = snapshotToTrackingItem(snap);
                stopsMap.set(getLiveItemKey(trackingItem), snap.stops ?? []);
                return trackingItem;
              })
          : [];
        liveSnapshotStopsRef.current = stopsMap;
        setLiveUsedFallback(true);
        setLiveItems(mapped);
      } catch (err) {
        if (reqId !== liveReqIdRef.current) return;
        setLiveError(err instanceof Error ? err.message : "Could not load live bus updates.");
      }
    } finally {
      if (reqId === liveReqIdRef.current) {
        setLiveLoading(false);
      }
    }
  }, []);

  const loadRecent = useCallback(async (page: number, append: boolean, queryString: string, pageSize = PAGE_SIZE) => {
    const reqId = ++recentReqIdRef.current;
    const loadKey = `${queryString}|ps=${pageSize}`;
    if (!append) loadedKeysRef.current.recent = loadKey;
    if (append) {
      setRecentLoadingMore(true);
    } else {
      setRecentLoadingMore(false);
      setRecentLoading(true);
    }
    setRecentError("");
    try {
      const params = new URLSearchParams(queryString);
      params.set("page", String(page));
      params.set("pageSize", String(pageSize));
      const res = await api<PaginatedBusTracking>(`/api/tracking/recent?${params.toString()}`);
      if (reqId !== recentReqIdRef.current) return;
      const rawItems = Array.isArray(res?.items) ? res.items : [];
      const clean = rawItems.filter((item) => item.status === "Completed" || item.status === "Cancelled");
      setRecentItems((prev) => (append ? mergeUniqueByTrip(prev, clean) : clean));
      setRecentPage(res.page ?? page);
      setRecentTotal(res.totalCount ?? clean.length);
      setRecentHasMore(Boolean(res.hasMore));
    } catch (err) {
      if (reqId !== recentReqIdRef.current) return;
      setRecentError(err instanceof Error ? err.message : "Could not load recent bus journeys.");
    } finally {
      if (reqId === recentReqIdRef.current) {
        setRecentLoading(false);
        setRecentLoadingMore(false);
      }
    }
  }, []);

  const loadUpcoming = useCallback(async (page: number, append: boolean, queryString: string, pageSize = PAGE_SIZE) => {
    const reqId = ++upcomingReqIdRef.current;
    const loadKey = `${queryString}|ps=${pageSize}`;
    if (!append) loadedKeysRef.current.upcoming = loadKey;
    if (append) {
      setUpcomingLoadingMore(true);
    } else {
      setUpcomingLoadingMore(false);
      setUpcomingLoading(true);
    }
    setUpcomingError("");
    try {
      const params = new URLSearchParams(queryString);
      params.set("page", String(page));
      params.set("pageSize", String(pageSize));
      const res = await api<PaginatedBusTracking>(`/api/tracking/upcoming?${params.toString()}`);
      if (reqId !== upcomingReqIdRef.current) return;
      const rawItems = Array.isArray(res?.items) ? res.items : [];
      const clean = rawItems.filter((item) => item.status === "Scheduled");
      setUpcomingItems((prev) => (append ? mergeUniqueUpcoming(prev, clean) : clean));
      setUpcomingPage(res.page ?? page);
      setUpcomingTotal(res.totalCount ?? clean.length);
      setUpcomingHasMore(Boolean(res.hasMore));
    } catch (err) {
      if (reqId !== upcomingReqIdRef.current) return;
      setUpcomingError(err instanceof Error ? err.message : "Could not load upcoming scheduled buses.");
    } finally {
      if (reqId === upcomingReqIdRef.current) {
        setUpcomingLoading(false);
        setUpcomingLoadingMore(false);
      }
    }
  }, []);

  // Load the active tab whenever its filter query changes or the user switches tabs with stale data.
  useEffect(() => {
    const recentLoadKey = `${recentQueryString}|ps=${recentFetchSize}`;
    const upcomingLoadKey = `${upcomingQueryString}|ps=${upcomingFetchSize}`;
    if (activeTab === "live" && loadedKeysRef.current.live !== liveQueryString) {
      void loadLive(liveQueryString);
    } else if (activeTab === "recent" && loadedKeysRef.current.recent !== recentLoadKey) {
      void loadRecent(1, false, recentQueryString, recentFetchSize);
    } else if (activeTab === "upcoming" && loadedKeysRef.current.upcoming !== upcomingLoadKey) {
      void loadUpcoming(1, false, upcomingQueryString, upcomingFetchSize);
    }
  }, [
    activeTab,
    liveQueryString,
    recentQueryString,
    upcomingQueryString,
    recentFetchSize,
    upcomingFetchSize,
    loadLive,
    loadRecent,
    loadUpcoming,
  ]);

  // Apply real-time SignalR location & status updates seamlessly across Live / Upcoming / Recent.
  useEffect(() => {
    if (!liveUpdate) return;
    const item = snapshotToTrackingItem(liveUpdate);
    const key = getLiveItemKey(item);

    if (item.status === "Running") {
      if (liveUpdate.stops?.length) {
        liveSnapshotStopsRef.current.set(key, liveUpdate.stops);
      }
      setLiveItems((current) => {
        const index = current.findIndex((row) =>
          (item.tripId != null && row.tripId === item.tripId)
          || (row.fleetNumber === item.fleetNumber && row.routeId === item.routeId),
        );
        if (index < 0) return [item, ...current];
        const next = current.slice();
        next[index] = item;
        return next;
      });

      // Automatically remove a newly started trip from Upcoming when its status transitions to Running.
      let removedFromUpcoming = false;
      setUpcomingItems((current) => {
        const filtered = current.filter((row) => {
          if (item.tripId != null && row.tripId === item.tripId) return false;
          if (item.scheduleId != null && row.scheduleId === item.scheduleId && row.serviceDate === item.serviceDate) return false;
          return true;
        });
        removedFromUpcoming = filtered.length !== current.length;
        return filtered;
      });
      if (removedFromUpcoming) {
        setUpcomingTotal((total) => Math.max(0, total - 1));
      }
      return;
    }

    if (item.status === "Completed" || item.status === "Cancelled") {
      liveSnapshotStopsRef.current.delete(key);
      setLiveItems((current) =>
        current.filter((row) =>
          !((item.tripId != null && row.tripId === item.tripId)
            || (row.fleetNumber === item.fleetNumber && row.routeId === item.routeId)),
        ),
      );
      if (activeTab === "recent") {
        void loadRecent(1, false, recentQueryString, recentFetchSize);
      } else {
        loadedKeysRef.current.recent = null;
      }
    }
  }, [liveUpdate, activeTab, recentQueryString, recentFetchSize, loadRecent]);

  // Apply instant client-side filters to Live items (handles live typing + SignalR pushes + /api/live fallback).
  const visibleLive = useMemo(() => {
    return liveItems.filter((item) => {
      const extraStops = liveSnapshotStopsRef.current.get(getLiveItemKey(item));
      return matchesTrackingItemFilters(item, filters, filterOptions, "live", {
        checkStop: !serverFilterSupport || liveUsedFallback || Boolean(extraStops),
        extraStops,
      });
    });
  }, [liveItems, filters, filterOptions, liveUsedFallback, serverFilterSupport]);

  // Apply instant client-side filters to Recent items (handles live typing + un-updated backend fallback).
  const visibleRecent = useMemo(() => {
    const checkStop = !serverFilterSupport;
    return recentItems.filter((item) =>
      matchesTrackingItemFilters(item, filters, filterOptions, "recent", { checkStop }),
    );
  }, [recentItems, filters, filterOptions, serverFilterSupport]);

  // Defensively hide any Upcoming departures whose scheduled time has passed since last fetch,
  // keep Upcoming entries excluded if they are currently active in Live, and apply instant client-side filters.
  const visibleUpcoming = useMemo(() => {
    void clockTick;
    const nowIst = getIndiaClockNow();
    const checkStop = !serverFilterSupport;
    return upcomingItems.filter((item) => {
      if (item.status !== "Scheduled") return false;
      if (!isAfterCurrentIst(item.serviceDate, item.departureTime, nowIst)) return false;
      const isCurrentlyLive = liveItems.some((live) =>
        (item.tripId != null && live.tripId === item.tripId)
        || (item.scheduleId != null && live.scheduleId === item.scheduleId && live.serviceDate === item.serviceDate),
      );
      if (isCurrentlyLive) return false;
      return matchesTrackingItemFilters(item, filters, filterOptions, "upcoming", { checkStop });
    });
  }, [upcomingItems, liveItems, clockTick, filters, filterOptions, serverFilterSupport]);

  const tabs: { id: TrackingTabKey; label: string }[] = [
    { id: "live", label: "Live" },
    { id: "recent", label: "Recent" },
    { id: "upcoming", label: "Upcoming" },
  ];

  const handleRefreshActive = () => {
    if (activeTab === "live") void loadLive(liveQueryString);
    else if (activeTab === "recent") void loadRecent(1, false, recentQueryString, recentFetchSize);
    else void loadUpcoming(1, false, upcomingQueryString, upcomingFetchSize);
  };

  const hasFiltersForActiveTab = hasActiveTrackingFilters(filters, activeTab);

  const activeLoading =
    activeTab === "live"
      ? liveLoading
      : activeTab === "recent"
        ? recentLoading
        : upcomingLoading;

  const resultSummary = useMemo(() => {
    if (activeTab === "live") {
      if (liveError) return "";
      return `${visibleLive.length} live ${visibleLive.length === 1 ? "bus" : "buses"}`;
    }
    if (activeTab === "recent") {
      if (recentError) return "";
      if (recentClientFiltered) {
        if (visibleRecent.length === 0) return recentLoading ? "" : "0 matching recent journeys";
        return `${visibleRecent.length} matching recent ${visibleRecent.length === 1 ? "journey" : "journeys"}`;
      }
      if (visibleRecent.length === 0 && recentTotal === 0) return recentLoading ? "" : "0 recent journeys";
      const total = Math.max(visibleRecent.length, recentTotal);
      return `Showing ${visibleRecent.length} of ${total} recent ${total === 1 ? "journey" : "journeys"}`;
    }
    if (upcomingError) return "";
    if (upcomingClientFiltered) {
      if (visibleUpcoming.length === 0) return upcomingLoading ? "" : "0 matching upcoming departures";
      return `${visibleUpcoming.length} matching upcoming ${visibleUpcoming.length === 1 ? "departure" : "departures"}`;
    }
    if (visibleUpcoming.length === 0 && upcomingTotal === 0) {
      return upcomingLoading ? "" : "0 upcoming departures";
    }
    return `Showing ${visibleUpcoming.length} of ${Math.max(visibleUpcoming.length, upcomingTotal)} upcoming departures`;
  }, [
    activeTab,
    liveError,
    visibleLive.length,
    recentError,
    recentClientFiltered,
    recentTotal,
    recentLoading,
    visibleRecent.length,
    upcomingError,
    upcomingClientFiltered,
    visibleUpcoming.length,
    upcomingTotal,
    upcomingLoading,
  ]);

  return (
    <section className="order-4 lg:col-span-2 lg:row-start-3" aria-labelledby="latest-bus-updates">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[0.18em] text-pine">Live service information</p>
          <h2 id="latest-bus-updates" className="mt-1 font-display text-3xl sm:text-4xl">Bus tracking &amp; schedules</h2>
          <p className="mt-1 text-sm text-muted-foreground">Track live road departures, review recent trips, or preview upcoming buses.</p>
        </div>
        <div className="flex items-center gap-2">
          <span className="flex items-center gap-2 rounded-full border bg-card px-3 py-1.5 text-xs font-medium text-pine">
            <span className="pulse" /> Live updates
          </span>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleRefreshActive}
            aria-label="Refresh bus tracking list"
            className="text-muted-foreground hover:text-ink"
          >
            <RefreshCw className={cn("h-4 w-4", activeLoading && "animate-spin")} aria-hidden="true" />
          </Button>
        </div>
      </div>

      {/* Minimal Tab Navigation matching Live | Recent | Upcoming */}
      <div className="mt-6 border-b border-border/80">
        <div
          role="tablist"
          aria-label="Bus tracking tabs"
          className="flex flex-wrap items-center gap-7 sm:gap-10"
        >
          {tabs.map((tab) => {
            const isActive = activeTab === tab.id;
            return (
              <button
                key={tab.id}
                type="button"
                role="tab"
                id={`tracking-tab-${tab.id}`}
                aria-selected={isActive}
                aria-controls={`tracking-panel-${tab.id}`}
                onClick={() => setActiveTab(tab.id)}
                className={cn(
                  "relative shrink-0 whitespace-nowrap pb-3 pt-1 text-[16px] tracking-tight transition-colors duration-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring sm:text-[17px]",
                  isActive
                    ? "font-semibold text-primary"
                    : "font-medium text-ink hover:text-primary/80",
                )}
              >
                {tab.label}
                <span
                  aria-hidden="true"
                  className={cn(
                    "absolute inset-x-0 bottom-0 h-[2.5px] rounded-full bg-primary transition-all duration-200 ease-out",
                    isActive ? "scale-x-100 opacity-100" : "scale-x-0 opacity-0",
                  )}
                />
              </button>
            );
          })}
        </div>
      </div>

      <BusTrackingFilters
        activeTab={activeTab}
        filters={filters}
        options={filterOptions}
        loading={activeLoading}
        resultSummary={resultSummary}
        onChange={handleFilterChange}
        onClearAll={handleClearFilters}
      />

      {liveUpdatesError && activeTab === "live" && (
        <p className="mt-3 text-sm text-destructive">Live updates disconnected: {liveUpdatesError}</p>
      )}

      {/* LIVE TAB */}
      {activeTab === "live" && (
        <div
          key="panel-live"
          id="tracking-panel-live"
          role="tabpanel"
          aria-labelledby="tracking-tab-live"
          className="tab-panel-animate mt-5"
        >
          {liveLoading && visibleLive.length === 0 && (
            <p className="text-sm text-muted-foreground">Loading currently running buses…</p>
          )}
          {liveError && (
            <div className="rounded-xl border border-destructive/30 bg-card p-4 text-sm text-destructive">
              <p>{liveError}</p>
              <Button className="mt-3" size="sm" variant="outline" onClick={() => void loadLive(liveQueryString)}>
                Try again
              </Button>
            </div>
          )}
          {!liveLoading && !liveError && visibleLive.length === 0 && (
            <div className="flex flex-col gap-4 rounded-2xl border bg-card p-5 shadow-sm sm:flex-row sm:items-center sm:p-6">
              <span className="grid h-12 w-12 shrink-0 place-items-center rounded-2xl bg-secondary text-secondary-foreground">
                <BusFront size={22} aria-hidden="true" />
              </span>
              <div className="min-w-0 flex-1">
                <h3 className="font-semibold text-ink">
                  {hasFiltersForActiveTab ? "No live buses match your filters" : "No buses are currently running"}
                </h3>
                <p className="mt-1 text-sm leading-6 text-muted-foreground">
                  {hasFiltersForActiveTab
                    ? "Try clearing or adjusting your bus number, route, operator, date, or other filters."
                    : "No live GPS-active buses are on the road right now. Check the Upcoming tab for scheduled departures or plan a journey."}
                </p>
              </div>
              <div className="flex flex-wrap gap-2">
                {hasFiltersForActiveTab && (
                  <Button variant="outline" onClick={handleClearFilters}>
                    Clear filters
                  </Button>
                )}
                <Button variant="outline" onClick={() => setActiveTab("upcoming")}>
                  View Upcoming <ArrowRight size={16} aria-hidden="true" />
                </Button>
                {onFindBusClick && !hasFiltersForActiveTab && (
                  <Button variant="ghost" onClick={onFindBusClick}>
                    Plan journey
                  </Button>
                )}
              </div>
            </div>
          )}
          {visibleLive.length > 0 && (
            <div className="grid gap-4 md:grid-cols-2">
              {visibleLive.map((item) => (
                <LiveBusCard key={`live-${getLiveItemKey(item)}`} item={item} />
              ))}
            </div>
          )}
        </div>
      )}

      {/* RECENT TAB */}
      {activeTab === "recent" && (
        <div
          key="panel-recent"
          id="tracking-panel-recent"
          role="tabpanel"
          aria-labelledby="tracking-tab-recent"
          className="tab-panel-animate mt-5"
        >
          {recentLoading && visibleRecent.length === 0 && (
            <p className="text-sm text-muted-foreground">Loading recently completed journeys…</p>
          )}
          {recentError && (
            <div className="rounded-xl border border-destructive/30 bg-card p-4 text-sm text-destructive">
              <p>{recentError}</p>
              <Button className="mt-3" size="sm" variant="outline" onClick={() => void loadRecent(1, false, recentQueryString, recentFetchSize)}>
                Try again
              </Button>
            </div>
          )}
          {!recentLoading && !recentError && visibleRecent.length === 0 && (
            <div className="flex flex-col gap-4 rounded-2xl border bg-card p-5 shadow-sm sm:flex-row sm:items-center sm:p-6">
              <span className="grid h-12 w-12 shrink-0 place-items-center rounded-2xl bg-secondary text-secondary-foreground">
                <History size={22} aria-hidden="true" />
              </span>
              <div className="min-w-0 flex-1">
                <h3 className="font-semibold text-ink">
                  {hasFiltersForActiveTab ? "No recent journeys match your filters" : "No recent journeys recorded yet"}
                </h3>
                <p className="mt-1 text-sm leading-6 text-muted-foreground">
                  {hasFiltersForActiveTab
                    ? "Try choosing another date, route, operator, or clearing active filters."
                    : "Completed and cancelled trips will appear here once bus services finish their journeys."}
                </p>
              </div>
              <div className="flex flex-wrap gap-2">
                {hasFiltersForActiveTab && (
                  <Button variant="outline" onClick={handleClearFilters}>
                    Clear filters
                  </Button>
                )}
                <Button variant="outline" onClick={() => setActiveTab("upcoming")}>
                  View Upcoming <ArrowRight size={16} aria-hidden="true" />
                </Button>
              </div>
            </div>
          )}
          {visibleRecent.length > 0 && (
            <>
              <div className="grid gap-4 md:grid-cols-2">
                {visibleRecent.map((item) => (
                  <RecentBusCard key={`recent-${item.tripId ?? `${item.fleetNumber}-${item.serviceDate}`}`} item={item} />
                ))}
              </div>
              <PaginationFooter
                shown={visibleRecent.length}
                total={recentClientFiltered ? visibleRecent.length : Math.max(visibleRecent.length, recentTotal)}
                hasMore={!recentClientFiltered && recentHasMore}
                loading={recentLoadingMore}
                label="recent journeys"
                onLoadMore={() => void loadRecent(recentPage + 1, true, recentQueryString, recentFetchSize)}
              />
            </>
          )}
        </div>
      )}

      {/* UPCOMING TAB */}
      {activeTab === "upcoming" && (
        <div
          key="panel-upcoming"
          id="tracking-panel-upcoming"
          role="tabpanel"
          aria-labelledby="tracking-tab-upcoming"
          className="tab-panel-animate mt-5"
        >
          {upcomingLoading && visibleUpcoming.length === 0 && (
            <p className="text-sm text-muted-foreground">Loading upcoming scheduled buses…</p>
          )}
          {upcomingError && (
            <div className="rounded-xl border border-destructive/30 bg-card p-4 text-sm text-destructive">
              <p>{upcomingError}</p>
              <Button className="mt-3" size="sm" variant="outline" onClick={() => void loadUpcoming(1, false, upcomingQueryString, upcomingFetchSize)}>
                Try again
              </Button>
            </div>
          )}
          {!upcomingLoading && !upcomingError && visibleUpcoming.length === 0 && (
            <div className="flex flex-col gap-4 rounded-2xl border bg-card p-5 shadow-sm sm:flex-row sm:items-center sm:p-6">
              <span className="grid h-12 w-12 shrink-0 place-items-center rounded-2xl bg-secondary text-secondary-foreground">
                <CalendarClock size={22} aria-hidden="true" />
              </span>
              <div className="min-w-0 flex-1">
                <h3 className="font-semibold text-ink">
                  {hasFiltersForActiveTab ? "No upcoming departures match your filters" : "No upcoming departures scheduled"}
                </h3>
                <p className="mt-1 text-sm leading-6 text-muted-foreground">
                  {hasFiltersForActiveTab
                    ? "Try expanding your schedule window, picking another date, or clearing filters."
                    : "There are no remaining scheduled departures for the upcoming days. Try searching a route directly."}
                </p>
              </div>
              <div className="flex flex-wrap gap-2">
                {hasFiltersForActiveTab && (
                  <Button variant="outline" onClick={handleClearFilters}>
                    Clear filters
                  </Button>
                )}
                {onFindBusClick && (
                  <Button variant="outline" onClick={onFindBusClick}>
                    Search routes <ArrowRight size={16} aria-hidden="true" />
                  </Button>
                )}
              </div>
            </div>
          )}
          {visibleUpcoming.length > 0 && (
            <>
              <div className="grid gap-4 md:grid-cols-2">
                {visibleUpcoming.map((item) => (
                  <UpcomingBusCard
                    key={`upcoming-${item.scheduleId ?? item.busId}-${item.serviceDate}-${item.departureTime}`}
                    item={item}
                  />
                ))}
              </div>
              <PaginationFooter
                shown={visibleUpcoming.length}
                total={upcomingClientFiltered ? visibleUpcoming.length : Math.max(visibleUpcoming.length, upcomingTotal)}
                hasMore={!upcomingClientFiltered && upcomingHasMore}
                loading={upcomingLoadingMore}
                label="upcoming departures"
                onLoadMore={() => void loadUpcoming(upcomingPage + 1, true, upcomingQueryString, upcomingFetchSize)}
              />
            </>
          )}
        </div>
      )}
    </section>
  );
}

function getLiveItemKey(item: BusTrackingItem) {
  return String(item.tripId ?? `${item.fleetNumber}-${item.routeId}`);
}

function CardMetaBadges({ item, showLiveBadge = false }: { item: BusTrackingItem; showLiveBadge?: boolean }) {
  return (
    <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
      {showLiveBadge && (
        <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-500/15 px-2 py-0.5 font-semibold text-emerald-800">
          <span className="h-2 w-2 rounded-full bg-emerald-600 animate-pulse" aria-hidden="true" />
          LIVE
        </span>
      )}
      <span className="rounded bg-primary/10 px-1.5 py-0.5 font-semibold text-primary">Route {item.routeCode}</span>
      {item.busType && (
        <span className="rounded bg-secondary px-1.5 py-0.5 font-medium text-secondary-foreground">{item.busType}</span>
      )}
      <span className="break-words">{item.operatorName}</span>
    </div>
  );
}

function LiveBusCard({ item }: { item: BusTrackingItem }) {
  const trackUrl = `/track/${encodeURIComponent(item.fleetNumber)}?routeId=${item.routeId}${item.tripId ? `&tripId=${item.tripId}` : ""}`;
  const delayText = item.delayMinutes != null && item.delayMinutes > 2
    ? `+${item.delayMinutes} min delayed`
    : item.delayMinutes != null && item.delayMinutes < -2
      ? `${Math.abs(item.delayMinutes)} min early`
      : "On time";

  return (
    <article className="min-w-0 rounded-2xl border border-border/70 bg-card p-4 shadow-xs transition-colors hover:border-primary/40 sm:p-5">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <CardMetaBadges item={item} showLiveBadge />
          <h3 className="mt-2 break-words font-display text-xl leading-snug text-ink sm:text-2xl">
            Bus {item.fleetNumber} · <span className="text-foreground/85">{item.busName}</span>
          </h3>
          <p className="break-words text-xs text-muted-foreground">{item.registrationNumber} · {item.routeName}</p>
        </div>
        <div className="flex flex-col items-end gap-1 shrink-0">
          <StatusBadge status={item.status} />
          {item.delayMinutes != null && (
            <span
              className={cn(
                "text-[11px] font-medium",
                item.delayMinutes > 2 ? "text-red-700" : "text-emerald-700",
              )}
            >
              {delayText}
            </span>
          )}
        </div>
      </div>

      <RouteCorridor origin={item.originStop} destination={item.destinationStop} />

      <dl className="mt-3.5 grid grid-cols-2 gap-3 text-sm">
        <InfoCell
          label="Current location"
          value={item.currentLocation || (item.nextStop ? `Approaching ${item.nextStop}` : "In transit")}
          icon={<Navigation className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />}
        />
        <InfoCell
          label="Next stop"
          value={item.nextStop ? `${item.nextStop}${item.etaMinutes != null ? ` · ${formatEta(item.etaMinutes)}` : ""}` : "Final stop"}
        />
        <InfoCell label="Departure" value={formatClock(item.actualDepartureTime || item.departureTime)} />
        <InfoCell label="Est. arrival" value={formatClock(item.estimatedArrivalTime)} />
      </dl>

      <div className="mt-4 flex flex-wrap items-center justify-between gap-2 border-t border-border/60 pt-3">
        <span className="text-xs text-muted-foreground">
          {item.speedKmph != null ? `${Math.round(item.speedKmph)} km/h · ` : ""}
          GPS update: {ago(item.recordedAt)}
        </span>
        <div className="flex items-center gap-1.5">
          <Button
            size="sm"
            variant="outline"
            onClick={() => void shareLiveJourney(trackingItemToShareTarget(item))}
            title="Share live journey with family"
            className="h-8 px-2.5 text-xs"
          >
            <Share2 className="mr-1 h-3.5 w-3.5" aria-hidden="true" /> Share
          </Button>
          <Button size="sm" asChild className="h-8 text-xs">
            <Link to={trackUrl}>
              View Live Location <ArrowRight className="ml-1 h-4 w-4" aria-hidden="true" />
            </Link>
          </Button>
        </div>
      </div>
    </article>
  );
}

function RecentBusCard({ item }: { item: BusTrackingItem }) {
  const detailsUrl = `/track/${encodeURIComponent(item.fleetNumber)}?routeId=${item.routeId}&date=${item.serviceDate}${item.tripId ? `&tripId=${item.tripId}` : ""}`;
  const isCancelled = item.status === "Cancelled";
  const actualDeparture = item.actualDepartureTime
    ? formatClock(item.actualDepartureTime)
    : (isCancelled ? "Cancelled" : formatClock(item.departureTime));
  const actualArrival = item.actualArrivalTime
    ? formatClock(item.actualArrivalTime)
    : (isCancelled ? "—" : formatClock(item.estimatedArrivalTime));

  return (
    <article className="min-w-0 rounded-2xl border border-border/70 bg-card p-4 shadow-xs transition-colors hover:border-primary/40 sm:p-5">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <CardMetaBadges item={item} />
          <h3 className="mt-2 break-words font-display text-xl leading-snug text-ink sm:text-2xl">
            {item.busName} · <span className="text-foreground/85">Bus {item.fleetNumber}</span>
          </h3>
          <p className="break-words text-xs text-muted-foreground">{item.registrationNumber} · {item.routeName}</p>
        </div>
        <StatusBadge status={item.status} />
      </div>

      <RouteCorridor origin={item.originStop} destination={item.destinationStop} />

      <dl className="mt-3.5 grid grid-cols-2 gap-3 text-sm">
        <InfoCell label="Journey date" value={formatJourneyDate(item.serviceDate)} />
        <InfoCell label="Journey status" value={item.status} />
        <InfoCell label="Actual departure" value={actualDeparture} />
        <InfoCell label="Actual arrival" value={actualArrival} />
      </dl>

      <div className="mt-4 flex flex-wrap items-center justify-end gap-2 border-t border-border/60 pt-3">
        <Button variant="outline" size="sm" asChild>
          <Link to={detailsUrl}>
            View journey details <ArrowRight className="ml-1 h-4 w-4" aria-hidden="true" />
          </Link>
        </Button>
      </div>
    </article>
  );
}

function UpcomingBusCard({ item }: { item: BusTrackingItem }) {
  const params = new URLSearchParams({
    routeId: String(item.routeId),
    date: item.serviceDate,
  });
  if (item.originStop) params.set("from", item.originStop);
  if (item.destinationStop) params.set("to", item.destinationStop);
  const detailsUrl = `/bus/${encodeURIComponent(item.fleetNumber)}?${params.toString()}`;

  return (
    <article className="min-w-0 rounded-2xl border border-border/70 bg-card p-4 shadow-xs transition-colors hover:border-primary/40 sm:p-5">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <CardMetaBadges item={item} />
          <h3 className="mt-2 break-words font-display text-xl leading-snug text-ink sm:text-2xl">
            {item.busName} · <span className="text-foreground/85">Bus {item.fleetNumber}</span>
          </h3>
          <p className="break-words text-xs text-muted-foreground">{item.registrationNumber} · {item.routeName}</p>
        </div>
        <StatusBadge status={item.status} />
      </div>

      <RouteCorridor origin={item.originStop} destination={item.destinationStop} />

      <dl className="mt-3.5 grid grid-cols-2 gap-3 text-sm">
        <InfoCell
          label="Scheduled departure"
          value={formatClock(item.departureTime)}
          icon={<Clock className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />}
        />
        <InfoCell label="Expected arrival" value={formatClock(item.estimatedArrivalTime)} />
        <InfoCell label="Journey date" value={formatJourneyDate(item.serviceDate)} />
        <InfoCell label="Bus operator" value={item.operatorName} />
      </dl>

      <div className="mt-4 flex flex-wrap items-center justify-end gap-2 border-t border-border/60 pt-3">
        <Button variant="outline" size="sm" asChild>
          <Link to={detailsUrl}>
            View schedule &amp; stops <ArrowRight className="ml-1 h-4 w-4" aria-hidden="true" />
          </Link>
        </Button>
      </div>
    </article>
  );
}

function RouteCorridor({ origin, destination }: { origin: string; destination: string }) {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 rounded-xl border border-border/60 bg-muted/35 px-3 py-2 text-sm">
      <MapPin className="h-3.5 w-3.5 shrink-0 text-primary" aria-hidden="true" />
      <span className="min-w-0 break-words font-semibold text-ink">{origin || "Origin"}</span>
      <RouteIcon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
      <span className="min-w-0 break-words font-semibold text-ink">{destination || "Destination"}</span>
    </div>
  );
}

function InfoCell({ label, value, icon }: { label: string; value: string; icon?: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 flex items-center gap-1.5 break-words font-medium text-ink">
        {icon}
        <span className="min-w-0 break-words">{value || "—"}</span>
      </dd>
    </div>
  );
}

function PaginationFooter({
  shown,
  total,
  hasMore,
  loading,
  label,
  onLoadMore,
}: {
  shown: number;
  total: number;
  hasMore: boolean;
  loading: boolean;
  label: string;
  onLoadMore: () => void;
}) {
  return (
    <div className="mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-border/60 pt-4">
      <p className="text-xs text-muted-foreground">
        Showing {shown} of {total} {label}
      </p>
      {hasMore && (
        <Button type="button" variant="outline" size="sm" onClick={onLoadMore} disabled={loading}>
          {loading ? "Loading…" : "Load more"}
        </Button>
      )}
    </div>
  );
}

function snapshotToTrackingItem(s: TrackSnapshot): BusTrackingItem {
  return {
    tripId: s.tripId,
    scheduleId: s.scheduleId,
    busId: s.busId,
    routeId: s.routeId,
    fleetNumber: s.fleetNumber,
    registrationNumber: s.registrationNumber,
    busName: s.busName || s.busType || `Bus ${s.fleetNumber}`,
    busType: s.busType,
    operatorName: s.operatorName,
    routeCode: s.routeCode,
    routeName: s.routeName,
    originStop: s.originStop ?? s.stops[0]?.name ?? "",
    destinationStop: s.destinationStop ?? s.stops.at(-1)?.name ?? "",
    currentLocation: s.currentStop,
    nextStop: s.nextStop,
    etaMinutes: s.etaMinutes,
    departureTime: s.departureTime,
    actualDepartureTime: s.actualDepartureTime ?? null,
    estimatedArrivalTime: s.estimatedArrivalTime ?? s.stops.at(-1)?.estimatedArrival ?? null,
    actualArrivalTime: s.actualArrivalTime ?? s.stops.at(-1)?.actualArrival ?? null,
    serviceDate: s.serviceDate,
    status: s.status,
    delayMinutes: s.delayMinutes,
    delayStatus: s.delayStatus ?? null,
    speedKmph: s.speedKmph ?? null,
    recordedAt: s.recordedAt,
    latitude: s.latitude,
    longitude: s.longitude,
    operationsLive: s.operationsLive,
  };
}

function mergeUniqueByTrip(existing: BusTrackingItem[], incoming: BusTrackingItem[]) {
  const seen = new Set(existing.map((i) => i.tripId ?? `${i.fleetNumber}-${i.serviceDate}-${i.departureTime}`));
  const merged = existing.slice();
  for (const item of incoming) {
    const key = item.tripId ?? `${item.fleetNumber}-${item.serviceDate}-${item.departureTime}`;
    if (!seen.has(key)) {
      seen.add(key);
      merged.push(item);
    }
  }
  return merged;
}

function mergeUniqueUpcoming(existing: BusTrackingItem[], incoming: BusTrackingItem[]) {
  const seen = new Set(existing.map((i) => `${i.scheduleId ?? i.busId}-${i.serviceDate}-${i.departureTime}`));
  const merged = existing.slice();
  for (const item of incoming) {
    const key = `${item.scheduleId ?? item.busId}-${item.serviceDate}-${item.departureTime}`;
    if (!seen.has(key)) {
      seen.add(key);
      merged.push(item);
    }
  }
  return merged;
}

function getIndiaClockNow() {
  const today = getIndiaDate();
  const parts = new Intl.DateTimeFormat("en-GB", {
    timeZone: "Asia/Kolkata",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).formatToParts(new Date());
  const hour = Number(parts.find((p) => p.type === "hour")?.value ?? 0);
  const minute = Number(parts.find((p) => p.type === "minute")?.value ?? 0);
  return { today, minutes: hour * 60 + minute };
}

function isAfterCurrentIst(serviceDate: string, departureTime: string, nowIst: { today: string; minutes: number }) {
  if (serviceDate > nowIst.today) return true;
  if (serviceDate < nowIst.today) return false;
  const [h, m] = departureTime.split(":").map(Number);
  if (!Number.isFinite(h) || !Number.isFinite(m)) return true;
  return h * 60 + m > nowIst.minutes;
}

function formatJourneyDate(dateStr: string) {
  const today = getIndiaDate();
  const base = new Date(`${dateStr}T12:00:00Z`);
  const formatted = new Intl.DateTimeFormat(undefined, {
    weekday: "short",
    day: "numeric",
    month: "short",
    timeZone: "UTC",
  }).format(base);

  if (dateStr === today) return `Today · ${formatted}`;
  const todayDate = new Date(`${today}T12:00:00Z`);
  todayDate.setUTCDate(todayDate.getUTCDate() + 1);
  const tomorrowStr = todayDate.toISOString().slice(0, 10);
  if (dateStr === tomorrowStr) return `Tomorrow · ${formatted}`;
  return formatted;
}

function mergeFilterOptionsFromItems(
  prev: TrackingFilterOptions,
  items: BusTrackingItem[],
): TrackingFilterOptions {
  const opNames = new Set(prev.operators.map((o) => o.name));
  const routeMap = new Map(prev.routes.map((r) => [r.id, r]));
  const busTypeSet = new Set(prev.busTypes);
  let changed = false;

  for (const item of items) {
    if (item.operatorName && !opNames.has(item.operatorName)) {
      opNames.add(item.operatorName);
      changed = true;
    }
    if (item.routeId && !routeMap.has(item.routeId)) {
      routeMap.set(item.routeId, {
        id: item.routeId,
        code: item.routeCode,
        name: item.routeName,
        originStop: item.originStop,
        destinationStop: item.destinationStop,
      });
      changed = true;
    }
    if (item.busType && !busTypeSet.has(item.busType)) {
      busTypeSet.add(item.busType);
      changed = true;
    }
  }

  if (!changed) return prev;
  return {
    operators: Array.from(opNames)
      .sort((a, b) => a.localeCompare(b))
      .map((name) => ({ id: 0, name, code: "" })),
    routes: Array.from(routeMap.values()).sort((a, b) => a.code.localeCompare(b.code)),
    busTypes: Array.from(busTypeSet).sort((a, b) => a.localeCompare(b)),
  };
}
