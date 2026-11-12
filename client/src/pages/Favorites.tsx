import { useState } from "react";
import { Bookmark, Bus, MapPin, Navigation, Plus, Route, Search, Trash2 } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { useI18n } from "@/lib/i18n";
import type { Favorite, PublicRoute, StopOption } from "@/lib/types";

export function FavoritesPage() {
  const { t } = useI18n();
  const query = useQuery<Favorite[]>("/api/favorites");
  const stopsQuery = useQuery<StopOption[]>("/api/stops");
  const routesQuery = useQuery<PublicRoute[]>("/api/routes");

  const [savingType, setSavingType] = useState<"stop" | "route">("stop");
  const [selectedStopId, setSelectedStopId] = useState<number>(0);
  const [fromStopId, setFromStopId] = useState<number>(0);
  const [toStopId, setToStopId] = useState<number>(0);
  const [selectedRouteId, setSelectedRouteId] = useState<number>(0);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [feedback, setFeedback] = useState("");

  async function remove(id: number) {
    await api(`/api/favorites/${id}`, { method: "DELETE" });
    await query.reload();
  }

  async function handleAddFavorite(e: React.FormEvent) {
    e.preventDefault();
    setIsSubmitting(true);
    setFeedback("");
    try {
      if (savingType === "stop") {
        const stopObj = stopsQuery.data?.find((s) => s.id === selectedStopId);
        if (!stopObj) {
          setFeedback("Please pick a bus stop.");
          return;
        }
        await api("/api/favorites", {
          method: "POST",
          body: JSON.stringify({
            fromStopId: stopObj.id,
            label: `Bus Stop · ${stopObj.name}`,
          }),
        });
      } else {
        const routeObj = routesQuery.data?.find((r) => r.id === selectedRouteId);
        const fromStopObj = stopsQuery.data?.find((s) => s.id === fromStopId);
        const toStopObj = stopsQuery.data?.find((s) => s.id === toStopId);

        if (!routeObj && (!fromStopObj || !toStopObj)) {
          setFeedback("Please select a route corridor or both starting and ending stops.");
          return;
        }

        await api("/api/favorites", {
          method: "POST",
          body: JSON.stringify({
            routeId: routeObj?.id ?? null,
            fromStopId: fromStopObj?.id ?? null,
            toStopId: toStopObj?.id ?? null,
            label: routeObj ? `Route ${routeObj.code}: ${routeObj.origin} → ${routeObj.destination}` : `${fromStopObj?.name} → ${toStopObj?.name}`,
          }),
        });
      }
      setFeedback("Saved to your favourites.");
      await query.reload();
    } catch (err) {
      setFeedback(err instanceof Error ? err.message : "Could not save favourite.");
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <div className="mx-auto max-w-4xl px-4 py-8">
      <div className="border-b pb-5">
        <h1 className="font-display text-3xl sm:text-4xl text-ink">{t("favoritesTitle")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("favoritesSubtitle")}</p>
      </div>

      {/* Add New Favorite Section */}
      <section className="mt-6 rounded-2xl border bg-card p-4 sm:p-5 shadow-xs">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b pb-3">
          <p className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-pine">
            <Plus className="h-4 w-4" /> Save New Favourite
          </p>
          <div className="flex items-center gap-1.5 text-xs">
            <button
              type="button"
              onClick={() => { setSavingType("stop"); setFeedback(""); }}
              className={`rounded-full px-3 py-1 font-medium transition-colors ${
                savingType === "stop" ? "bg-pine text-white" : "border bg-card text-muted-foreground hover:bg-muted"
              }`}
            >
              {t("saveStopTab")}
            </button>
            <button
              type="button"
              onClick={() => { setSavingType("route"); setFeedback(""); }}
              className={`rounded-full px-3 py-1 font-medium transition-colors ${
                savingType === "route" ? "bg-pine text-white" : "border bg-card text-muted-foreground hover:bg-muted"
              }`}
            >
              {t("saveRouteTab")}
            </button>
          </div>
        </div>

        <form onSubmit={handleAddFavorite} className="mt-4 space-y-3">
          {savingType === "stop" ? (
            <div>
              <label className="text-xs font-medium text-ink">Choose Bus Stop</label>
              <select
                value={selectedStopId}
                onChange={(e) => setSelectedStopId(Number(e.target.value))}
                className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-xs text-ink focus:outline-pine"
              >
                <option value={0}>Select a bus stop to save…</option>
                {stopsQuery.data?.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name} ({s.code}){s.city ? ` · ${s.city}` : ""}
                  </option>
                ))}
              </select>
            </div>
          ) : (
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label className="text-xs font-medium text-ink">From Stop</label>
                <select
                  value={fromStopId}
                  onChange={(e) => setFromStopId(Number(e.target.value))}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-xs text-ink focus:outline-pine"
                >
                  <option value={0}>Starting bus stop…</option>
                  {stopsQuery.data?.map((s) => (
                    <option key={s.id} value={s.id}>{s.name}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="text-xs font-medium text-ink">To Stop</label>
                <select
                  value={toStopId}
                  onChange={(e) => setToStopId(Number(e.target.value))}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-2 text-xs text-ink focus:outline-pine"
                >
                  <option value={0}>Destination bus stop…</option>
                  {stopsQuery.data?.map((s) => (
                    <option key={s.id} value={s.id}>{s.name}</option>
                  ))}
                </select>
              </div>
            </div>
          )}

          <div className="flex items-center justify-between pt-1">
            {feedback && <p className="text-xs text-pine font-medium" role="status">{feedback}</p>}
            <Button type="submit" size="sm" disabled={isSubmitting} className="ml-auto">
              <Bookmark className="mr-1.5 h-3.5 w-3.5" /> Save Favourite
            </Button>
          </div>
        </form>
      </section>

      {/* List of Saved Favorites */}
      <section className="mt-8">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-muted-foreground">Your Saved List</h2>
        {query.loading && <p className="mt-4 text-xs text-muted-foreground">Loading your favourites…</p>}
        {query.error && (
          <div className="mt-4">
            <p className="text-xs text-destructive">{query.error}</p>
            <Button className="mt-2" size="sm" variant="outline" onClick={() => void query.reload()}>Try again</Button>
          </div>
        )}
        {query.data && query.data.length === 0 && (
          <div className="mt-4 rounded-xl border border-dashed bg-card p-6 text-center">
            <Bookmark className="mx-auto h-8 w-8 text-muted-foreground/50" />
            <p className="mt-2 text-sm font-semibold text-ink">Nothing saved yet</p>
            <p className="mt-1 text-xs text-muted-foreground">Save your daily bus stops, routes, and buses for one-click tracking.</p>
          </div>
        )}

        <div className="mt-4 grid gap-3">
          {query.data?.map((item) => (
            <article key={item.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4 transition-colors hover:border-pine/30">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <span
                    className={`rounded-md px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${
                      item.kind === "stop"
                        ? "bg-purple-100 text-purple-800 dark:bg-purple-950 dark:text-purple-300"
                        : item.kind === "route"
                        ? "bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300"
                        : "bg-emerald-100 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300"
                    }`}
                  >
                    {item.kind === "stop" ? "Bus Stop" : item.kind === "route" ? "Route" : "Bus"}
                  </span>
                  <p className="font-semibold text-ink">{item.label || `Bus ${item.fleetNumber}`}</p>
                </div>
                <p className="text-xs text-muted-foreground">
                  {item.fromStop && item.toStop
                    ? `${item.fromStop} → ${item.toStop}`
                    : item.fromStop
                    ? `Stop: ${item.fromStop}`
                    : item.registrationNumber
                    ? `${item.registrationNumber} · Route ${item.routeCode}`
                    : `Route ${item.routeCode}`}
                </p>
              </div>

              <div className="flex items-center gap-2">
                {item.fleetNumber ? (
                  <Button size="sm" asChild>
                    <Link to={`/track/${encodeURIComponent(item.fleetNumber)}`}>
                      <Navigation className="mr-1 h-3.5 w-3.5" /> Track
                    </Link>
                  </Button>
                ) : item.fromStop && item.toStop ? (
                  <Button size="sm" asChild>
                    <Link to={`/search?from=${encodeURIComponent(item.fromStop)}&to=${encodeURIComponent(item.toStop)}`}>
                      <Search className="mr-1 h-3.5 w-3.5" /> Search
                    </Link>
                  </Button>
                ) : item.fromStop ? (
                  <Button size="sm" asChild>
                    <Link to={`/search?from=${encodeURIComponent(item.fromStop)}&to=Delhi`}>
                      <Search className="mr-1 h-3.5 w-3.5" /> Buses
                    </Link>
                  </Button>
                ) : null}
                <Button variant="ghost" size="icon" onClick={() => void remove(item.id)} aria-label="Remove favourite">
                  <Trash2 className="h-4 w-4 text-muted-foreground hover:text-destructive" />
                </Button>
              </div>
            </article>
          ))}
        </div>
      </section>
    </div>
  );
}
