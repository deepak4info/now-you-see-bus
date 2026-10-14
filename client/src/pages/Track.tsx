import { Link, useParams, useSearchParams } from "react-router-dom";
import { MapView } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { StopTimeline } from "@/components/StopTimeline";
import { Button } from "@/components/ui/button";
import { useQuery } from "@/lib/api";
import { ago, formatEta, rupee } from "@/lib/format";
import { useBusLive } from "@/lib/live";
import type { TrackSnapshot } from "@/lib/types";

export function TrackPage() {
  const { fleet = "" } = useParams();
  const [params] = useSearchParams();
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const qs = new URLSearchParams();
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  const query = useQuery<TrackSnapshot>(fleet ? `/api/track/${fleet}?${qs.toString()}` : null);
  const live = useBusLive(fleet, query.data);

  if (query.loading) return <p className="px-4 py-10 text-muted-foreground">Opening the live map…</p>;
  if (query.error || !live) {
    return (
      <div className="px-4 py-10">
        <p>{query.error ?? "No live trip for this bus."}</p>
        <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
      </div>
    );
  }

  const bus = live.latitude != null && live.longitude != null
    ? { latitude: live.latitude, longitude: live.longitude, heading: live.heading, label: live.fleetNumber }
    : null;
  const reachesYou = live.status === "Running" && !live.boardingPassed && live.etaToYouMinutes != null;

  return (
    <div className="grid min-h-[calc(100vh-4rem)] lg:grid-cols-[380px_1fr]">
      <aside className="order-2 border-t bg-card/95 p-5 lg:order-1 lg:border-r lg:border-t-0">
        <div className="flex items-center justify-between gap-3">
          <div>
            <p className="text-xs uppercase tracking-wider text-muted-foreground">/track/{live.fleetNumber}</p>
            <h1 className="font-display text-4xl">Bus {live.fleetNumber}</h1>
          </div>
          <StatusBadge status={live.status} />
        </div>
        <p className="mt-1 text-sm text-muted-foreground">{live.registrationNumber} · Route {live.routeCode}</p>
        {live.status === "Running" && (
          <p className="mt-3 flex items-center gap-2 text-sm text-pine">
            <span className="pulse" /> Live · updated {ago(live.recordedAt)}
            {live.simulation && " · demo playback"}
          </p>
        )}
        {live.status === "Completed" && (
          <p className="mt-4 rounded-xl bg-ink px-4 py-3 text-paper">Trip completed. The bus is at {live.currentStop ?? "the last stop"}.</p>
        )}
        <div className="mt-5">
          <p className="text-xs uppercase tracking-wider text-muted-foreground">Right now</p>
          <p className="font-display text-3xl">{live.currentStop ?? "Not on the road"}</p>
          {live.nextStop && <p className="text-muted-foreground">Next {live.nextStop} · {formatEta(live.etaMinutes)}</p>}
        </div>
        <div className="mt-4 rounded-2xl bg-paper px-4 py-3">
          <p className="text-xs uppercase tracking-wider text-muted-foreground">
            {reachesYou ? `Until it reaches ${from || "you"}` : `Until ${to || live.stops.at(-1)?.name || "the last stop"}`}
          </p>
          <p className="font-display text-5xl leading-none">
            {reachesYou ? formatEta(live.etaToYouMinutes) : formatEta(live.etaToDestinationMinutes)}
          </p>
          {live.fare != null && <p className="mt-2 text-sm">Fare {rupee(live.fare)}</p>}
          {live.simulation && (
            <p className="mt-2 text-xs text-muted-foreground">Playback is sped up so the whole corridor is visible. ETAs still use a 42 km/h road speed.</p>
          )}
        </div>
        <div className="mt-5">
          <StopTimeline stops={live.stops} />
        </div>
        <Button className="mt-5" variant="outline" asChild>
          <Link to={`/bus/${live.fleetNumber}?${qs.toString()}`}>Trip details</Link>
        </Button>
      </aside>
      <div className="order-1 h-[52vh] lg:order-2 lg:h-auto">
        <MapView path={live.path} stops={live.stops} bus={bus} className="h-full w-full" />
      </div>
    </div>
  );
}
