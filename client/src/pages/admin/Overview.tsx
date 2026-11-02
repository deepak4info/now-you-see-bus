import { useState } from "react";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";

type Dash = {
  operationsLive: boolean;
  serviceDate: string;
  operators: number;
  buses: number;
  drivers: number;
  stops: number;
  routes: number;
  schedules: number;
  tripsToday: number;
  runningNow: number;
  completedToday: number;
  delayedNow: number;
  recentAudit: { id: number; action: string; entity: string; detail: string | null; createdAt: string }[];
};

export function OverviewPage() {
  const query = useQuery<Dash>("/api/admin/dashboard");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);

  async function start() {
    setBusy(true);
    setNote("");
    try {
      const result = await api<{ tripsCreated: number; serviceDate: string }>("/api/admin/operations/start", { method: "POST" });
      setNote(`Operations are live for ${result.serviceDate}. ${result.tripsCreated} trip${result.tripsCreated === 1 ? "" : "s"} opened for drivers.`);
      await query.reload();
    } catch (err) {
      setNote(err instanceof Error ? err.message : "Could not start operations");
    } finally {
      setBusy(false);
    }
  }

  if (query.loading) return <p className="text-muted-foreground">Loading the control room…</p>;
  if (query.error || !query.data) return <p>{query.error ?? "Dashboard unavailable."}</p>;
  const data = query.data;
  const cards = [
    ["Operators", data.operators],
    ["Buses", data.buses],
    ["Drivers", data.drivers],
    ["Stops", data.stops],
    ["Routes", data.routes],
    ["Schedules", data.schedules],
    ["Trips today", data.tripsToday],
    ["On the road", data.runningNow],
    ["Completed", data.completedToday],
    ["Delayed", data.delayedNow],
  ];

  return (
    <div>
      <p className="text-xs uppercase tracking-[0.18em] text-pine">Service date {data.serviceDate}</p>
      <h1 className="font-display text-4xl">Start the day</h1>
      <p className="mt-2 max-w-2xl text-muted-foreground">
        The sample corridor is already loaded: Haryana Roadways, bus HR39-1020, stops Hisar → Barwala → Tohana → Fatehabad, route 101, the 8:00 AM schedule, and driver Rahul.
      </p>
      <div className="mt-6 grid grid-cols-2 gap-3 md:grid-cols-5">
        {cards.map(([label, value]) => (
          <div key={String(label)} className="rounded-xl border bg-card p-3">
            <p className="text-xs uppercase tracking-wider text-muted-foreground">{label}</p>
            <p className="font-display text-3xl">{value}</p>
          </div>
        ))}
      </div>
      <div className="mt-6 rounded-2xl border bg-card p-5">
        <h2 className="font-display text-2xl">{data.operationsLive ? "Operations are live" : "Operations are still closed"}</h2>
        <p className="mt-1 text-sm text-muted-foreground">Starting operations opens today's trips. While the system stays live, each new morning opens the next day's trips on its own.</p>
        <Button className="mt-4" size="lg" disabled={busy} onClick={() => void start()}>
          {data.operationsLive ? "Refresh today's trips" : "Start operations"}
        </Button>
        {note && <p className="mt-3 text-sm">{note}</p>}
      </div>
      <section className="mt-6">
        <h2 className="font-display text-2xl">Recent changes</h2>
        {data.recentAudit.length === 0 && <p className="mt-2 text-sm text-muted-foreground">Nothing has been recorded yet.</p>}
        <ul className="mt-3 divide-y rounded-xl border bg-card">
          {data.recentAudit.map((row) => (
            <li key={row.id} className="px-4 py-3 text-sm">
              <span className="font-medium">{row.action}</span> {row.entity} {row.detail && <span className="text-muted-foreground">· {row.detail}</span>}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
