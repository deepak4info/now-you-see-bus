import { useEffect, useRef, useState } from "react";
import { AlertTriangle, Bell, BellRing, Check, Clock, Copy, Flag, Gauge, MessageCircle, Radio, Share2, ShieldCheck, Smartphone } from "lucide-react";
import { Link } from "react-router-dom";
import { CommunityUpdatesDialog } from "@/components/CommunityUpdatesDialog";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { ago, formatClock, formatEta } from "@/lib/format";
import { useI18n } from "@/lib/i18n";
import { requestPushPermission } from "@/lib/pwa";
import type { BusTrackingItem, CommunityUpdateItem, DelayAlertEvent, TrackSnapshot } from "@/lib/types";

type ShareTarget = {
  fleetNumber: string;
  routeId: number;
  routeCode: string;
  routeName: string;
  tripId?: number | null;
  status: string;
  currentLocation?: string | null;
  nextStop?: string | null;
  etaMinutes?: number | null;
  estimatedArrivalTime?: string | null;
  delayMinutes?: number | null;
  speedKmph?: number | null;
  recordedAt?: string | null;
  fromStop?: string | null;
  toStop?: string | null;
  serviceDate?: string;
};

export function buildLiveSharePayload(target: ShareTarget) {
  const qs = new URLSearchParams();
  if (target.routeId) qs.set("routeId", String(target.routeId));
  if (target.tripId) qs.set("tripId", String(target.tripId));
  if (target.fromStop) qs.set("from", target.fromStop);
  if (target.toStop) qs.set("to", target.toStop);
  if (target.serviceDate) qs.set("date", target.serviceDate);

  const origin = typeof window !== "undefined" ? window.location.origin : "";
  const url = `${origin}/track/${encodeURIComponent(target.fleetNumber)}${qs.toString() ? `?${qs.toString()}` : ""}`;
  const title = `Track Bus ${target.fleetNumber} live · ${target.routeCode}`;

  const parts: string[] = [
    `Live Bus Journey · Bus ${target.fleetNumber} (Route ${target.routeCode}: ${target.routeName})`,
  ];

  if (target.currentLocation) {
    parts.push(`Current location: ${target.currentLocation}`);
  }
  if (target.nextStop) {
    parts.push(`Next stop: ${target.nextStop}${target.etaMinutes != null ? ` (ETA ${formatEta(target.etaMinutes)})` : ""}`);
  }
  if (target.estimatedArrivalTime) {
    parts.push(`Est. arrival: ${formatClock(target.estimatedArrivalTime)}`);
  }
  if (target.delayMinutes != null && target.delayMinutes >= 5) {
    parts.push(`Delay: +${target.delayMinutes} min behind schedule`);
  }
  if (target.speedKmph != null) {
    parts.push(`Speed: ${Math.round(target.speedKmph)} km/h`);
  }
  if (target.recordedAt) {
    parts.push(`Last GPS update: ${formatClock(target.recordedAt)} (${ago(target.recordedAt)})`);
  }
  parts.push(`Track live on map: ${url}`);

  return { title, url, text: parts.join("\n") };
}

export function trackingItemToShareTarget(item: BusTrackingItem): ShareTarget {
  return {
    fleetNumber: item.fleetNumber,
    routeId: item.routeId,
    routeCode: item.routeCode,
    routeName: item.routeName,
    tripId: item.tripId,
    status: item.status,
    currentLocation: item.currentLocation,
    nextStop: item.nextStop,
    etaMinutes: item.etaMinutes,
    estimatedArrivalTime: item.estimatedArrivalTime,
    delayMinutes: item.delayMinutes,
    speedKmph: item.speedKmph,
    recordedAt: item.recordedAt,
    fromStop: item.originStop,
    toStop: item.destinationStop,
    serviceDate: item.serviceDate,
  };
}

