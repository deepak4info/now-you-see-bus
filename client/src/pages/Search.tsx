import { useEffect, useState } from "react";
import { ArrowLeft, ArrowRight, BusFront, CalendarDays, MapPin, Share2 } from "lucide-react";
import { Link, useSearchParams } from "react-router-dom";
import { StatusBadge } from "@/components/StatusBadge";
import { Button } from "@/components/ui/button";
import { useQuery } from "@/lib/api";
import { formatClock, formatDateWithWeekday, formatEta, formatWhen, getIndiaDate, isDateOnly, rupee } from "@/lib/format";
import { saveRecentSearch } from "@/lib/recentSearch";
import type { SearchResponse, StopOnTrip, TrackSnapshot } from "@/lib/types";

export function SearchPage() {
  const [params, setParams] = useSearchParams();
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const dateParam = params.get("date");
  const date = isDateOnly(dateParam) ? dateParam : getIndiaDate();
  const [shareMessage, setShareMessage] = useState("");
  const query = useQuery<SearchResponse>(
    from && to ? `/api/search?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&date=${date}` : null,
  );
  const orderedResults = (query.data?.results ?? [])
    .map((bus, index) => ({ bus, index }))
    .sort((left, right) => departureMinutes(left.bus.departureTime) - departureMinutes(right.bus.departureTime) || left.index - right.index);

  useEffect(() => {
    if (from && to && from.trim().toLocaleLowerCase() !== to.trim().toLocaleLowerCase()) {
      saveRecentSearch(from, to);
    }
  }, [from, to]);

  async function shareSearch() {
    setShareMessage("");
    const url = window.location.href;
    const title = `Bus services: ${from} to ${to}`;
    const text = `Bus services from ${from} to ${to} on ${formatDateWithWeekday(date)}.`;
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
    <div className="mx-auto max-w-5xl px-4 py-6 sm:py-9">
      <div className="overflow-hidden rounded-3xl border bg-card shadow-sm">
        <div className="bg-gradient-to-br from-primary/10 via-card to-accent/40 p-5 sm:p-7">
          <Link to={editStopsUrl} className="mb-5 inline-flex items-center gap-2 rounded-md text-sm font-medium text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
            <ArrowLeft size={16} aria-hidden="true" />
            Edit stops
          </Link>
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <p className="text-xs font-semibold uppercase tracking-[0.18em] text-primary">Plan your journey</p>
              <h1 className="mt-2 font-display text-3xl leading-tight sm:text-4xl">{from || "Choose a stop"} <span className="text-muted-foreground">→</span> {to || "Choose a destination"}</h1>
              <p className="mt-2 text-sm text-muted-foreground">Bus times, live status, and fares for your trip.</p>
            </div>
            <Button type="button" variant="outline" className="shrink-0" onClick={() => void shareSearch()}>
              <Share2 size={16} aria-hidden="true" />
              Share search
            </Button>
          </div>
          <div className="mt-5 grid gap-3 sm:grid-cols-[1fr_auto_1fr] sm:items-center">
            <div className="flex min-w-0 items-center gap-3 rounded-xl border bg-card/80 p-3">
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-primary/10 text-primary"><MapPin size={18} aria-hidden="true" /></span>
              <div className="min-w-0"><p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Board at</p><p className="truncate font-medium">{from || "Select your stop"}</p></div>
            </div>
            <ArrowRight className="hidden text-muted-foreground sm:block" size={18} aria-hidden="true" />
            <div className="flex min-w-0 items-center gap-3 rounded-xl border bg-card/80 p-3">
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-secondary text-secondary-foreground"><MapPin size={18} aria-hidden="true" /></span>
              <div className="min-w-0"><p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Get off at</p><p className="truncate font-medium">{to || "Select your destination"}</p></div>
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
            <p className="pb-2 text-sm text-muted-foreground">{formatDateWithWeekday(date)}</p>
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
      {query.data && query.data.results.length === 0 && (
        <div className="mt-8 rounded-2xl border bg-card p-6 sm:p-8">
          <div className="mx-auto max-w-lg text-center">
            <span className="mx-auto grid h-12 w-12 place-items-center rounded-full bg-muted text-muted-foreground"><BusFront size={22} aria-hidden="true" /></span>
            <h2 className="mt-4 font-display text-2xl">No buses found for this trip</h2>
            <p className="mt-2 text-sm leading-6 text-muted-foreground">
              No buses run from {query.data.from} to {query.data.to} on {formatDateWithWeekday(query.data.date)}. Try another date or check your stops.
            </p>
            <Button className="mt-5" variant="outline" asChild><Link to={editStopsUrl}><ArrowLeft size={16} aria-hidden="true" /> Edit stops</Link></Button>
          </div>
        </div>
      )}
      {query.data && query.data.results.length > 0 && (
        <div className="mt-8 flex flex-wrap items-end justify-between gap-2">
          <div>
            <p className="font-semibold text-ink">
              {query.data.results.length} bus {query.data.results.length === 1 ? "service" : "services"} found
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
              key={`${bus.busId}-${bus.routeId}-${bus.departureTime}`}
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
  const distance = journeyStops.at(-1) && journeyStops[0]
    ? Math.max(0, journeyStops.at(-1)!.distanceKm - journeyStops[0].distanceKm)
    : null;

  return (
    <li className="min-w-0 list-none">
      <article className="min-w-0 overflow-hidden rounded-3xl border bg-card shadow-sm transition-shadow hover:shadow-md">
        <div className="flex flex-wrap items-center gap-3 border-b bg-muted/30 p-4 sm:p-5">
          <span className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl bg-primary text-lg font-bold text-primary-foreground shadow-sm" aria-label={`Service ${number}`}>
            {number}
          </span>
          <div className="min-w-0 flex-1">
            <h2 className="font-display text-xl leading-tight sm:text-2xl">{from} <span className="text-muted-foreground">→</span> {to}</h2>
            <p className="mt-1 truncate text-sm text-muted-foreground">{bus.operatorName} · Route {bus.routeCode} · {bus.routeName}</p>
          </div>
          <div className="min-w-28 rounded-xl border bg-card px-4 py-2 text-center sm:min-w-32">
            <p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Departs</p>
            <p className="font-display text-xl font-semibold text-primary">{formatClock(bus.departureTime)}</p>
          </div>
        </div>
        <div className="p-4 sm:p-5">
          <div className="mb-2 flex items-center justify-between gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Stops &amp; distance from {from}</h3>
            {distance != null && <span className="text-xs font-medium text-muted-foreground">{distance.toFixed(0)} km</span>}
          </div>
          <JourneyDiagram stops={journeyStops} boardingStop={from} destinationStop={to} />
          <BusDeparture bus={bus} from={from} to={to} date={date} />
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
    <div className="mt-4 overflow-x-auto rounded-2xl bg-muted/45 px-3 py-4 sm:px-5" aria-label={`Journey distance from ${boardingStop}`}>
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
            <li key={`${stop.stopId}-${index}`} className="relative flex min-w-0 flex-col items-center px-2 text-center">
              <div className="relative h-[58px] w-full">
                <span className={`absolute inset-x-0 top-0 text-xs font-semibold ${isBoarding || isDestination ? "text-foreground" : "text-muted-foreground"}`} aria-label={`${distanceFromBoarding.toFixed(0)} kilometers from ${boardingStop}`}>
                  {distanceFromBoarding.toFixed(0)} km
                </span>
                {index > 0 && (
                  <span className="absolute left-0 right-1/2 top-[39px] border-t-2 border-primary" />
                )}
                {index < stops.length - 1 && (
                  <span className="absolute left-1/2 right-0 top-[39px] border-t-2 border-primary" />
                )}
                {isBoarding && (
                  <BusFront aria-label={`Board at ${stop.name}`} className="absolute left-1/2 top-[17px] z-10 h-5 w-5 -translate-x-1/2 text-primary" />
                )}
                {isDestination && (
                  <MapPin aria-label={`Get off at ${stop.name}`} className="absolute left-1/2 top-[17px] z-10 h-5 w-5 -translate-x-1/2 text-foreground" />
                )}
                <span className={`absolute left-1/2 top-[33px] z-10 h-3.5 w-3.5 -translate-x-1/2 rounded-full border-[3px] border-card ${isBoarding ? "bg-primary ring-1 ring-primary" : isDestination ? "bg-foreground ring-1 ring-foreground" : "bg-card ring-2 ring-primary"}`} />
              </div>
              <span className={`min-h-10 w-full break-words px-1 text-xs leading-5 ${isBoarding || isDestination ? "font-semibold text-ink" : "text-muted-foreground"}`} title={stop.name}>
                {stop.name}
              </span>
              <span className="mt-0.5 min-h-4 text-[10px] font-medium leading-4">
                {isBoarding && <span className="text-primary">Board here</span>}
                {isDestination && <span className="text-foreground">Get off here</span>}
              </span>
            </li>
          );
        })}
      </ol>
    </div>
  );
}

