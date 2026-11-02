import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import type { Notice } from "@/lib/types";

export function NotificationsPage() {
  const query = useQuery<{ unread: number; items: Notice[] }>("/api/notifications");

  async function readAll() {
    await api("/api/notifications/read-all", { method: "POST" });
    await query.reload();
  }

  return (
    <div className="mx-auto max-w-3xl px-4 py-8">
      <div className="flex items-end justify-between gap-3">
        <div>
          <h1 className="font-display text-4xl">Alerts</h1>
          <p className="mt-2 text-muted-foreground">Start and arrival notes for buses you have saved.</p>
        </div>
        {!!query.data?.unread && <Button variant="outline" onClick={() => void readAll()}>Mark all read</Button>}
      </div>
      {query.loading && <p className="mt-6 text-muted-foreground">Looking for alerts…</p>}
      {query.error && <p className="mt-6">{query.error}</p>}
      {query.data && query.data.items.length === 0 && (
        <p className="mt-6 rounded-xl border bg-card p-5 text-sm">No alerts yet. Save bus 102, then have Rahul start the trip.</p>
      )}
      <div className="mt-6 grid gap-3">
        {query.data?.items.map((item) => (
          <article key={item.id} className={`rounded-xl border bg-card p-4 ${item.isRead ? "opacity-70" : ""}`}>
            <p className="text-xs uppercase tracking-wider text-muted-foreground">{item.kind}</p>
            <h2 className="font-medium">{item.title}</h2>
            <p className="text-sm text-muted-foreground">{item.body}</p>
          </article>
        ))}
      </div>
    </div>
  );
}