export async function shareLiveJourney(target: ShareTarget): Promise<{ ok: boolean; message: string }> {
  const { title, url, text } = buildLiveSharePayload(target);
  try {
    if (typeof navigator !== "undefined" && typeof navigator.share === "function") {
      await navigator.share({ title, text, url });
      return { ok: true, message: "Live journey shared with family." };
    }
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      await navigator.clipboard.writeText(`${text}`);
      return { ok: true, message: "Live tracking link & summary copied — paste in WhatsApp or SMS to share." };
    }
    return { ok: false, message: "Copy the page address from your browser to share with family." };
  } catch (err) {
    if (err instanceof DOMException && err.name === "AbortError") {
      return { ok: false, message: "" };
    }
    try {
      if (typeof navigator !== "undefined" && navigator.clipboard) {
        await navigator.clipboard.writeText(url);
        return { ok: true, message: "Live tracking link copied." };
      }
    } catch {}
    return { ok: false, message: "Could not share automatically. Copy the link manually." };
  }
}

export function LiveJourneyPanel({
  snapshot,
  from,
  to,
  date,
  delayAlert,
}: {
  snapshot: TrackSnapshot;
  from?: string;
  to?: string;
  date?: string;
  delayAlert?: DelayAlertEvent | null;
}) {
  const { user } = useAuth();
  const { t } = useI18n();
  const [shareFeedback, setShareFeedback] = useState("");
  const [copiedLink, setCopiedLink] = useState(false);
  const [delayWatchEnabled, setDelayWatchEnabled] = useState(false);
  const [alertFeedback, setAlertFeedback] = useState("");
  const lastBrowserAlertDelayRef = useRef<number | null>(null);

  // Stop arrival reminder state
  const stops = snapshot?.stops ?? [];
  const upcomingStops = stops.filter((s) => s.status !== "Passed");
  const [reminderStopId, setReminderStopId] = useState<number>(() => upcomingStops[0]?.stopId ?? stops[0]?.stopId ?? 0);
  const [reminderMinutes, setReminderMinutes] = useState(10);
  const [reminderArmed, setReminderArmed] = useState(false);
  const [reminderFeedback, setReminderFeedback] = useState("");
  const [savingReminder, setSavingReminder] = useState(false);
  const firedStopReminderRef = useRef<number | null>(null);

  // Quick report & verified community updates state
  const [communityOpen, setCommunityOpen] = useState(false);
  const [dialogTab, setDialogTab] = useState<"verified" | "delay">("verified");

  const communityQuery = useQuery<CommunityUpdateItem[]>(
    snapshot?.fleetNumber ? `/api/community-updates?fleetNumber=${encodeURIComponent(snapshot.fleetNumber)}` : null,
  );

  const lastStop = stops.length > 0 ? stops[stops.length - 1] : undefined;
  const shareTarget: ShareTarget = {
    fleetNumber: snapshot.fleetNumber,
    routeId: snapshot.routeId,
    routeCode: snapshot.routeCode,
    routeName: snapshot.routeName,
    tripId: snapshot.tripId,
    status: snapshot.status,
    currentLocation: snapshot.currentStop,
    nextStop: snapshot.nextStop,
    etaMinutes: snapshot.etaToYouMinutes ?? snapshot.etaMinutes,
    estimatedArrivalTime: snapshot.estimatedArrivalTime ?? lastStop?.estimatedArrival ?? null,
    delayMinutes: snapshot.delayMinutes,
    speedKmph: snapshot.speedKmph,
    recordedAt: snapshot.recordedAt,
    fromStop: from || snapshot.fromStop || stops[0]?.name,
    toStop: to || snapshot.toStop || lastStop?.name,
    serviceDate: date || snapshot.serviceDate,
  };

  const payload = buildLiveSharePayload(shareTarget);
  const whatsappHref = `https://wa.me/?text=${encodeURIComponent(payload.text)}`;
  const smsHref = `sms:?body=${encodeURIComponent(payload.text)}`;

  const effectiveDelay = delayAlert?.delayMinutes ?? snapshot.delayMinutes;
  const isDelayed = effectiveDelay != null && effectiveDelay >= 5 && snapshot.status !== "Completed" && snapshot.status !== "Cancelled";

  useEffect(() => {
    if (!delayWatchEnabled || !isDelayed || effectiveDelay == null) return;
    const prev = lastBrowserAlertDelayRef.current;
    if (prev != null && effectiveDelay - prev < 5) return;

    lastBrowserAlertDelayRef.current = effectiveDelay;
    const msg = delayAlert?.message
      || `Bus ${snapshot.fleetNumber} is running ${effectiveDelay} min behind schedule.${snapshot.nextStop ? ` Next: ${snapshot.nextStop}.` : ""}`;

    if (typeof window !== "undefined" && "Notification" in window && Notification.permission === "granted") {
      try {
        new Notification(`Delay Alert · Bus ${snapshot.fleetNumber}`, {
          body: msg,
          icon: "/icons/icon-192.png",
        });
      } catch {}
    }
    setAlertFeedback(msg);
  }, [delayWatchEnabled, isDelayed, effectiveDelay, delayAlert, snapshot.fleetNumber, snapshot.nextStop]);

  async function handleEnableDelayAlerts() {
    if (delayWatchEnabled) {
      setDelayWatchEnabled(false);
      setAlertFeedback("Live delay notifications paused for this session.");
      return;
    }

    const perm = await requestPushPermission();
    setDelayWatchEnabled(true);
    if (perm === "granted") {
      setAlertFeedback("Delay notifications active — you will be alerted immediately if this bus falls behind schedule.");
    } else {
      setAlertFeedback("In-page delay alerts enabled for this trip.");
    }
  }

  async function handleShareNative() {
    const res = await shareLiveJourney(shareTarget);
    if (res.message) setShareFeedback(res.message);
  }

  async function handleCopyLink() {
    try {
      await navigator.clipboard.writeText(payload.url);
      setCopiedLink(true);
      setShareFeedback("Live journey link copied — send it to family members.");
      window.setTimeout(() => setCopiedLink(false), 2500);
    } catch {
      setShareFeedback("Could not copy automatically. Copy the browser address bar.");
    }
  }

  const speedLabel = (() => {
    if (snapshot.speedKmph == null) {
      return snapshot.status === "Running" ? "Waiting for GPS" : "Not moving";
    }
    const rounded = Math.round(snapshot.speedKmph);
    if (rounded <= 1) return "0 km/h · Halted";
    return `${rounded} km/h`;
  })();

  const gpsExactClock = snapshot.recordedAt ? formatClock(snapshot.recordedAt) : null;

  return (
    <div className="space-y-4">
      {/* Live Speed & Last GPS Update telemetry strip */}
      <div className="grid grid-cols-2 gap-3 rounded-xl border bg-card p-3.5 shadow-xs">
        <div className="min-w-0">
          <p className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
            <Gauge className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />
            Bus speed
          </p>
          <p className="mt-1 truncate text-base font-bold text-ink">{speedLabel}</p>
          <p className="text-[11px] text-muted-foreground">
            {snapshot.status === "Running"
              ? snapshot.simulation
                ? "Simulated road speed"
                : "Live telemetry speed"
              : `Status: ${snapshot.status}`}
          </p>
        </div>

        <div className="min-w-0 border-l pl-3">
          <p className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
            <Radio className="h-3.5 w-3.5 text-emerald-600 shrink-0" aria-hidden="true" />
            Last GPS update
          </p>
          <p className="mt-1 truncate text-base font-bold text-ink">
            {snapshot.recordedAt ? ago(snapshot.recordedAt) : "No GPS fix"}
          </p>
          <p className="text-[11px] text-muted-foreground">
            {gpsExactClock ? `${gpsExactClock} IST` : "Awaiting signal"}
          </p>
        </div>
      </div>

      {/* Delay Notification Alert Card */}
      <div
        className={`rounded-xl border p-3.5 ${
          isDelayed
            ? "border-red-500/30 bg-red-500/5"
            : "border-border bg-card"
        }`}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
              {isDelayed ? (
                <AlertTriangle className="h-3.5 w-3.5 text-red-600 shrink-0" aria-hidden="true" />
              ) : (
                <Bell className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />
              )}
              Delay notifications
            </p>
            {isDelayed ? (
              <p className="mt-1 text-sm font-semibold text-red-700 dark:text-red-300">
                Delayed by {effectiveDelay} min
                {shareTarget.estimatedArrivalTime ? ` · Updated arrival ${formatClock(shareTarget.estimatedArrivalTime)}` : ""}
              </p>
            ) : (
              <p className="mt-1 text-xs text-muted-foreground">
                Get notified immediately if Bus {snapshot.fleetNumber} runs 5+ min late.
              </p>
            )}
          </div>
          <Button
            type="button"
            size="sm"
            variant={delayWatchEnabled ? "default" : "outline"}
            onClick={() => void handleEnableDelayAlerts()}
            className="shrink-0"
          >
            {delayWatchEnabled ? (
              <>
                <BellRing className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" /> Alerts On
              </>
            ) : (
              <>
                <Bell className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" /> Notify Me
              </>
            )}
          </Button>
        </div>
        {alertFeedback && (
          <p className="mt-2 text-xs text-muted-foreground" role="status" aria-live="polite">
            {alertFeedback}
          </p>
        )}
      </div>

      {/* Share Live Journey with Family */}
      <div className="rounded-xl border bg-card p-3.5 shadow-xs space-y-2.5">
        <div>
          <p className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            <Share2 className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />
            Share live journey with family
          </p>
          <p className="mt-1 text-xs text-muted-foreground">
            Send real-time location, ETA, speed, and delay status for Bus {snapshot.fleetNumber}.
          </p>
        </div>

        <div className="grid grid-cols-2 gap-2">
          <Button type="button" size="sm" onClick={() => void handleShareNative()}>
            <Share2 className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" /> Share live
          </Button>
          <Button type="button" size="sm" variant="outline" onClick={() => void handleCopyLink()}>
            {copiedLink ? (
              <>
                <Check className="mr-1.5 h-3.5 w-3.5 text-emerald-600" aria-hidden="true" /> Copied
              </>
            ) : (
              <>
                <Copy className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" /> Copy link
              </>
            )}
          </Button>
          <Button type="button" size="sm" variant="outline" asChild>
            <a href={whatsappHref} target="_blank" rel="noopener noreferrer">
              <MessageCircle className="mr-1.5 h-3.5 w-3.5 text-emerald-600" aria-hidden="true" /> WhatsApp
            </a>
          </Button>
          <Button type="button" size="sm" variant="outline" asChild>
            <a href={smsHref}>
              <Smartphone className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" /> SMS
            </a>
          </Button>
        </div>

        {shareFeedback && (
          <p className="text-xs font-medium text-primary" role="status" aria-live="polite">
            {shareFeedback}
          </p>
        )}
      </div>

      {/* Stop Arrival Reminder */}
      <div className="rounded-xl border bg-card p-3.5 shadow-xs space-y-3">
        <div className="flex items-center justify-between">
          <p className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            <Bell className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />
            {t("stopArrivalReminder")}
          </p>
          {reminderArmed && (
            <span className="inline-flex items-center gap-1 rounded-full bg-emerald-500/10 px-2 py-0.5 text-[10px] font-semibold text-emerald-700 dark:text-emerald-400">
              <Check className="h-3 w-3" /> Armed
            </span>
          )}
        </div>

        {upcomingStops.length > 0 ? (
          <div className="space-y-2.5">
            <div className="grid grid-cols-2 gap-2">
              <div>
                <label className="text-[11px] font-medium text-muted-foreground">Select Stop</label>
                <select
                  value={reminderStopId}
                  onChange={(e) => setReminderStopId(Number(e.target.value))}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-2 py-1.5 text-xs text-ink focus:outline-pine"
                >
                  {upcomingStops.map((s) => (
                    <option key={s.stopId} value={s.stopId}>
                      {s.name} {s.etaMinutes != null ? `(~${s.etaMinutes}m)` : ""}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="text-[11px] font-medium text-muted-foreground">Remind me</label>
                <select
                  value={reminderMinutes}
                  onChange={(e) => setReminderMinutes(Number(e.target.value))}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-2 py-1.5 text-xs text-ink focus:outline-pine"
                >
                  <option value={5}>5 min before</option>
                  <option value={10}>10 min before</option>
                  <option value={15}>15 min before</option>
                  <option value={20}>20 min before</option>
                  <option value={30}>30 min before</option>
                </select>
              </div>
            </div>

            <Button
              type="button"
              size="sm"
              variant={reminderArmed ? "outline" : "default"}
              disabled={savingReminder}
              onClick={async () => {
                if (reminderArmed) {
                  setReminderArmed(false);
                  setReminderFeedback("Stop reminder disarmed.");
                  return;
                }
                const perm = await requestPushPermission();
                setSavingReminder(true);
                try {
                  if (user) {
                    await api("/api/reminders", {
                      method: "POST",
                      body: JSON.stringify({
                        routeId: snapshot.routeId,
                        stopId: reminderStopId,
                        tripId: snapshot.tripId,
                        fleetNumber: snapshot.fleetNumber,
                        remindBeforeMinutes: reminderMinutes,
                      }),
                    });
                  }
                  setReminderArmed(true);
                  const selectedName = upcomingStops.find((s) => s.stopId === reminderStopId)?.name ?? "selected stop";
                  setReminderFeedback(
                    `Reminder set: We will alert you ${reminderMinutes} min before arriving at ${selectedName}.`
                  );
                } catch {
                  setReminderArmed(true);
                  setReminderFeedback(`In-session reminder set: ${reminderMinutes} min before your stop.`);
                } finally {
                  setSavingReminder(false);
                }
              }}
              className="w-full"
            >
              <Bell className="mr-1.5 h-3.5 w-3.5" />
              {reminderArmed ? "Cancel Reminder" : "Set Stop Arrival Reminder"}
            </Button>
          </div>
        ) : (
          <p className="text-xs text-muted-foreground">Trip completed or no upcoming stops.</p>
        )}

        {reminderFeedback && (
          <p className="text-xs text-muted-foreground" role="status" aria-live="polite">
            {reminderFeedback}
          </p>
        )}
      </div>

      {/* Community Updates & Issue Reporting */}
      <div className="rounded-xl border bg-card p-3.5 shadow-xs space-y-2.5">
        <div className="flex items-center justify-between">
          <div>
            <p className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
              <Flag className="h-3.5 w-3.5 text-primary shrink-0" aria-hidden="true" />
              {t("communityTitle")}
            </p>
            <p className="mt-0.5 text-xs text-muted-foreground">
              Delays, cancelled services & timing updates verified by admin.
            </p>
          </div>
        </div>

        {communityQuery.data && communityQuery.data.length > 0 && (
          <div className="rounded-lg border border-border/70 bg-muted/30 p-2.5 text-xs space-y-1">
            <p className="font-semibold text-ink flex items-center gap-1.5">
              <ShieldCheck className="h-3.5 w-3.5 text-emerald-600" />
              {communityQuery.data.length} verified update{communityQuery.data.length > 1 ? "s" : ""} for Bus {snapshot.fleetNumber}
            </p>
            <p className="text-muted-foreground line-clamp-2">
              {communityQuery.data[0].category}: {communityQuery.data[0].description}
            </p>
          </div>
        )}

        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => {
              setDialogTab("verified");
              setCommunityOpen(true);
            }}
            className="flex-1"
          >
            <ShieldCheck className="mr-1.5 h-3.5 w-3.5 text-emerald-600" /> Community Updates
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => {
              setDialogTab("delay");
              setCommunityOpen(true);
            }}
            className="flex-1"
          >
            <Flag className="mr-1.5 h-3.5 w-3.5 text-amber-600" /> Report Issue
          </Button>
        </div>
      </div>

      <CommunityUpdatesDialog
        isOpen={communityOpen}
        onClose={() => setCommunityOpen(false)}
        initialFleet={snapshot.fleetNumber}
        initialRoute={snapshot.routeCode}
        initialStop={snapshot.currentStop ?? snapshot.nextStop ?? ""}
        initialTab={dialogTab}
      />
    </div>
  );
}
