import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { MapView } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatClock } from "@/lib/format";
import { useBusLive } from "@/lib/live";
import type { TrackSnapshot } from "@/lib/types";

type Dashboard = {
  driverName: string;
  driverCode: string | null;
  licenseNumber: string;
  operationsLive: boolean;
  serviceDate: string;
  bus: null | {
    id: number;
    fleetNumber: string;
    registrationNumber: string;
    busType: string;
    model: string | null;
    capacity: number;
    operatorName: string;
  };
  route: null | { id: number; code: string; name: string; distanceKm: number | null; stops: string[] };
  trips: { id: number; status: string; simulation: boolean; fleetNumber: string; routeCode: string; routeName: string; departureTime: string }[];
};

export function DriverPage() {
  const { user, logout } = useAuth();
  const board = useQuery<Dashboard>("/api/driver/dashboard");
  const [tripId, setTripId] = useState<number | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [gpsOn, setGpsOn] = useState(false);
  const selected = board.data?.trips.find((t) => t.id === tripId) ?? board.data?.trips[0];
  const fleet = selected?.fleetNumber ?? board.data?.bus?.fleetNumber ?? null;
  const seed = useQuery<TrackSnapshot>(fleet ? `/api/track/${fleet}` : null);
  const live = useBusLive(fleet, seed.data);

  useEffect(() => {
    if (!tripId && board.data?.trips[0]) setTripId(board.data.trips[0].id);
  }, [board.data, tripId]);

  useEffect(() => {
    if (!gpsOn || !selected || selected.status !== "Running") return;
    if (!navigator.geolocation) {
      setMessage("This browser has no GPS. Play the route instead.");
      return;
    }
    const watch = navigator.geolocation.watchPosition(async (pos) => {
      try {
        await api(`/api/driver/trips/${selected.id}/location`, {
          method: "POST",
          body: JSON.stringify({
            latitude: pos.coords.latitude,
            longitude: pos.coords.longitude,
            speedKmph: pos.coords.speed != null && pos.coords.speed >= 0 ? +(pos.coords.speed * 3.6).toFixed(1) : null,
            heading: pos.coords.heading,
          }),
        });
        setMessage("GPS is on. Your phone is sending the bus location.");
      } catch (err) {
        setMessage(err instanceof Error ? err.message : "Could not send the location");
      }
    }, (err) => setMessage(`${err.message} Play the Hisar–Fatehabad run instead.`), { enableHighAccuracy: true, maximumAge: 4000 });
    return () => navigator.geolocation.clearWatch(watch);
  }, [gpsOn, selected]);

  async function start(mode: "gps" | "simulate") {
    if (!selected) return;
    setBusy(true);
    setMessage("");
    try {
      if (selected.status === "Running" && mode === "simulate") {
        await api(`/api/driver/trips/${selected.id}/simulate`, { method: "POST" });
      } else if (selected.status !== "Running") {
        await api(`/api/driver/trips/${selected.id}/start`, { method: "POST", body: JSON.stringify({ mode }) });
      }
      setGpsOn(mode === "gps");
      setMessage(mode === "gps" ? "Trip started. GPS is turning on." : "Trip started. Demo playback is moving the bus.");
      await board.reload();
      await seed.reload();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Could not start");
    } finally {
      setBusy(false);
    }
  }

  async function replay() {
    if (!selected) return;
    setBusy(true);
    try {
      await api(`/api/driver/trips/${selected.id}/replay`, { method: "POST" });
      setGpsOn(false);
      setMessage("Trip reset. Start it again when you are ready.");
      await board.reload();
      await seed.reload();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Could not reset the trip");
    } finally {
      setBusy(false);
    }
  }

  async function endTrip() {
    if (!selected) return;
    setBusy(true);
    try {
      await api(`/api/driver/trips/${selected.id}/end`, { method: "POST" });
      setGpsOn(false);
      setMessage("Trip ended.");
      await board.reload();
      await seed.reload();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Could not end the trip");
    } finally {
      setBusy(false);
    }
  }

  async function confirmArrival(stopId: number) {
    if (!selected) return;
    setBusy(true);
    try {
      await api(`/api/driver/trips/${selected.id}/stops/${stopId}/arrival`, { method: "POST" });
      setMessage("Station arrival recorded. Upcoming ETAs updated.");
      await seed.reload();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Could not record arrival");
    } finally {
      setBusy(false);
    }
  }

  async function confirmDeparture(stopId: number) {
    if (!selected) return;
    setBusy(true);
    try {
      await api(`/api/driver/trips/${selected.id}/stops/${stopId}/departure`, { method: "POST" });
      setMessage("Station departure recorded. Upcoming ETAs updated.");
      await seed.reload();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Could not record departure");
    } finally {
      setBusy(false);
    }
  }

  const pin = live?.recordedAt != null && live.latitude != null && live.longitude != null
    ? { latitude: live.latitude, longitude: live.longitude, heading: live.heading, label: live.fleetNumber }
    : null;

  return (
    <div className="min-h-screen bg-ink text-paper">
      <header className="flex items-center justify-between px-4 py-4 md:px-8">
        <div>
          <p className="text-xs uppercase tracking-[0.2em] text-gold">Driver console</p>
          <p className="font-display text-2xl">{user?.fullName}</p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" className="border-white/20 bg-transparent text-paper hover:bg-white/10" asChild><Link to="/">Passenger view</Link></Button>
          <Button variant="outline" className="border-white/20 bg-transparent text-paper hover:bg-white/10" onClick={logout}>Sign out</Button>
        </div>
      </header>
      <div className="grid gap-4 px-4 pb-10 md:px-8 lg:grid-cols-[420px_1fr]">
        <section className="rounded-2xl bg-white/5 p-5">
          {board.loading && <p>Loading your assignment…</p>}
          {board.error && <p>{board.error}</p>}
          {board.data && !board.data.bus && <p>No bus is assigned to you today. Ask dispatch to attach you to a schedule.</p>}
          {board.data?.bus && (
            <>
              <p className="text-sm text-white/70">{board.data.bus.operatorName}</p>
              <h1 className="font-display text-5xl">{board.data.bus.registrationNumber}</h1>
              <p className="mt-1">Fleet {board.data.bus.fleetNumber} · {board.data.bus.busType} · {board.data.driverCode}</p>
              {board.data.route && (
                <p className="mt-3 text-sm text-white/80">Route {board.data.route.code}: {board.data.route.stops.join(" → ")}</p>
              )}
              {!board.data.operationsLive && (
                <p className="mt-4 rounded-xl bg-clay/90 px-3 py-2 text-sm">Dispatch has not started operations. The start button stays locked until then.</p>
              )}
              <div className="mt-4 grid gap-2">
                {board.data.trips.map((trip) => (
                  <button key={trip.id} onClick={() => setTripId(trip.id)} className={`rounded-xl px-3 py-3 text-left ${selected?.id === trip.id ? "bg-white text-ink" : "bg-white/10"}`}>
                    <span className="flex items-center justify-between gap-2">
                      <span>{formatClock(trip.departureTime)} · Bus {trip.fleetNumber}</span>
                      <StatusBadge status={trip.status} />
                    </span>
                    <span className="mt-1 block text-sm opacity-70">{trip.routeName}</span>
                  </button>
                ))}
                {board.data.trips.length === 0 && <p className="text-sm text-white/70">No trip has been opened for today. Start operations in dispatch first.</p>}
              </div>
              {selected && selected.status !== "Completed" && (
                <div className="mt-5 grid gap-2">
                  <Button size="lg" disabled={busy || !board.data.operationsLive} onClick={() => void start("gps")}>Start trip</Button>
                  <Button size="lg" variant="outline" className="border-white/30 bg-transparent text-paper hover:bg-white/10" disabled={busy || !board.data.operationsLive} onClick={() => void start("simulate")}>
                    Play the Hisar–Fatehabad run
                  </Button>
                  {selected.status === "Running" && (
                    <Button size="lg" variant="destructive" disabled={busy} onClick={() => void endTrip()}>End trip</Button>
                  )}
                </div>
              )}
              {selected && selected.status === "Completed" && (
                <Button className="mt-5" size="lg" disabled={busy} onClick={() => void replay()}>Run this trip again</Button>
              )}
              {selected?.status === "Running" && live?.stops && (
                <div className="mt-5 border-t border-white/20 pt-4">
                  <p className="text-xs uppercase tracking-wider text-gold font-semibold mb-2">Station Confirmation (Approach B)</p>
                  <div className="max-h-60 overflow-y-auto space-y-2 pr-1">
                    {live.stops.map((stop) => (
                      <div key={stop.stopId} className="flex items-center justify-between rounded-xl bg-white/10 p-2.5 text-xs">
                        <div className="min-w-0 flex-1">
                          <p className="font-semibold text-white truncate">{stop.name}</p>
                          <p className="text-[11px] opacity-70">
                            {stop.displayStatus || stop.status} · {formatClock(stop.estimatedArrival || stop.scheduledTime)}
                          </p>
                        </div>
                        <div className="flex gap-1.5 shrink-0 ml-2">
                          {stop.status !== "Passed" && stop.displayStatus !== "Departed" && (
                            <button
                              type="button"
                              disabled={busy}
                              onClick={() => void confirmArrival(stop.stopId)}
                              className="rounded-md bg-amber-500 hover:bg-amber-600 px-2.5 py-1 text-[11px] font-semibold text-black transition-colors"
                            >
                              Reached
                            </button>
                          )}
                          {stop.status !== "Passed" && stop.displayStatus !== "Departed" && (
                            <button
                              type="button"
                              disabled={busy}
                              onClick={() => void confirmDeparture(stop.stopId)}
                              className="rounded-md bg-emerald-600 hover:bg-emerald-700 px-2.5 py-1 text-[11px] font-semibold text-white transition-colors"
                            >
                              Departed
                            </button>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}
              {message && <p className="mt-3 text-sm text-gold">{message}</p>}
              {live?.status === "Running" && (
                <p className="mt-3 text-sm">Now {live.currentStop ?? "between stops"} · next {live.nextStop ?? "destination"}.</p>
              )}
            </>
          )}
        </section>
        <section className="min-h-[420px] overflow-hidden rounded-2xl">
          {live && <MapView path={live.path} stops={live.stops} bus={pin} focusBus={live.status === "Running"} className="h-full min-h-[420px] w-full" />}
        </section>
      </div>
    </div>
  );
}
