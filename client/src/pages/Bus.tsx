import { Link, useParams, useSearchParams } from "react-router-dom";
import { useState } from "react";
import { Star } from "lucide-react";
import { MapView } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { StopTimeline } from "@/components/StopTimeline";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatClock, formatEta, rupee } from "@/lib/format";
import { useBusLive } from "@/lib/live";
import type { TrackSnapshot } from "@/lib/types";

export function BusPage() {
  const { fleet = "" } = useParams();
  const [params] = useSearchParams();
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const qs = new URLSearchParams();
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  const query = useQuery<TrackSnapshot>(fleet ? `/api/track/${fleet}?${qs.toString()}` : null);
  const live = useBusLive(fleet, query.data);
  const { user } = useAuth();
  const [note, setNote] = useState("");
  const [saving, setSaving] = useState(false);

  async function save() {
    if (!live) return;
    setSaving(true);
    setNote("");
    try {
      await api("/api/favorites", {
        method: "POST",
        body: JSON.stringify({
          busId: live.busId,
          routeId: live.routeId,
          label: `${from || live.stops[0]?.name} → ${to || live.stops.at(-1)?.name} · Bus ${live.fleetNumber}`,
        }),
      });
      setNote("Saved to favourites. You will get an alert when this bus starts or finishes.");
    } catch (err) {
      setNote(err instanceof Error ? err.message : "Could not save");
    } finally {
      setSaving(false);
    }
  }

  if (query.loading) return <p className="mx-auto max-w-6xl px-4 py-10 text-muted-foreground">Loading bus {fleet}…</p>;
  if (query.error || !live) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-10">
        <p>{query.error ?? "This bus is not on today's board."}</p>
        <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
      </div>
    );
  }

  const track = `/track/${live.fleetNumber}?${qs.toString()}`;
  const busPin = live.latitude != null && live.longitude != null
    ? { latitude: live.latitude, longitude: live.longitude, heading: live.heading, label: live.fleetNumber }
    : null;

  return (
    <div className="mx-auto grid max-w-6xl gap-6 px-4 py-8 lg:grid-cols-[0.9fr_1.1fr]">
      <section className="rounded-2xl border bg-card p-5">
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-xs uppercase tracking-wider text-muted-foreground">{live.operatorName} · Route {live.routeCode}</p>
            <h1 className="font-display text-4xl">Bus {live.fleetNumber}</h1>
            <p className="mt-1">{live.registrationNumber} · {live.busType}</p>
          </div>
          <StatusBadge status={live.status} />
        </div>
        <dl className="mt-4 grid grid-cols-2 gap-3 text-sm">
          <div><dt className="text-muted-foreground">Departure</dt><dd className="font-medium">{formatClock(live.departureTime)}</dd></div>
          <div><dt className="text-muted-foreground">Fare {from && to ? `${from}–${to}` : ""}</dt><dd className="font-medium">{rupee(live.fare)}</dd></div>
          <div><dt className="text-muted-foreground">Driver</dt><dd className="font-medium">{live.driverName ?? "Unassigned"}</dd></div>
          <div><dt className="text-muted-foreground">Next stop</dt><dd className="font-medium">{live.nextStop ?? "—"} {live.etaMinutes != null ? `· ${formatEta(live.etaMinutes)}` : ""}</dd></div>
        </dl>
        <div className="mt-5">
          <StopTimeline stops={live.stops} />
        </div>
        <div className="mt-5 flex flex-wrap gap-2">
          <Button asChild><Link to={track}>Track live</Link></Button>
          {user && (user.role === "Passenger" || user.role === "Admin") ? (
            <Button variant="outline" onClick={() => void save()} disabled={saving}>
              <Star className="h-4 w-4" /> Save
            </Button>
          ) : (
            <Button variant="outline" asChild><Link to={`/login?next=${encodeURIComponent(window.location.pathname + window.location.search)}`}>Sign in to save</Link></Button>
          )}
        </div>
        {note && <p className="mt-3 text-sm text-muted-foreground">{note}</p>}
      </section>
      <section className="min-h-[420px] overflow-hidden rounded-2xl border bg-card">
        <MapView path={live.path} stops={live.stops} bus={busPin} className="h-full min-h-[420px] w-full" />
      </section>
    </div>
  );
}