function BusDeparture({ bus, from, to, date }: { bus: TrackSnapshot; from: string; to: string; date: string }) {
  const params = new URLSearchParams({ from, to, routeId: String(bus.routeId), date });
  const href = `/bus/${bus.fleetNumber}?${params.toString()}`;
  return (
    <div className="mt-4 rounded-2xl border p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-start gap-3">
          <span className="grid h-10 w-10 shrink-0 place-items-center rounded-xl bg-primary/10 text-primary">
            <BusFront aria-hidden="true" className="h-5 w-5" />
          </span>
          <div>
            <p className="font-semibold text-ink">Bus {bus.fleetNumber}</p>
            <p className="text-xs text-muted-foreground">{bus.registrationNumber} · {bus.busType} · {bus.capacity} seats</p>
          </div>
        </div>
        <StatusBadge status={bus.status} />
      </div>
      <dl className="mt-3 grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
        <Cell k="Fare" v={rupee(bus.fare)} />
        <Cell k="Now" v={bus.currentStop ?? "At the depot"} />
        <Cell k="Next" v={bus.nextStop ? `${bus.nextStop}${bus.etaMinutes != null ? ` · ${formatEta(bus.etaMinutes)}` : ""}` : "—"} />
      </dl>
      <p className="mt-3 text-sm">
        {bus.status === "Running" && !bus.boardingPassed && bus.etaToYouMinutes != null && `Reaches ${from} in ${formatEta(bus.etaToYouMinutes)}.`}
        {bus.status === "Running" && bus.boardingPassed && `The bus has passed ${from}. ${to} in ${formatEta(bus.etaToDestinationMinutes)}.`}
        {bus.status === "Scheduled" && bus.departsInMinutes != null && bus.departsInMinutes >= 0 && `Departs ${formatWhen(bus.departsInMinutes)} from the first stop.`}
        {bus.status === "Scheduled" && bus.departsInMinutes != null && bus.departsInMinutes < 0 && "The printed departure has passed. The driver can still start this trip."}
        {bus.status === "Completed" && "This trip has finished."}
      </p>
      <div className="mt-4 flex flex-wrap gap-2">
        <Button asChild><Link to={href}>View stops and fare</Link></Button>
        <Button variant="outline" asChild><Link to={`/track/${bus.fleetNumber}?${params.toString()}`}>Track live</Link></Button>
      </div>
    </div>
  );
}

function Cell({ k, v }: { k: string; v: string }) {
  return (
    <div>
      <dt className="text-xs uppercase tracking-wider text-muted-foreground">{k}</dt>
      <dd className="font-medium">{v}</dd>
    </div>
  );
}
