import { Link, useSearchParams } from "react-router-dom";
import { StatusBadge } from "@/components/StatusBadge";
import { Button } from "@/components/ui/button";
import { useQuery } from "@/lib/api";
import { formatClock, formatEta, formatWhen, rupee } from "@/lib/format";
import type { SearchResponse, TrackSnapshot } from "@/lib/types";

export function SearchPage() {
  const [params] = useSearchParams();
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const query = useQuery<SearchResponse>(from && to ? `/api/search?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}` : null);

  return (
    <div className="mx-auto max-w-4xl px-4 py-8">
      <p className="text-xs uppercase tracking-[0.18em] text-pine">Today's services</p>
      <h1 className="mt-2 font-display text-4xl">{from || "From"} → {to || "To"}</h1>
      <p className="mt-2 text-muted-foreground">Fares follow the corridor at ₹1.80 per km, rounded to the nearest ₹5.</p>
      {query.loading && <p className="mt-8 text-muted-foreground">Checking the timetable…</p>}
      {query.error && (
        <div className="mt-8 rounded-xl border bg-card p-5">
          <p>{query.error}</p>
          <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
        </div>
      )}
      {query.data && query.data.results.length === 0 && (
        <div className="mt-8 rounded-xl border bg-card p-5">
          <p className="font-medium">No bus runs from {query.data.from} to {query.data.to} today.</p>
          <p className="mt-1 text-sm text-muted-foreground">Route 101 only travels Hisar → Barwala → Tohana → Fatehabad. Try that direction.</p>
          <Button className="mt-4" asChild><Link to="/search?from=Hisar&to=Fatehabad">Show Hisar to Fatehabad</Link></Button>
        </div>
      )}
      <div className="mt-6 grid gap-4">
        {query.data?.results.map((bus) => <ResultCard key={`${bus.fleetNumber}-${bus.departureTime}`} bus={bus} from={query.data!.from} to={query.data!.to} />)}
      </div>
    </div>
  );
}

function ResultCard({ bus, from, to }: { bus: TrackSnapshot; from: string; to: string }) {
  const href = `/bus/${bus.fleetNumber}?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`;
  return (
    <article className="rounded-2xl border bg-card p-5 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-xs uppercase tracking-wider text-muted-foreground">{bus.operatorName} · Route {bus.routeCode}</p>
          <h2 className="font-display text-4xl leading-none">Bus {bus.fleetNumber}</h2>
          <p className="mt-1 text-sm">{bus.registrationNumber} · {bus.busType} · {bus.capacity} seats</p>
        </div>
        <StatusBadge status={bus.status} />
      </div>
      <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-4">
        <Cell k="Departs" v={formatClock(bus.departureTime)} />
        <Cell k="Fare" v={rupee(bus.fare)} />
        <Cell k="Now" v={bus.currentStop ?? "At the depot"} />
        <Cell k="Next" v={bus.nextStop ? `${bus.nextStop}${bus.etaMinutes != null ? ` · ${formatEta(bus.etaMinutes)}` : ""}` : "—"} />
      </dl>
      <p className="mt-3 text-sm">
        {bus.status === "Running" && !bus.boardingPassed && bus.etaToYouMinutes != null && `Reaches you in ${formatEta(bus.etaToYouMinutes)}.`}
        {bus.status === "Running" && bus.boardingPassed && `The bus has passed ${from}. ${to} in ${formatEta(bus.etaToDestinationMinutes)}.`}
        {bus.status === "Scheduled" && bus.departsInMinutes != null && bus.departsInMinutes >= 0 && `Departs ${formatWhen(bus.departsInMinutes)} from the first stop.`}
        {bus.status === "Scheduled" && bus.departsInMinutes != null && bus.departsInMinutes < 0 && "The printed departure has passed. The driver can still start this trip."}
        {bus.status === "Completed" && "This trip has finished."}
      </p>
      <div className="mt-4 flex flex-wrap gap-2">
        <Button asChild><Link to={href}>View stops and fare</Link></Button>
        <Button variant="outline" asChild><Link to={`/track/${bus.fleetNumber}?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`}>Track live</Link></Button>
      </div>
    </article>
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
