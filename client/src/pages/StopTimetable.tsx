import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  ArrowLeft,
  ArrowRight,
  BusFront,
  Clock,
  Compass,
  Filter,
  MapPin,
  QrCode,
  Radio,
  RefreshCw,
  Search,
  Share2,
  ShieldCheck,
  Star,
  Users,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { StopQrModal } from "@/components/StopQrModal";
import { api, useQuery } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import type { StopTimetableResponse } from "@/lib/types";
import { cn } from "@/lib/utils";

export function StopTimetablePage() {
  const { code } = useParams<{ code: string }>();
  const { user } = useAuth();

  const [destFilter, setDestFilter] = useState("");
  const [selectedDestChip, setSelectedDestChip] = useState("all");
  const [activeTab, setActiveTab] = useState<"upcoming" | "routes">("upcoming");
  const [qrModalOpen, setQrModalOpen] = useState(false);
  const [favSaved, setFavSaved] = useState(false);
  const [favLoading, setFavLoading] = useState(false);
  const [shareMsg, setShareMsg] = useState("");

  const timetableQuery = useQuery<StopTimetableResponse>(
    code ? `/api/stops/${encodeURIComponent(code)}/timetable` : null,
  );

  const data = timetableQuery.data;

  // Auto-refresh every 30 seconds for live board display
  useEffect(() => {
    const timer = setInterval(() => {
      timetableQuery.reload();
    }, 30_000);
    return () => clearInterval(timer);
  }, [timetableQuery]);

  // Derive unique destinations for quick filter chips
  const popularDestinations = useMemo(() => {
    if (!data) return [];
    const set = new Set<string>();
    data.upcomingBuses.forEach((b) => set.add(b.destinationStop));
    data.routes.forEach((r) => {
      set.add(r.destinationStop);
      r.downstreamStops.slice(0, 3).forEach((s) => set.add(s));
    });
    return Array.from(set).slice(0, 8);
  }, [data]);

  // Filtered upcoming buses
  const filteredBuses = useMemo(() => {
    if (!data) return [];
    return data.upcomingBuses.filter((bus) => {
      if (selectedDestChip !== "all" && bus.destinationStop.toLowerCase() !== selectedDestChip.toLowerCase()) {
        return false;
      }
      if (!destFilter.trim()) return true;
      const q = destFilter.toLowerCase().trim();
      return (
        bus.destinationStop.toLowerCase().includes(q) ||
        bus.routeName.toLowerCase().includes(q) ||
        bus.routeCode.toLowerCase().includes(q) ||
        bus.fleetNumber.toLowerCase().includes(q) ||
        bus.operatorName.toLowerCase().includes(q)
      );
    });
  }, [data, selectedDestChip, destFilter]);

  async function handleSaveStop() {
    if (!data?.stop || favLoading) return;
    setFavLoading(true);
    setShareMsg("");
    try {
      await api("/api/favorites", {
        method: "POST",
        body: JSON.stringify({
          fromStopId: data.stop.id,
          label: `${data.stop.name} (${data.stop.code})`,
        }),
      });
      setFavSaved(true);
      setShareMsg("Stop saved to your Favourites.");
    } catch (err) {
      setShareMsg(err instanceof Error ? err.message : "Could not save stop.");
    } finally {
      setFavLoading(false);
    }
  }

  async function handleShare() {
    setShareMsg("");
    const url = window.location.href;
    const title = `${data?.stop.name ?? "Bus Stand"} Live Timetable & Arrivals`;
    const text = `Check upcoming Haryana Roadways buses & live timings for ${data?.stop.name ?? "this stop"}.`;

    try {
      if (typeof navigator.share === "function") {
        await navigator.share({ title, text, url });
        setShareMsg("Shared successfully.");
        return;
      }
      await navigator.clipboard.writeText(url);
      setShareMsg("Stop link copied to clipboard.");
    } catch (err) {
      if (err instanceof Error && err.name === "AbortError") return;
      setShareMsg("Copy page address from browser to share.");
    }
  }

  if (timetableQuery.loading && !data) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-16 text-center">
        <div className="mx-auto h-10 w-10 animate-spin rounded-full border-3 border-primary border-t-transparent" />
        <p className="mt-4 text-sm font-medium text-muted-foreground animate-pulse">
          Connecting to live bus stand display…
        </p>
      </div>
    );
  }

  if (!data?.stop) {
    return (
      <div className="mx-auto max-w-xl px-4 py-16 text-center">
        <div className="mx-auto grid h-14 w-14 place-items-center rounded-2xl bg-muted text-muted-foreground">
          <MapPin size={28} />
        </div>
        <h1 className="mt-4 font-display text-2xl font-bold">Bus Stop Not Found</h1>
        <p className="mt-2 text-sm text-muted-foreground">
          No bus stand matches stop identifier &quot;{code}&quot;. Scan a valid QR code or search by stop name.
        </p>
        <Button className="mt-6" asChild>
          <Link to="/">
            <ArrowLeft className="mr-2 h-4 w-4" /> Go to Find a Bus
          </Link>
        </Button>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-4xl px-3 py-4 sm:px-6 sm:py-8 space-y-6">
      {/* Top Breadcrumb & Live Banner */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Link
          to="/"
          className="inline-flex items-center gap-1.5 text-xs font-semibold text-muted-foreground hover:text-foreground transition-colors"
        >
          <ArrowLeft size={14} /> Back to Search
        </Link>

        <div className="flex items-center gap-2">
          <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-500/10 px-3 py-1 text-xs font-semibold text-emerald-700 dark:text-emerald-400">
            <Radio className="h-3 w-3 animate-pulse text-emerald-600" />
            Live Stand Display · {data.currentTimeIst} IST
          </span>
          <Button
            variant="ghost"
            size="icon"
            onClick={() => timetableQuery.reload()}
            title="Refresh arrivals now"
            className="h-7 w-7"
          >
            <RefreshCw className={cn("h-3.5 w-3.5", timetableQuery.loading && "animate-spin")} />
          </Button>
        </div>
      </div>

      {/* Hero Stand Card */}
      <div className="rounded-3xl border border-border bg-gradient-to-br from-primary/10 via-card to-accent/40 p-5 sm:p-7 shadow-xs">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div className="space-y-1.5">
            <div className="flex items-center gap-2">
              <span className="grid h-8 w-8 place-items-center rounded-xl bg-primary text-primary-foreground font-bold text-sm shadow-xs">
                🚏
              </span>
              <span className="text-xs font-bold uppercase tracking-wider text-primary">
                Official Bus Stand Timetable
              </span>
            </div>

            <h1 className="font-display text-2xl font-bold text-ink sm:text-3xl leading-snug">
              {data.stop.name}
            </h1>

            <p className="text-xs text-muted-foreground flex flex-wrap items-center gap-2">
              <span className="font-semibold text-ink">Code: {data.stop.code}</span>
              {data.stop.city && <span>• {data.stop.city}</span>}
              {data.stop.address && <span className="hidden sm:inline">• {data.stop.address}</span>}
            </p>
          </div>

          {/* Quick Stand Actions */}
          <div className="flex flex-wrap gap-2 shrink-0">
            <Button
              variant="outline"
              size="sm"
              onClick={() => setQrModalOpen(true)}
              className="gap-1.5 text-xs shadow-xs"
            >
              <QrCode size={14} className="text-primary" />
              Stand QR Poster
            </Button>

            {user && (
              <Button
                variant="outline"
                size="sm"
                onClick={handleSaveStop}
                disabled={favLoading || favSaved}
                className="gap-1.5 text-xs"
              >
                <Star size={14} className={favSaved ? "fill-amber-400 text-amber-400" : ""} />
                {favSaved ? "Saved" : "Save Stand"}
              </Button>
            )}

            <Button
              variant="outline"
              size="sm"
              onClick={handleShare}
              className="gap-1.5 text-xs"
            >
              <Share2 size={14} />
              Share
            </Button>
          </div>
        </div>

        {shareMsg && (
          <p className="mt-3 text-xs font-medium text-primary animate-in fade-in" role="status">
            {shareMsg}
          </p>
        )}

        {/* Quick Stats Badges */}
        <div className="mt-5 grid grid-cols-3 gap-2 sm:gap-4 border-t border-border/60 pt-4 text-center">
          <div className="rounded-xl bg-card/80 p-2 sm:p-3 border">
            <p className="text-lg sm:text-2xl font-bold font-display text-ink">
              {data.stats.upcomingBuses}
            </p>
            <p className="text-[11px] sm:text-xs text-muted-foreground font-medium">Upcoming Buses</p>
          </div>
          <div className="rounded-xl bg-card/80 p-2 sm:p-3 border">
            <p className="text-lg sm:text-2xl font-bold font-display text-ink">
              {data.stats.activeRoutes}
            </p>
            <p className="text-[11px] sm:text-xs text-muted-foreground font-medium">Corridors / Routes</p>
          </div>
          <div className="rounded-xl bg-card/80 p-2 sm:p-3 border">
            <p className="text-lg sm:text-2xl font-bold font-display text-ink">
              {data.stats.totalBusesToday}
            </p>
            <p className="text-[11px] sm:text-xs text-muted-foreground font-medium">Scheduled Today</p>
          </div>
        </div>
      </div>

      {/* Main Content Area */}
      <div className="space-y-4">
        {/* Tab & Filter Bar */}
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex rounded-xl bg-muted/60 p-1 border">
            <button
              type="button"
              onClick={() => setActiveTab("upcoming")}
              className={cn(
                "flex items-center gap-1.5 rounded-lg px-3.5 py-1.5 text-xs font-semibold transition-all cursor-pointer",
                activeTab === "upcoming"
                  ? "bg-card text-ink shadow-xs"
                  : "text-muted-foreground hover:text-ink"
              )}
            >
              <Clock size={14} />
              Live Arrivals ({filteredBuses.length})
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("routes")}
              className={cn(
                "flex items-center gap-1.5 rounded-lg px-3.5 py-1.5 text-xs font-semibold transition-all cursor-pointer",
                activeTab === "routes"
                  ? "bg-card text-ink shadow-xs"
                  : "text-muted-foreground hover:text-ink"
              )}
            >
              <Compass size={14} />
              All Corridors ({data.routes.length})
            </button>
          </div>

          {activeTab === "upcoming" && (
            <div className="relative min-w-[220px]">
              <Search className="absolute left-2.5 top-2.5 h-3.5 w-3.5 text-muted-foreground" />
              <Input
                type="text"
                placeholder="Filter destination, bus #…"
                value={destFilter}
                onChange={(e) => setDestFilter(e.target.value)}
                className="h-8 pl-8 text-xs rounded-xl"
              />
            </div>
          )}
        </div>

        {/* Quick Destination Chips */}
        {activeTab === "upcoming" && popularDestinations.length > 0 && (
          <div className="flex items-center gap-1.5 overflow-x-auto pb-1 text-xs">
            <span className="text-[11px] font-semibold text-muted-foreground uppercase tracking-wider shrink-0 mr-1 flex items-center gap-1">
              <Filter size={12} /> Go To:
            </span>
            <button
              type="button"
              onClick={() => setSelectedDestChip("all")}
              className={cn(
                "rounded-full px-2.5 py-1 text-xs font-medium shrink-0 transition-colors cursor-pointer",
                selectedDestChip === "all"
                  ? "bg-primary text-primary-foreground font-semibold"
                  : "bg-muted hover:bg-muted/80 text-muted-foreground hover:text-ink"
              )}
            >
              All Destinations
            </button>
            {popularDestinations.map((dest) => (
              <button
                key={dest}
                type="button"
                onClick={() => setSelectedDestChip(dest)}
                className={cn(
                  "rounded-full px-2.5 py-1 text-xs font-medium shrink-0 transition-colors cursor-pointer",
                  selectedDestChip.toLowerCase() === dest.toLowerCase()
                    ? "bg-primary text-primary-foreground font-semibold"
                    : "bg-muted hover:bg-muted/80 text-muted-foreground hover:text-ink"
                )}
              >
                {dest}
              </button>
            ))}
          </div>
        )}

        {/* TAB 1: UPCOMING BUSES */}
        {activeTab === "upcoming" && (
          <div className="space-y-3">
            {filteredBuses.length === 0 ? (
              <div className="rounded-2xl border bg-card p-8 text-center">
                <BusFront className="mx-auto h-10 w-10 text-muted-foreground/60" />
                <h3 className="mt-3 font-display text-lg font-semibold">No Departures Match Filter</h3>
                <p className="mt-1 text-xs text-muted-foreground">
                  Try clearing the destination filter or checking all route timetables below.
                </p>
                {(destFilter || selectedDestChip !== "all") && (
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => {
                      setDestFilter("");
                      setSelectedDestChip("all");
                    }}
                    className="mt-4 text-xs"
                  >
                    Clear Filters
                  </Button>
                )}
              </div>
            ) : (
              filteredBuses.map((bus) => (
                <div
                  key={`${bus.scheduleId}-${bus.scheduledTime}`}
                  className={cn(
                    "rounded-2xl border bg-card p-4 sm:p-5 shadow-xs transition-all hover:border-primary/40",
                    bus.isLive && "border-emerald-500/40 bg-emerald-500/5 ring-1 ring-emerald-500/10",
                    bus.status === "Departed" && "opacity-60 bg-muted/20"
                  )}
                >
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                    {/* Time & Destination */}
                    <div className="space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="font-display text-xl sm:text-2xl font-bold text-ink tracking-tight">
                          {bus.scheduledTime}
                        </span>

                        {/* Status Badge */}
                        {bus.status === "Approaching" && (
                          <span className="inline-flex items-center gap-1 rounded-full bg-emerald-500/15 px-2.5 py-0.5 text-xs font-bold text-emerald-700 dark:text-emerald-400">
                            <span className="h-2 w-2 rounded-full bg-emerald-500 animate-ping" />
                            Approaching · {bus.etaMinutes != null ? `${bus.etaMinutes}m ETA` : "Live"}
                          </span>
                        )}
                        {bus.status === "Live" && (
                          <span className="inline-flex items-center gap-1 rounded-full bg-emerald-500/15 px-2.5 py-0.5 text-xs font-bold text-emerald-700 dark:text-emerald-400">
                            <Radio className="h-3 w-3 animate-pulse" /> Live Tracking Active
                          </span>
                        )}
                        {bus.status === "Delayed" && (
                          <span className="inline-flex items-center gap-1 rounded-full bg-amber-500/15 px-2.5 py-0.5 text-xs font-bold text-amber-700 dark:text-amber-400">
                            Delayed by {bus.delayMinutes}m (ETA {bus.estimatedTime})
                          </span>
                        )}
                        {bus.status === "Scheduled" && (
                          <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2.5 py-0.5 text-xs font-medium text-muted-foreground">
                            Scheduled
                          </span>
                        )}
                        {bus.status === "Departed" && (
                          <span className="inline-flex items-center gap-1 rounded-full bg-muted/80 px-2.5 py-0.5 text-xs font-medium text-muted-foreground">
                            Departed
                          </span>
                        )}
                      </div>

                      <div className="flex items-center gap-1.5 text-sm font-semibold text-ink">
                        <span>To</span>
                        <span className="text-primary font-bold text-base sm:text-lg">
                          {bus.destinationStop}
                        </span>
                      </div>

                      <p className="text-xs text-muted-foreground">
                        {bus.routeCode} · {bus.routeName}
                      </p>
                    </div>

                    {/* Bus Info & Action Buttons */}
                    <div className="flex flex-col sm:items-end gap-2 pt-2 sm:pt-0 border-t sm:border-t-0 border-border/60">
                      <div className="text-xs text-muted-foreground sm:text-right">
                        <p className="font-semibold text-ink">
                          {bus.operatorName} · <span className="font-mono">{bus.fleetNumber}</span>
                        </p>
                        {bus.busType && <p className="text-[11px]">{bus.busType}</p>}
                      </div>

                      <div className="flex items-center gap-2">
                        {bus.trackUrl && bus.status !== "Departed" && (
                          <Button size="sm" asChild className="gap-1 text-xs h-8">
                            <Link to={bus.trackUrl}>
                              Track Live <ArrowRight size={13} />
                            </Link>
                          </Button>
                        )}
                        <Button
                          variant="outline"
                          size="sm"
                          asChild
                          className="text-xs h-8"
                        >
                          <Link
                            to={`/search?from=${encodeURIComponent(data.stop.name)}&to=${encodeURIComponent(bus.destinationStop)}&date=${data.serviceDate}`}
                          >
                            All Times
                          </Link>
                        </Button>
                      </div>
                    </div>
                  </div>
                </div>
              ))
            )}
          </div>
        )}

        {/* TAB 2: ALL CORRIDORS SERVING THIS STOP */}
        {activeTab === "routes" && (
          <div className="space-y-3">
            {data.routes.map((route) => (
              <div
                key={route.routeId}
                className="rounded-2xl border bg-card p-4 sm:p-5 shadow-xs space-y-3"
              >
                <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                  <div>
                    <div className="flex items-center gap-2">
                      <span className="rounded-md bg-primary/10 px-2 py-0.5 text-xs font-mono font-bold text-primary">
                        {route.routeCode}
                      </span>
                      <h3 className="font-display text-base font-bold text-ink">
                        {route.routeName}
                      </h3>
                    </div>
                    <p className="mt-1 text-xs text-muted-foreground">
                      {route.operatorName} · {route.dailyTripsCount} departures daily
                    </p>
                  </div>

                  <div className="flex items-center gap-2 text-xs font-medium text-muted-foreground sm:text-right">
                    <div>
                      <p className="text-[11px] uppercase tracking-wide">First / Last Bus</p>
                      <p className="font-semibold text-ink">
                        {route.firstBusTime} → {route.lastBusTime}
                      </p>
                    </div>
                  </div>
                </div>

                {/* Downstream reachable stops */}
                {route.downstreamStops.length > 0 && (
                  <div className="rounded-xl bg-muted/30 p-2.5 text-xs space-y-1">
                    <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground flex items-center gap-1">
                      <MapPin size={11} /> Next Stops from here:
                    </p>
                    <div className="flex flex-wrap items-center gap-1.5 pt-0.5">
                      {route.downstreamStops.map((st, i) => (
                        <span
                          key={st}
                          className="inline-flex items-center gap-1 rounded-md bg-card px-2 py-0.5 text-xs font-medium border text-ink"
                        >
                          {st}
                          {i < route.downstreamStops.length - 1 && (
                            <span className="text-muted-foreground/60 text-[10px]">→</span>
                          )}
                        </span>
                      ))}
                    </div>
                  </div>
                )}

                <div className="flex items-center justify-between pt-1 text-xs">
                  <span className="text-muted-foreground">
                    Stop #{route.stopSequence} on this corridor
                  </span>
                  <Button variant="ghost" size="sm" asChild className="h-7 text-xs gap-1">
                    <Link
                      to={`/routes?routeId=${route.routeId}`}
                    >
                      View Full Corridor <ArrowRight size={12} />
                    </Link>
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* QR Code Poster Modal */}
      {data.stop && (
        <StopQrModal
          stop={data.stop}
          isOpen={qrModalOpen}
          onClose={() => setQrModalOpen(false)}
        />
      )}
    </div>
  );
}
