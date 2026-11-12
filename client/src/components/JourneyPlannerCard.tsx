import { ArrowRight, Bus, Clock, GitFork, Info, MapPin, ShieldCheck, Ticket } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { formatClock, rupee } from "@/lib/format";
import type { JourneyOption, MultiRoutePlan } from "@/lib/types";

function formatMinutes(mins: number): string {
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

export function JourneyPlannerCard({
  plan,
  date,
}: {
  plan: MultiRoutePlan;
  date: string;
}) {
  if (!plan || plan.allOptions.length === 0) return null;

  return (
    <section className="space-y-4 rounded-2xl border border-border/80 bg-card p-4 shadow-sm sm:p-5" aria-label="Journey Planner">
      {/* Header Status Banner */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/70 pb-3.5">
        <div>
          <div className="flex items-center gap-2">
            <span className="flex h-6 w-6 items-center justify-center rounded-full bg-pine/10 text-pine">
              <GitFork className="h-3.5 w-3.5" aria-hidden="true" />
            </span>
            <h2 className="text-sm font-bold uppercase tracking-wider text-ink">
              Multi-Route Journey Planner
            </h2>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            {plan.from} → {plan.to} · {plan.allOptions.length} route options found for {date}
          </p>
        </div>

        <div>
          {plan.directAvailable ? (
            <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-500/10 px-3 py-1 text-xs font-semibold text-emerald-700 dark:text-emerald-400">
              <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" /> Direct bus available
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 rounded-full bg-amber-500/10 px-3 py-1 text-xs font-semibold text-amber-700 dark:text-amber-400">
              <Info className="h-3.5 w-3.5" aria-hidden="true" /> No direct bus · Connecting routes found
            </span>
          )}
        </div>
      </div>

      {/* Options List */}
      <div className="space-y-3">
        {plan.allOptions.map((option, idx) => (
          <JourneyOptionItem key={option.optionId || idx} option={option} from={plan.from} to={plan.to} date={date} isRecommended={idx === 0} />
        ))}
      </div>
    </section>
  );
}

function JourneyOptionItem({
  option,
  from,
  to,
  date,
  isRecommended,
}: {
  option: JourneyOption;
  from: string;
  to: string;
  date: string;
  isRecommended?: boolean;
}) {
  return (
    <article className="rounded-xl border border-border/70 bg-muted/20 p-3.5 transition-colors hover:border-pine/40 hover:bg-muted/40">
      {/* Top summary row */}
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border/50 pb-2.5">
        <div className="flex flex-wrap items-center gap-2">
          {isRecommended && (
            <span className="rounded-md bg-pine px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide text-white">
              Recommended
            </span>
          )}
          <span
            className={`rounded-md px-2 py-0.5 text-xs font-semibold ${
              option.isDirect
                ? "bg-emerald-100 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300"
                : "bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300"
            }`}
          >
            {option.isDirect ? "Direct · 0 Transfers" : `${option.transfersCount} Transfer · ${option.transferStops.join(", ")}`}
          </span>
          <span className="text-xs text-muted-foreground">{option.totalDistanceKm} km</span>
        </div>

        <div className="flex items-center gap-3 text-right">
          <div>
            <p className="flex items-center gap-1 text-xs font-semibold text-muted-foreground">
              <Clock className="h-3 w-3" aria-hidden="true" /> Total time
            </p>
            <p className="text-sm font-bold text-ink">{formatMinutes(option.totalJourneyMinutes)}</p>
          </div>
          <div className="border-l border-border pl-3">
            <p className="flex items-center gap-1 text-xs font-semibold text-muted-foreground">
              <Ticket className="h-3 w-3" aria-hidden="true" /> Total fare
            </p>
            <p className="text-sm font-bold text-ink">{rupee(option.estimatedTotalFare)}</p>
          </div>
        </div>
      </div>

      {/* Departure → Arrival Header */}
      <div className="mt-2.5 flex items-center justify-between text-xs">
        <span className="font-semibold text-ink">
          Departs {formatClock(option.departureTime)} ({from})
        </span>
        <ArrowRight className="h-3.5 w-3.5 text-muted-foreground" aria-hidden="true" />
        <span className="font-semibold text-ink">
          Arrives ~{formatClock(option.arrivalTime)} ({to})
        </span>
      </div>

      {/* Visual Leg-by-leg timeline */}
      <div className="mt-3 space-y-2 border-l-2 border-pine/40 pl-3">
        {option.legs.map((leg, legIdx) => (
          <div key={legIdx} className="space-y-1">
            <div className="flex flex-wrap items-center justify-between gap-1 text-xs">
              <div className="flex items-center gap-1.5 font-medium text-ink">
                <Bus className="h-3.5 w-3.5 text-pine" aria-hidden="true" />
                <span>Leg {legIdx + 1}: Route {leg.routeCode}</span>
                {leg.fleetNumber && (
                  <span className="text-muted-foreground">· Bus {leg.fleetNumber}</span>
                )}
              </div>
              <span className="text-muted-foreground">
                {formatClock(leg.departureTime)} → {formatClock(leg.arrivalTime)} ({formatMinutes(leg.durationMinutes)}) · {rupee(leg.estimatedFare)}
              </span>
            </div>
            <p className="text-[11px] text-muted-foreground">
              {leg.fromStop} → {leg.toStop} ({leg.distanceKm} km)
            </p>

            {/* If there is a next leg, render the transfer layover box */}
            {legIdx < option.legs.length - 1 && (
              <div className="my-1.5 rounded-lg border border-dashed border-amber-500/50 bg-amber-500/10 px-2.5 py-1.5 text-xs text-amber-900 dark:text-amber-200">
                <p className="font-semibold">
                  Transfer at {leg.toStop}
                </p>
                <p className="text-[11px] text-amber-800/80 dark:text-amber-300/80">
                  Layover wait time: ~{formatMinutes(option.totalWaitMinutes || 15)}. Switch to Route {option.legs[legIdx + 1]?.routeCode} bay.
                </p>
              </div>
            )}
          </div>
        ))}
      </div>

      {/* Actions */}
      <div className="mt-3 flex flex-wrap items-center justify-between gap-2 border-t border-border/40 pt-2 text-xs">
        <span className="text-muted-foreground">{option.summary}</span>
        {option.legs[0]?.fleetNumber && (
          <Button variant="outline" size="sm" asChild className="h-7 text-xs">
            <Link to={`/track/${encodeURIComponent(option.legs[0].fleetNumber)}?date=${date}`}>
              Track Leg 1 bus
            </Link>
          </Button>
        )}
      </div>
    </article>
  );
}
