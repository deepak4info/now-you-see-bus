import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { MapView } from "@/components/MapView";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { useQuery } from "@/lib/api";
import { useBusLive } from "@/lib/live";
import type { StopOption, TrackSnapshot } from "@/lib/types";

export function HomePage() {
  const stops = useQuery<StopOption[]>("/api/stops");
  const sample = useQuery<TrackSnapshot>("/api/track/102");
  const live = useBusLive("102", sample.data);
  const [from, setFrom] = useState("Hisar");
  const [to, setTo] = useState("Fatehabad");
  const [error, setError] = useState("");
  const navigate = useNavigate();

  function search(event: FormEvent) {
    event.preventDefault();
    if (!from || !to) {
      setError("Choose a boarding stop and a destination.");
      return;
    }
    if (from === to) {
      setError("Pick two different stops.");
      return;
    }
    navigate(`/search?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
  }

  const bus = live?.latitude != null && live.longitude != null
    ? { latitude: live.latitude, longitude: live.longitude, heading: live.heading, label: live.fleetNumber }
    : null;

  return (
    <div className="mx-auto max-w-6xl px-4 py-8 md:py-12">
      <div className="grid items-stretch gap-8 lg:grid-cols-[1.05fr_0.95fr]">
        <section>
          <p className="text-xs font-medium uppercase tracking-[0.22em] text-pine">Haryana Roadways · Route 101</p>
          <h1 className="mt-3 font-display text-5xl leading-[0.95] text-ink md:text-6xl">
            Where is my bus
          </h1>
          <p className="mt-4 max-w-xl text-lg text-muted-foreground">
            Hisar, Barwala, Tohana, Fatehabad. Search the pair of stops you care about, open the timetable, then watch the bus move without refreshing.
          </p>
          <form onSubmit={search} className="mt-8 grid gap-4 rounded-2xl border bg-card p-4 shadow-sm sm:p-5">
            <div className="grid gap-4 sm:grid-cols-2">
              <StopField label="From" value={from} stops={stops.data ?? []} onChange={setFrom} />
              <StopField label="To" value={to} stops={stops.data ?? []} onChange={setTo} />
            </div>
            {stops.error && <p className="text-sm text-destructive">{stops.error}</p>}
            {error && <p className="text-sm text-destructive">{error}</p>}
            <div className="flex flex-wrap gap-2">
              <Button type="submit" size="lg">Search buses</Button>
              <Button type="button" variant="outline" size="lg" onClick={() => navigate("/search?from=Hisar&to=Fatehabad")}>
                Hisar → Fatehabad
              </Button>
            </div>
          </form>
          <dl className="mt-6 grid grid-cols-3 gap-3 text-sm">
            <Stat k="Fleet" v="102" />
            <Stat k="Plate" v="HR39-1020" />
            <Stat k="Leaves" v="8:00 AM" />
          </dl>
        </section>
        <section className="overflow-hidden rounded-3xl border bg-card shadow-sm">
          <div className="flex items-center justify-between px-4 py-3">
            <div>
              <p className="text-xs uppercase tracking-wider text-muted-foreground">Corridor map</p>
              <p className="font-medium">{live ? `Bus ${live.fleetNumber} · ${live.status}` : "Route 101"}</p>
            </div>
            {live?.status === "Running" && (
              <span className="flex items-center gap-2 text-sm text-pine"><span className="pulse" /> Live</span>
            )}
          </div>
          <div className="h-[380px]">
            {sample.loading && <p className="p-4 text-sm text-muted-foreground">Drawing the route…</p>}
            {sample.error && (
              <div className="p-4">
                <p className="text-sm text-destructive">{sample.error}</p>
                <Button className="mt-3" variant="outline" onClick={() => void sample.reload()}>Try again</Button>
              </div>
            )}
            {live && (
              <MapView path={live.path} stops={live.stops} bus={bus} className="h-full w-full" />
            )}
          </div>
        </section>
      </div>
      <section className="mt-12 grid gap-4 md:grid-cols-3">
        <Step n="01" title="Search" body="Type the stop you are standing at and the stop you want. The timetable comes back with fare, time, and whether the bus has left." />
        <Step n="02" title="Track" body="Open the live map. The marker moves as the driver's phone, or the demo playback, sends GPS to the server." />
        <Step n="03" title="Arrive" body="Each stop gets an ETA from the remaining distance at 42 km/h. When the bus reaches Fatehabad, the trip completes on your screen." />
      </section>
    </div>
  );
}

function StopField({ label, value, stops, onChange }: { label: string; value: string; stops: StopOption[]; onChange: (value: string) => void }) {
  const id = label.toLowerCase();
  return (
    <div className="grid gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <select id={id} className="h-10 rounded-md border bg-card px-3 text-sm" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Select a stop</option>
        {stops.map((stop) => <option key={stop.id} value={stop.name}>{stop.name}</option>)}
      </select>
    </div>
  );
}

function Stat({ k, v }: { k: string; v: string }) {
  return (
    <div className="rounded-xl border bg-card/70 px-3 py-2">
      <dt className="text-xs uppercase tracking-wider text-muted-foreground">{k}</dt>
      <dd className="font-medium">{v}</dd>
    </div>
  );
}

function Step({ n, title, body }: { n: string; title: string; body: string }) {
  return (
    <article className="rounded-2xl border bg-card p-5">
      <p className="font-display text-clay">{n}</p>
      <h2 className="mt-1 font-display text-2xl">{title}</h2>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">{body}</p>
    </article>
  );
}
