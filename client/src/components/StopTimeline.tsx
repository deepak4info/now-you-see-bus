import { formatClock, formatEta } from "@/lib/format";
import type { StopOnTrip } from "@/lib/types";
import { cn } from "@/lib/utils";

export function StopTimeline({ stops }: { stops: StopOnTrip[] }) {
  return (
    <ol>
      {stops.map((stop, index) => {
        const last = index === stops.length - 1;
        return (
          <li key={stop.stopId} className="grid grid-cols-[18px_1fr] gap-3">
            <div className="flex flex-col items-center">
              <span
                className={cn(
                  "mt-1 h-3 w-3 rounded-full border-2 border-card",
                  stop.status === "Current" && "bg-clay",
                  stop.status === "Passed" && "bg-pine",
                  stop.status === "Next" && "bg-ink ring-2 ring-gold",
                  (stop.status === "Pending" || !stop.status) && "bg-ink/25",
                )}
              />
              {!last && <span className="w-px flex-1 bg-ink/15" />}
            </div>
            <div className={cn("pb-4", last && "pb-0")}>
              <div className="flex items-baseline justify-between gap-3">
                <p className={cn("font-medium", stop.status === "Current" && "text-clay")}>{stop.name}</p>
                <p className="text-sm text-muted-foreground">{formatClock(stop.scheduledTime)}</p>
              </div>
              <p className="text-xs text-muted-foreground">
                {stop.distanceKm.toFixed(0)} km
                {stop.status === "Current" && " · bus is here"}
                {stop.status === "Next" && stop.etaMinutes != null && ` · ETA ${formatEta(stop.etaMinutes)}`}
                {stop.status === "Passed" && " · passed"}
              </p>
            </div>
          </li>
        );
      })}
    </ol>
  );
}
