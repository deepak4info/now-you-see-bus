import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import type { Favorite } from "@/lib/types";

export function FavoritesPage() {
  const query = useQuery<Favorite[]>("/api/favorites");

  async function remove(id: number) {
    await api(`/api/favorites/${id}`, { method: "DELETE" });
    await query.reload();
  }

  return (
    <div className="mx-auto max-w-3xl px-4 py-8">
      <h1 className="font-display text-4xl">Favourites</h1>
      <p className="mt-2 text-muted-foreground">Buses you asked to be told about. Meera already has the 8:00 AM Hisar service saved.</p>
      {query.loading && <p className="mt-6 text-muted-foreground">Loading your list…</p>}
      {query.error && (
        <div className="mt-6">
          <p>{query.error}</p>
          <Button className="mt-3" variant="outline" onClick={() => void query.reload()}>Try again</Button>
        </div>
      )}
      {query.data && query.data.length === 0 && (
        <div className="mt-6 rounded-xl border bg-card p-5">
          <p>Nothing saved yet.</p>
          <Button className="mt-3" asChild><Link to="/search?from=Hisar&to=Fatehabad">Find the Hisar bus</Link></Button>
        </div>
      )}
      <div className="mt-6 grid gap-3">
        {query.data?.map((item) => (
          <article key={item.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
            <div>
              <p className="font-medium">{item.label || `Bus ${item.fleetNumber}`}</p>
              <p className="text-sm text-muted-foreground">{item.registrationNumber} · Route {item.routeCode}</p>
            </div>
            <div className="flex gap-2">
              {item.fleetNumber && (
                <Button asChild>
                  <Link to={`/track/${item.fleetNumber}?${new URLSearchParams({
                    from: item.fromStop ?? "",
                    to: item.toStop ?? "",
                    ...(item.routeId != null ? { routeId: String(item.routeId) } : {}),
                  }).toString()}`}>Track</Link>
                </Button>
              )}
              <Button variant="outline" onClick={() => void remove(item.id)}>Remove</Button>
            </div>
          </article>
        ))}
      </div>
    </div>
  );
}
