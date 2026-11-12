import { useEffect, useState } from "react";
import { ArrowDown, ArrowLeft, ArrowRight, BusFront, CalendarDays, MapPin, Share2, Star } from "lucide-react";
import { Link, useSearchParams } from "react-router-dom";
import { JourneyPlannerCard } from "@/components/JourneyPlannerCard";
import { StatusBadge } from "@/components/StatusBadge";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatClock, formatDateWithWeekday, formatEta, formatWhen, getIndiaDate, isDateOnly, rupee } from "@/lib/format";
import { saveRecentSearch } from "@/lib/recentSearch";
import type { SearchResponse, StopOnTrip, StopOption, TrackSnapshot } from "@/lib/types";

export function SearchPage() {
  const { user } = useAuth();
  const [params, setParams] = useSearchParams();
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const dateParam = params.get("date");
  const date = isDateOnly(dateParam) ? dateParam : getIndiaDate();
  const [shareMessage, setShareMessage] = useState("");
  const [savingFav, setSavingFav] = useState(false);
  const stopsQuery = useQuery<StopOption[]>("/api/stops");
  const query = useQuery<SearchResponse>(
    from && to ? `/api/search?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&date=${date}` : null,
  );
  const resolvedFrom = query.data?.from || from;
  const resolvedTo = query.data?.to || to;
  const orderedResults = (query.data?.results ?? [])
    .map((bus, index) => ({ bus, index }))
    .sort((left, right) =>
      departureMinutes(boardingDepartureTime(left.bus, resolvedFrom)) -
      departureMinutes(boardingDepartureTime(right.bus, resolvedFrom)) || left.index - right.index,
    );

  useEffect(() => {
    if (from && to && from.trim().toLocaleLowerCase() !== to.trim().toLocaleLowerCase()) {
      saveRecentSearch(from, to);
    }
  }, [from, to]);

  async function shareSearch() {
    setShareMessage("");
    const url = window.location.href;
    const title = `Bus services: ${from} to ${to}`;
    const text = `Bus services from ${from} to ${to} on ${formatDateWithWeekday(query.data?.date ?? date)}.`;
    try {
      if (typeof navigator.share === "function") {
        await navigator.share({ title, text, url });
        setShareMessage("Search shared.");
        return;
      }
      await navigator.clipboard.writeText(url);
      setShareMessage("Search link copied. You can paste it into a message.");
    } catch (error) {
      if (error instanceof Error && error.name === "AbortError") return;
      setShareMessage("Could not share automatically. Copy the page address from your browser to share this search.");
    }
  }

  const editStopsUrl = `/?${new URLSearchParams({ from, to, date }).toString()}`;

  return (
    <div className="mx-auto max-w-5xl px-3 py-5 sm:px-4 sm:py-9">
      <div className="overflow-hidden rounded-3xl border bg-card shadow-sm">
        <div className="bg-gradient-to-br from-primary/10 via-card to-accent/40 p-4 sm:p-7">
          <Link to={editStopsUrl} className="mb-4 inline-flex items-center gap-2 rounded-md text-sm font-medium text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring sm:mb-5">
            <ArrowLeft size={16} aria-hidden="true" />
            Edit stops
          </Link>
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div className="min-w-0 flex-1">
              <p className="text-xs font-semibold uppercase tracking-[0.18em] text-primary">Plan your journey</p>
              <h1 className="mt-2 break-words font-display text-2xl leading-snug sm:text-4xl">
                {resolvedFrom || "Choose a stop"}{" "}
                <span className="text-muted-foreground" aria-hidden="true">→</span>{" "}
                {resolvedTo || "Choose a destination"}
              </h1>
              <p className="mt-2 text-sm text-muted-foreground">Upcoming bus times, live status, and fares for your trip.</p>
            </div>
            <div className="flex flex-wrap gap-2 self-start shrink-0">
              {user && (
                <Button
                  type="button"
                  variant="outline"
                  disabled={savingFav}
                  onClick={async () => {
                    const fromStopObj = stopsQuery.data?.find((s) => s.name.toLowerCase() === resolvedFrom.toLowerCase());
                    const toStopObj = stopsQuery.data?.find((s) => s.name.toLowerCase() === resolvedTo.toLowerCase());
                    if (!fromStopObj) {
                      setShareMessage("Open Favourites page to save this corridor.");
                      return;
                    }
                    setSavingFav(true);
                    try {
                      await api("/api/favorites", {
                        method: "POST",
                        body: JSON.stringify({
                          fromStopId: fromStopObj.id,
                          toStopId: toStopObj?.id ?? null,
                          label: `${resolvedFrom} → ${resolvedTo}`,
                        }),
                      });
                      setShareMessage("Route saved to your Favourites.");
                    } catch (e) {
                      setShareMessage(e instanceof Error ? e.message : "Could not save route.");
                    } finally {
                      setSavingFav(false);
                    }
                  }}
                >
                  <Star size={16} aria-hidden="true" />
                  Save route
                </Button>
              )}
              <Button type="button" variant="outline" onClick={() => void shareSearch()}>
                <Share2 size={16} aria-hidden="true" />
                Share search
              </Button>
            </div>
          </div>
          <div className="mt-5 grid grid-cols-1 gap-2 sm:grid-cols-[1fr_auto_1fr] sm:items-center sm:gap-3">
            <div className="flex min-w-0 items-start gap-3 rounded-xl border bg-card/85 p-3">
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-primary/10 text-primary"><MapPin size={18} aria-hidden="true" /></span>
              <div className="min-w-0 flex-1">
                <p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">From · Starting point</p>
                <p className="break-words font-medium leading-snug text-ink">{resolvedFrom || "Select your stop"}</p>
              </div>
            </div>
            <div className="flex items-center justify-center text-muted-foreground" aria-hidden="true">
              <ArrowDown className="sm:hidden" size={16} />
              <ArrowRight className="hidden sm:block" size={18} />
            </div>
            <div className="flex min-w-0 items-start gap-3 rounded-xl border bg-card/85 p-3">
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-secondary text-secondary-foreground"><MapPin size={18} aria-hidden="true" /></span>
              <div className="min-w-0 flex-1">
                <p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">To · Ending point</p>
                <p className="break-words font-medium leading-snug text-ink">{resolvedTo || "Select your destination"}</p>
              </div>
            </div>
          </div>
          <div className="mt-4 flex flex-wrap items-end justify-between gap-3">
            <label htmlFor="search-date" className="grid gap-1.5 text-sm font-medium">
              <span className="flex items-center gap-1.5"><CalendarDays size={15} aria-hidden="true" /> Travel date</span>
              <input
                id="search-date"
                type="date"
                required
                value={date}
                onChange={(event) => {
                  const next = new URLSearchParams(params);
                  next.set("date", event.target.value);
                  setParams(next);
                }}
                className="h-10 rounded-md border bg-card px-3"
              />
            </label>
            <p className="pb-1 text-sm text-muted-foreground">{formatDateWithWeekday(query.data?.date ?? date)}</p>
          </div>
          <p className="mt-4 border-t border-border/70 pt-3 text-sm text-muted-foreground">Fares are estimated at ₹1.80 per km, rounded to the nearest ₹5.</p>
          {shareMessage && <p className="mt-2 text-sm text-primary" role="status" aria-live="polite">{shareMessage}</p>}
        </div>
      </div>
      {query.loading && <p className="mt-8 text-muted-foreground">Checking the timetable…</p>}
      {query.error && (
        <div className="mt-8 rounded-xl border bg-card p-5">
          <p>{query.error}</p>
          <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
        </div>
      )}
      {query.data?.showingNextDate && (
        <div className="mt-6 flex items-start gap-3 rounded-2xl border border-amber-500/40 bg-amber-500/10 p-4 text-sm text-foreground" role="status" aria-live="polite">
          <CalendarDays className="mt-0.5 h-5 w-5 shrink-0 text-amber-700" aria-hidden="true" />
          <div className="min-w-0 flex-1 space-y-1">
            <p className="font-semibold">
              No more departures remaining for {formatDateWithWeekday(query.data.requestedDate ?? date)}.
            </p>
            <p className="text-muted-foreground">
              Automatically showing buses available for the next date: <span className="font-medium text-foreground">{formatDateWithWeekday(query.data.date)}</span>.
            </p>
          </div>
        </div>
      )}
      {query.data?.journeyPlan && query.data.journeyPlan.allOptions.length > 0 && (
        <div className="mt-6">
          <JourneyPlannerCard plan={query.data.journeyPlan} date={query.data.date} />
        </div>
      )}
      {query.data && query.data.results.length === 0 && (!query.data.journeyPlan || query.data.journeyPlan.allOptions.length === 0) && (
        <div className="mt-8 rounded-2xl border bg-card p-6 sm:p-8">
          <div className="mx-auto max-w-lg text-center">
            <span className="mx-auto grid h-12 w-12 place-items-center rounded-full bg-muted text-muted-foreground"><BusFront size={22} aria-hidden="true" /></span>
            <h2 className="mt-4 font-display text-2xl">No upcoming buses found</h2>
            <p className="mt-2 text-sm leading-6 text-muted-foreground">
              No upcoming buses run from {query.data.from} to {query.data.to} on {formatDateWithWeekday(query.data.date)}. Try another date or check your stops.
            </p>
            <Button className="mt-5" variant="outline" asChild><Link to={editStopsUrl}><ArrowLeft size={16} aria-hidden="true" /> Edit stops</Link></Button>
          </div>
        </div>
      )}
      {query.data && query.data.results.length > 0 && (
        <div className="mt-8 flex flex-wrap items-end justify-between gap-2">
          <div className="min-w-0">
            <p className="font-semibold text-ink">
              {query.data.results.length} upcoming bus {query.data.results.length === 1 ? "service" : "services"} found
            </p>
            <p className="text-sm text-muted-foreground">Services are numbered by departure time, earliest first · {formatDateWithWeekday(query.data.date)}.</p>
          </div>
          <Link to={editStopsUrl} className="text-sm font-medium text-primary underline-offset-4 hover:underline">Change stops</Link>
        </div>
      )}
      {orderedResults.length > 0 && (
        <ol className="mt-5 grid min-w-0 gap-4" aria-label="Bus services ordered by departure time">
          {orderedResults.map(({ bus }, index) => (
            <ServiceCard
              key={`${bus.busId}-${bus.routeId}-${bus.serviceDate}-${bus.departureTime}`}
              bus={bus}
              number={index + 1}
              from={query.data!.from}
              to={query.data!.to}
              date={query.data!.date}
            />
          ))}
        </ol>
      )}
    </div>
  );
}

