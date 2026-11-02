import { formatClock, formatEta } from "@/lib/format";
import type { StopOnTrip } from "@/lib/types";
import { cn } from "@/lib/utils";

export function StopTimeline({
  stops,
  serviceDate,
  boardingStop,
  showTitle = true,
}: {
  stops: StopOnTrip[];
  serviceDate?: string;
  boardingStop?: string;
  showTitle?: boolean;
}) {
  let previousMinute = -1;
  let dayOffset = 0;

  return (
    <div className="space-y-4">
      {showTitle && (
        <div className="flex items-center justify-between border-b pb-2">
          <h3 className="text-base font-semibold text-ink">Upcoming station arrival times</h3>
          <span className="text-xs text-muted-foreground">{stops.length} stations</span>
        </div>
      )}

      <ol className="relative">
        {stops.map((stop, index) => {
          const last = index === stops.length - 1;
          const [hour, minute] = (stop.scheduledTime || "00:00").split(":").map(Number);
          const scheduledMinute = (Number.isNaN(hour) ? 0 : hour) * 60 + (Number.isNaN(minute) ? 0 : minute);
          if (previousMinute >= 0 && scheduledMinute < previousMinute) dayOffset += 1;
          previousMinute = scheduledMinute;
          const stopDate = serviceDate ? dateForStop(serviceDate, dayOffset) : null;
          const isBoardingStop = boardingStop?.toLocaleLowerCase() === stop.name.toLocaleLowerCase();

          // Determine status category
          const isDeparted = stop.displayStatus === "Departed" || stop.status === "Passed";
          const isArrived = stop.displayStatus === "Arrived" || stop.status === "Current";
          const isDelayed = stop.displayStatus === "Delayed" || (stop.delayMinutes != null && stop.delayMinutes > 3 && !isDeparted && !isArrived);
          const isOnTime = stop.displayStatus === "On time" || (stop.delayMinutes != null && Math.abs(stop.delayMinutes) <= 3 && !isDeparted && !isArrived);

          const statusBadge = isDeparted
            ? { text: "Departed", className: "bg-muted text-muted-foreground border-border" }
            : isArrived
            ? { text: "Arrived", className: "bg-amber-500/15 text-amber-900 border-amber-500/30 font-medium" }
            : isDelayed
            ? { text: `Delayed · +${stop.delayMinutes}m`, className: "bg-red-500/10 text-red-700 border-red-500/20 font-medium" }
            : isOnTime
            ? { text: "On time", className: "bg-emerald-500/10 text-emerald-800 border-emerald-500/20 font-medium" }
            : { text: "Scheduled", className: "bg-muted/60 text-muted-foreground border-transparent" };

          const scheduledDisplay = formatClock(stop.scheduledArrival || stop.scheduledDeparture || stop.scheduledTime);
          const estimatedDisplay = formatClock(stop.estimatedArrival || stop.estimatedDeparture || stop.scheduledTime);
          const actualDisplay = formatClock(stop.actualDeparture || stop.actualArrival);

          return (
            <li key={stop.stopId} className="grid grid-cols-[20px_1fr] gap-3 pb-5 last:pb-0">
              {/* Timeline Connector */}
              <div className="flex flex-col items-center">
                <span
                  className={cn(
                    "mt-1.5 h-3.5 w-3.5 rounded-full border-2 transition-colors",
                    isDeparted && "border-emerald-600 bg-emerald-600",
                    isArrived && "border-amber-500 bg-amber-500 ring-4 ring-amber-500/20 animate-pulse",
                    isDelayed && "border-red-500 bg-red-100 ring-2 ring-red-400",
                    isOnTime && "border-emerald-600 bg-emerald-50",
                    !isDeparted && !isArrived && !isDelayed && !isOnTime && "border-border bg-card",
                  )}
                />
                {!last && (
                  <span
                    className={cn(
                      "w-0.5 flex-1 mt-1",
                      isDeparted ? "bg-emerald-600/70" : "bg-border/60 border-l border-dashed",
                    )}
                  />
                )}
              </div>

              {/* Station Content */}
              <div className="min-w-0 rounded-xl border bg-card/60 p-3 shadow-xs transition-colors hover:bg-card">
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 flex-wrap">
                      <span className="text-xs font-semibold text-muted-foreground">{index + 1}.</span>
                      <p className={cn("font-semibold text-ink leading-tight", (isArrived || isBoardingStop) && "text-clay")}>
                        {stop.name}
                      </p>
                      <span className={cn("rounded-full border px-2 py-0.5 text-[11px]", statusBadge.className)}>
                        {statusBadge.text}
                      </span>
                      {isBoardingStop && (
                        <span className="rounded-full bg-pine/10 text-pine border border-pine/20 px-2 py-0.5 text-[11px] font-medium">
                          Your boarding point
                        </span>
                      )}
                    </div>
                  </div>

                  {/* Primary Arrival Time */}
                  <div className="text-right shrink-0">
                    <p className={cn("text-base font-bold tracking-tight", isDelayed && "text-red-600", isDeparted && "text-muted-foreground")}>
                      {isDeparted
                        ? actualDisplay !== "—" ? actualDisplay : scheduledDisplay
                        : isArrived
                        ? actualDisplay !== "—" ? actualDisplay : estimatedDisplay
                        : estimatedDisplay}
                    </p>
                    {stopDate && <p className="text-[11px] text-muted-foreground">{stopDate}</p>}
                  </div>
                </div>

                {/* Subtitle with Scheduled vs Estimated/Actual breakdown */}
                <div className="mt-2 flex flex-wrap items-center justify-between border-t border-border/50 pt-2 text-xs text-muted-foreground gap-2">
                  <div>
                    {isDeparted ? (
                      <span>
                        Scheduled <b className="font-medium text-foreground">{scheduledDisplay}</b> · Actual <b className="font-medium text-emerald-800">{actualDisplay}</b>
                      </span>
                    ) : isArrived ? (
                      <span>
                        Scheduled <b className="font-medium text-foreground">{scheduledDisplay}</b> · Actual <b className="font-medium text-amber-800">{actualDisplay}</b>
                      </span>
                    ) : (
                      <span>
                        Scheduled <b className="font-medium text-foreground">{scheduledDisplay}</b> · Estimated <b className={cn("font-medium", isDelayed ? "text-red-600 font-semibold" : "text-foreground")}>{estimatedDisplay}</b>
                      </span>
                    )}
                  </div>
                  <div className="text-[11px]">
                    {stop.distanceKm.toFixed(0)} km
                    {stop.etaMinutes != null && stop.etaMinutes > 0 && !isDeparted && !isArrived && ` · in ${formatEta(stop.etaMinutes)}`}
                  </div>
                </div>
              </div>
            </li>
          );
        })}
      </ol>

      <p className="flex items-center gap-1.5 text-xs text-muted-foreground pt-1">
        <span className="inline-block h-2 w-2 rounded-full bg-emerald-600" />
        ETA based on the latest available bus location and traffic estimate.
      </p>
    </div>
  );
}

function dateForStop(serviceDate: string, dayOffset: number) {
  const date = new Date(`${serviceDate}T12:00:00Z`);
  date.setUTCDate(date.getUTCDate() + dayOffset);
  return new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", timeZone: "UTC" }).format(date);
}
