import { useEffect, useState } from "react";
import { HubConnectionBuilder } from "@microsoft/signalr";
import type { TrackSnapshot } from "@/lib/types";

function mergeLeg(previous: TrackSnapshot | null, next: TrackSnapshot): TrackSnapshot {
  if (!previous?.fromStop && !previous?.toStop) return next;
  const from = next.stops.find((stop) => stop.name === previous.fromStop);
  const to = next.stops.find((stop) => stop.name === previous.toStop);
  return {
    ...next,
    fromStop: previous.fromStop,
    toStop: previous.toStop,
    fare: previous.fare ?? next.fare,
    etaToYouMinutes: from && (from.status === "Pending" || from.status === "Next") ? from.etaMinutes : from ? 0 : next.etaToYouMinutes,
    boardingPassed: from?.status === "Passed",
    etaToDestinationMinutes: to?.etaMinutes ?? next.etaToDestinationMinutes,
  };
}

export function useBusLive(
  fleet: string | null,
  seed: TrackSnapshot | null,
  selectedRouteId?: number,
  subscribeToLive = true,
) {
  const [snap, setSnap] = useState<TrackSnapshot | null>(seed);

  useEffect(() => {
    setSnap(seed);
  }, [seed]);

  useEffect(() => {
    if (!fleet || !subscribeToLive) return;
    let active = true;
    const conn = new HubConnectionBuilder().withUrl("/hubs/tracking").withAutomaticReconnect().build();
    conn.on("location", (payload: TrackSnapshot) => {
      if (
        payload.fleetNumber === fleet
        && (selectedRouteId == null || payload.routeId === selectedRouteId)
      ) {
        setSnap((current) => mergeLeg(current, payload));
      }
    });
    conn.onreconnected(() => {
      if (active) void conn.invoke("JoinBus", fleet);
    });
    conn.start()
      .then(() => {
        if (!active) return conn.stop();
        return conn.invoke("JoinBus", fleet);
      })
      .catch(() => undefined);
    return () => {
      active = false;
      if (conn.state === "Connected") void conn.stop();
    };
  }, [fleet, selectedRouteId, subscribeToLive]);

  return snap;
}