function boardingDepartureTime(bus: TrackSnapshot, from: string) {
  const match = bus.stops.find((stop) => stop.name.toLocaleLowerCase() === from.toLocaleLowerCase());
  return match?.scheduledTime || bus.departureTime;
}

function departureMinutes(value: string) {
  const [hours, minutes] = value.split(":").map(Number);
  if (!Number.isFinite(hours) || !Number.isFinite(minutes)) return Number.POSITIVE_INFINITY;
  return hours * 60 + minutes;
}

function ServiceCard({ bus, number, from, to, date }: {
  bus: TrackSnapshot;
  number: number;
  from: string;
  to: string;
  date: string;
}) {
  const firstIndex = bus.stops.findIndex((stop) => stop.name.toLocaleLowerCase() === from.toLocaleLowerCase());
  const lastIndex = bus.stops.findIndex((stop) => stop.name.toLocaleLowerCase() === to.toLocaleLowerCase());
  const journeyStops = firstIndex >= 0 && lastIndex >= firstIndex
    ? bus.stops.slice(firstIndex, lastIndex + 1)
    : bus.stops;
  const boardingTime = journeyStops[0]?.scheduledTime || bus.departureTime;
  const distance = journeyStops.at(-1) && journeyStops[0]
    ? Math.max(0, journeyStops.at(-1)!.distanceKm - journeyStops[0].distanceKm)
    : null;

  return (
    <li className="min-w-0 list-none">
      <article className="min-w-0 overflow-hidden rounded-3xl border bg-card shadow-sm transition-shadow hover:shadow-md">
        <div className="border-b bg-muted/30 p-4 sm:p-5">
          <div className="flex items-start justify-between gap-3">
            <div className="flex min-w-0 flex-1 items-start gap-3">
              <span className="grid h-10 w-10 shrink-0 place-items-center rounded-2xl bg-primary text-base font-bold text-primary-foreground shadow-sm sm:h-11 sm:w-11 sm:text-lg" aria-label={`Service ${number}`}>
                {number}
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
                  <span className="rounded-md bg-primary/10 px-2 py-0.5 font-semibold text-primary">Route {bus.routeCode}</span>
                  <span className="break-words">{bus.operatorName}</span>
                </div>
                <p className="mt-1 break-words text-sm font-medium leading-snug text-ink">{bus.routeName}</p>
              </div>
            </div>
            <div className="shrink-0 rounded-xl border bg-card px-3 py-1.5 text-center sm:min-w-28 sm:px-4 sm:py-2">
              <p className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground sm:text-[11px]">Departs</p>
              <p className="font-display text-lg font-semibold text-primary sm:text-xl">{formatClock(boardingTime)}</p>
            </div>
          </div>

          <div className="mt-3 grid grid-cols-1 gap-2 rounded-2xl border bg-card/85 p-3 sm:grid-cols-[1fr_auto_1fr] sm:items-center sm:gap-3">
            <div className="flex min-w-0 items-start gap-2.5">
              <span className="mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full bg-primary ring-4 ring-primary/15" aria-hidden="true" />
              <div className="min-w-0 flex-1">
                <p className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">From · Starting point</p>
                <h2 className="break-words font-display text-base font-semibold leading-snug text-ink sm:text-lg">{from}</h2>
              </div>
            </div>
            <div className="hidden items-center justify-center sm:flex" aria-hidden="true">
              <ArrowRight className="h-4 w-4 text-muted-foreground" />
            </div>
            <div className="flex min-w-0 items-start gap-2.5 border-t border-border/60 pt-2 sm:border-t-0 sm:pt-0">
              <span className="mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full bg-foreground ring-4 ring-foreground/10" aria-hidden="true" />
              <div className="min-w-0 flex-1">
                <p className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">To · Ending point</p>
                <p className="break-words font-display text-base font-semibold leading-snug text-ink sm:text-lg">{to}</p>
              </div>
            </div>
          </div>
        </div>
        <div className="p-4 sm:p-5">
          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
            <h3 className="min-w-0 flex-1 break-words text-xs font-semibold uppercase tracking-wide text-muted-foreground">
              Stops &amp; distance from {from}
            </h3>
            {distance != null && <span className="shrink-0 text-xs font-medium text-muted-foreground">{distance.toFixed(0)} km</span>}
          </div>
          <JourneyDiagram stops={journeyStops} boardingStop={from} destinationStop={to} />
          <BusDeparture bus={bus} from={from} to={to} date={date} boardingTime={boardingTime} />
        </div>
      </article>
    </li>
  );
}

function JourneyDiagram({ stops, boardingStop, destinationStop }: {
  stops: StopOnTrip[];
  boardingStop: string;
  destinationStop: string;
}) {
  const boardingIndex = stops.findIndex((stop) => stop.name.toLocaleLowerCase() === boardingStop.toLocaleLowerCase());
  const boardingDistance = boardingIndex >= 0 ? stops[boardingIndex].distanceKm : stops[0]?.distanceKm ?? 0;

  return (
    <div className="mt-4 rounded-2xl bg-muted/45 px-3 py-4 sm:px-5" aria-label={`Journey distance from ${boardingStop} to ${destinationStop}`}>
      {/* Mobile vertical timeline: ensures Starting Point, Ending Point, and all intermediate stops are readable without horizontal clipping */}
      <ol className="sm:hidden">
        {stops.map((stop, index) => {
          const isBoarding = stop.name.toLocaleLowerCase() === boardingStop.toLocaleLowerCase();
          const isDestination = stop.name.toLocaleLowerCase() === destinationStop.toLocaleLowerCase();
          const isLast = index === stops.length - 1;
          const distanceFromBoarding = Math.max(0, stop.distanceKm - boardingDistance);
          return (
            <li key={`${stop.stopId}-${index}-mobile`} className="grid grid-cols-[20px_1fr] gap-2.5">
              <div className="flex flex-col items-center">
                <span
                  className={`mt-1.5 h-3.5 w-3.5 shrink-0 rounded-full border-[3px] border-card ${
                    isBoarding ? "bg-primary ring-2 ring-primary/40" : isDestination ? "bg-foreground ring-2 ring-foreground/30" : "bg-card ring-2 ring-primary"
                  }`}
                />
                {!isLast && <span className="mt-1 w-0.5 flex-1 bg-primary/50" />}
              </div>
              <div className={`min-w-0 ${isLast ? "pb-0" : "pb-3.5"}`}>
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0 flex-1">
                    <p className={`break-words text-sm leading-snug ${isBoarding || isDestination ? "font-semibold text-ink" : "text-foreground/90"}`}>
                      {stop.name}
                    </p>
                    {isBoarding && <span className="mt-0.5 inline-block text-[11px] font-medium text-primary">Board here (Start)</span>}
                    {isDestination && <span className="mt-0.5 inline-block text-[11px] font-medium text-foreground">Get off here (End)</span>}
                  </div>
                  <div className="shrink-0 text-right">
                    <p className="text-xs font-semibold text-foreground">{formatClock(stop.scheduledTime)}</p>
                    <p className="text-[11px] text-muted-foreground">{distanceFromBoarding.toFixed(0)} km</p>
                  </div>
                </div>
              </div>
            </li>
          );
        })}
      </ol>

      {/* Tablet/desktop horizontal progression diagram */}
      <div className="hidden overflow-x-auto sm:block">
        <ol
          className="grid items-start"
          style={{
            width: `max(100%, ${Math.max(480, stops.length * 132)}px)`,
            gridTemplateColumns: `repeat(${stops.length}, minmax(0, 1fr))`,
          }}
        >
          {stops.map((stop, index) => {
            const isBoarding = stop.name.toLocaleLowerCase() === boardingStop.toLocaleLowerCase();
            const isDestination = stop.name.toLocaleLowerCase() === destinationStop.toLocaleLowerCase();
            const distanceFromBoarding = Math.max(0, stop.distanceKm - boardingDistance);
            return (
              <li key={`${stop.stopId}-${index}-desktop`} className="relative flex min-w-0 flex-col items-center px-2 text-center">
                <div className="relative h-[64px] w-full">
                  <span className={`absolute inset-x-0 top-0 text-xs font-semibold ${isBoarding || isDestination ? "text-foreground" : "text-muted-foreground"}`} aria-label={`${distanceFromBoarding.toFixed(0)} kilometers from ${boardingStop}`}>
                    {distanceFromBoarding.toFixed(0)} km
                  </span>
                  {index > 0 && (
                    <span className="absolute left-0 right-1/2 top-[43px] border-t-2 border-primary" />
                  )}
                  {index < stops.length - 1 && (
                    <span className="absolute left-1/2 right-0 top-[43px] border-t-2 border-primary" />
                  )}
                  {isBoarding && (
                    <BusFront aria-label={`Board at ${stop.name}`} className="absolute left-1/2 top-[18px] z-10 h-4 w-4 -translate-x-1/2 text-primary" />
                  )}
                  {isDestination && (
                    <MapPin aria-label={`Get off at ${stop.name}`} className="absolute left-1/2 top-[18px] z-10 h-4 w-4 -translate-x-1/2 text-foreground" />
                  )}
                  <span className={`absolute left-1/2 top-[37px] z-10 h-3.5 w-3.5 -translate-x-1/2 rounded-full border-[3px] border-card ${isBoarding ? "bg-primary ring-1 ring-primary" : isDestination ? "bg-foreground ring-1 ring-foreground" : "bg-card ring-2 ring-primary"}`} />
                </div>
                <span className={`w-full break-words px-1 text-xs leading-5 ${isBoarding || isDestination ? "font-semibold text-ink" : "text-muted-foreground"}`} title={stop.name}>
                  {stop.name}
                </span>
                <span className="mt-0.5 text-[11px] font-medium text-muted-foreground">{formatClock(stop.scheduledTime)}</span>
                <span className="mt-0.5 min-h-4 text-[10px] font-medium leading-4">
                  {isBoarding && <span className="text-primary">Board here</span>}
                  {isDestination && <span className="text-foreground">Get off here</span>}
                </span>
              </li>
            );
          })}
        </ol>
      </div>
    </div>
  );
}

function BusDeparture({ bus, from, to, date, boardingTime }: { bus: TrackSnapshot; from: string; to: string; date: string; boardingTime: string }) {
  const params = new URLSearchParams({ from, to, routeId: String(bus.routeId), date });
  const href = `/bus/${bus.fleetNumber}?${params.toString()}`;
  return (
    <div className="mt-4 rounded-2xl border p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-3">
          <span className="grid h-10 w-10 shrink-0 place-items-center rounded-xl bg-primary/10 text-primary">
            <BusFront aria-hidden="true" className="h-5 w-5" />
          </span>
          <div className="min-w-0">
            <p className="font-semibold text-ink">Bus {bus.fleetNumber}</p>
            <p className="break-words text-xs text-muted-foreground">{bus.registrationNumber} · {bus.busType} · {bus.capacity} seats</p>
          </div>
        </div>
        <StatusBadge status={bus.status} />
      </div>
      <dl className="mt-3 grid grid-cols-2 gap-3 text-sm sm:grid-cols-3">
        <Cell k="Fare" v={rupee(bus.fare)} />
        <Cell k="Now" v={bus.currentStop ?? "At the depot"} />
        <Cell k="Next" v={bus.nextStop ? `${bus.nextStop}${bus.etaMinutes != null ? ` · ${formatEta(bus.etaMinutes)}` : ""}` : "—"} />
      </dl>
      <p className="mt-3 break-words text-sm text-muted-foreground">
        {bus.status === "Running" && !bus.boardingPassed && bus.etaToYouMinutes != null && `Reaches ${from} in ${formatEta(bus.etaToYouMinutes)}.`}
        {bus.status === "Scheduled" && bus.departsInMinutes != null && bus.departsInMinutes >= 0 && `Departs ${formatWhen(bus.departsInMinutes)} from ${boardingTime !== bus.departureTime ? "the route origin" : from}.`}
        {bus.status === "Scheduled" && (bus.departsInMinutes == null || bus.departsInMinutes < 0) && `Scheduled to depart ${from} at ${formatClock(boardingTime)}.`}
      </p>
      <div className="mt-4 flex flex-col gap-2 sm:flex-row sm:flex-wrap">
        <Button className="w-full sm:w-auto" asChild><Link to={href}>View stops and fare</Link></Button>
        <Button className="w-full sm:w-auto" variant="outline" asChild><Link to={`/track/${bus.fleetNumber}?${params.toString()}`}>Track live</Link></Button>
      </div>
    </div>
  );
}

function Cell({ k, v }: { k: string; v: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs uppercase tracking-wider text-muted-foreground">{k}</dt>
      <dd className="break-words font-medium">{v}</dd>
    </div>
  );
}
