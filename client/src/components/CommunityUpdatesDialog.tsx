import { useState } from "react";
import { AlertCircle, AlertTriangle, CalendarX, Check, Clock, GitPullRequest, Info, MessageSquare, Plus, RefreshCw, Send, ShieldCheck, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { formatClock } from "@/lib/format";
import type { CommunityUpdateItem } from "@/lib/types";

type DialogTab = "verified" | "delay" | "cancelled" | "missing_route" | "timing";

export function CommunityUpdatesDialog({
  isOpen,
  onClose,
  initialFleet = "",
  initialRoute = "",
  initialStop = "",
}: {
  isOpen: boolean;
  onClose: () => void;
  initialFleet?: string;
  initialRoute?: string;
  initialStop?: string;
}) {
  const [tab, setTab] = useState<DialogTab>("verified");
  const [filterCategory, setFilterCategory] = useState("all");

  // Query verified community updates
  const updatesQuery = useQuery<CommunityUpdateItem[]>(
    isOpen ? `/api/community-updates${filterCategory !== "all" ? `?category=${filterCategory}` : ""}` : null
  );

  // Form states
  const [fleetNumber, setFleetNumber] = useState(initialFleet);
  const [stopName, setStopName] = useState(initialStop);
  const [delayMinutes, setDelayMinutes] = useState(15);
  const [proposedTime, setProposedTime] = useState("");
  const [description, setDescription] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [feedback, setFeedback] = useState<{ ok: boolean; message: string } | null>(null);

  if (!isOpen) return null;

  async function handleSubmit(category: string) {
    if (!description.trim() || description.trim().length < 5) {
      setFeedback({ ok: false, message: "Please describe the update (at least 5 characters)." });
      return;
    }

    setSubmitting(true);
    setFeedback(null);
    try {
      const res = await api<{ id: number; message: string }>("/api/reports", {
        method: "POST",
        body: JSON.stringify({
          fleetNumber: fleetNumber.trim() || (category === "MissingRoute" ? "New Route" : "General"),
          category,
          reportedStopName: stopName.trim() || null,
          proposedTime: proposedTime.trim() || null,
          delayMinutes: category === "DelayReport" ? Number(delayMinutes) : null,
          description: description.trim(),
        }),
      });

      setFeedback({
        ok: true,
        message: res.message || "Thank you! Your report has been submitted for admin verification before publishing.",
      });
      setDescription("");
      setProposedTime("");
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to submit update. Please try again.";
      setFeedback({ ok: false, message: msg });
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-3 sm:p-4 backdrop-blur-xs" role="dialog" aria-modal="true" aria-label="Community Bus Updates">
      <div className="relative flex max-h-[90vh] w-full max-w-2xl flex-col rounded-2xl border border-border bg-card shadow-2xl overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between border-b px-5 py-4">
          <div>
            <div className="flex items-center gap-2">
              <span className="flex h-7 w-7 items-center justify-center rounded-lg bg-pine/10 text-pine">
                <MessageSquare className="h-4 w-4" aria-hidden="true" />
              </span>
              <h2 className="font-display text-lg font-bold text-ink">Community Bus Updates</h2>
            </div>
            <p className="text-xs text-muted-foreground mt-0.5">
              Verified passenger reports, delay alerts, cancellations & route suggestions.
            </p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close dialog">
            <X className="h-4 w-4" />
          </Button>
        </div>

        {/* Tab Navigation */}
        <div className="flex overflow-x-auto border-b bg-muted/30 px-3 py-1.5 gap-1.5 text-xs">
          <button
            type="button"
            onClick={() => { setTab("verified"); setFeedback(null); }}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 font-medium whitespace-nowrap transition-colors ${
              tab === "verified" ? "bg-card text-ink shadow-xs border" : "text-muted-foreground hover:text-ink"
            }`}
          >
            <ShieldCheck className="h-3.5 w-3.5 text-emerald-600" /> Verified Board
          </button>
          <button
            type="button"
            onClick={() => { setTab("delay"); setFeedback(null); }}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 font-medium whitespace-nowrap transition-colors ${
              tab === "delay" ? "bg-card text-ink shadow-xs border" : "text-muted-foreground hover:text-ink"
            }`}
          >
            <AlertTriangle className="h-3.5 w-3.5 text-amber-600" /> Report Delay
          </button>
          <button
            type="button"
            onClick={() => { setTab("cancelled"); setFeedback(null); }}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 font-medium whitespace-nowrap transition-colors ${
              tab === "cancelled" ? "bg-card text-ink shadow-xs border" : "text-muted-foreground hover:text-ink"
            }`}
          >
            <CalendarX className="h-3.5 w-3.5 text-red-600" /> Report Cancelled
          </button>
          <button
            type="button"
            onClick={() => { setTab("timing"); setFeedback(null); }}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 font-medium whitespace-nowrap transition-colors ${
              tab === "timing" ? "bg-card text-ink shadow-xs border" : "text-muted-foreground hover:text-ink"
            }`}
          >
            <Clock className="h-3.5 w-3.5 text-blue-600" /> Submit Timing
          </button>
          <button
            type="button"
            onClick={() => { setTab("missing_route"); setFeedback(null); }}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 font-medium whitespace-nowrap transition-colors ${
              tab === "missing_route" ? "bg-card text-ink shadow-xs border" : "text-muted-foreground hover:text-ink"
            }`}
          >
            <GitPullRequest className="h-3.5 w-3.5 text-purple-600" /> Suggest Route
          </button>
        </div>

        {/* Content Body */}
        <div className="flex-1 overflow-y-auto p-4 sm:p-5">
          {feedback && (
            <div
              className={`mb-4 flex items-start gap-2.5 rounded-xl border p-3 text-xs ${
                feedback.ok
                  ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-800 dark:text-emerald-300"
                  : "border-red-500/30 bg-red-500/10 text-red-800 dark:text-red-300"
              }`}
            >
              {feedback.ok ? <Check className="h-4 w-4 shrink-0 text-emerald-600" /> : <AlertCircle className="h-4 w-4 shrink-0 text-red-600" />}
              <div>
                <p className="font-semibold">{feedback.ok ? "Submission Received" : "Notice"}</p>
                <p className="mt-0.5">{feedback.message}</p>
              </div>
            </div>
          )}

          {tab === "verified" && (
            <div className="space-y-3">
              {/* Category Filter Pills */}
              <div className="flex flex-wrap items-center gap-1.5 text-xs">
                <span className="text-muted-foreground mr-1">Filter:</span>
                {[
                  ["all", "All Updates"],
                  ["DelayReport", "Delays"],
                  ["CancelledService", "Cancellations"],
                  ["UpdatedTiming", "Timings"],
                  ["MissingRoute", "Route Suggestions"],
                ].map(([cat, label]) => (
                  <button
                    key={cat}
                    type="button"
                    onClick={() => setFilterCategory(cat)}
                    className={`rounded-full px-2.5 py-1 text-xs font-medium transition-colors ${
                      filterCategory === cat
                        ? "bg-pine text-white"
                        : "border border-border/80 bg-card text-muted-foreground hover:bg-muted"
                    }`}
                  >
                    {label}
                  </button>
                ))}
              </div>

              {updatesQuery.loading && (
                <div className="flex items-center justify-center p-8 text-xs text-muted-foreground">
                  <RefreshCw className="mr-2 h-4 w-4 animate-spin text-pine" /> Loading verified community updates…
                </div>
              )}

              {updatesQuery.error && (
                <p className="text-xs text-destructive p-4">Error loading updates: {updatesQuery.error}</p>
              )}

              {!updatesQuery.loading && (!updatesQuery.data || updatesQuery.data.length === 0) && (
                <div className="rounded-xl border border-dashed border-border/80 p-8 text-center">
                  <Info className="mx-auto h-8 w-8 text-muted-foreground/60" />
                  <p className="mt-2 text-sm font-semibold text-ink">No verified community updates in this category</p>
                  <p className="mt-1 text-xs text-muted-foreground">
                    All passenger reports are verified by transport administrators before appearing here.
                  </p>
                </div>
              )}

              {updatesQuery.data && updatesQuery.data.length > 0 && (
                <div className="space-y-2.5">
                  {updatesQuery.data.map((item) => (
                    <article key={item.id} className="rounded-xl border border-border/80 bg-card p-3.5 shadow-2xs">
                      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border/50 pb-2">
                        <div className="flex items-center gap-2">
                          <span
                            className={`rounded-md px-2 py-0.5 text-[11px] font-bold uppercase tracking-wider ${
                              item.category.includes("Delay")
                                ? "bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300"
                                : item.category.includes("Cancel")
                                ? "bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-300"
                                : item.category.includes("Timing")
                                ? "bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300"
                                : "bg-purple-100 text-purple-800 dark:bg-purple-950 dark:text-purple-300"
                            }`}
                          >
                            {item.category === "DelayReport" ? "Verified Delay" : item.category === "CancelledService" ? "Cancelled Service" : item.category === "UpdatedTiming" ? "Timetable Update" : "Route Suggestion"}
                          </span>
                          <span className="text-xs font-semibold text-ink">
                            Bus {item.fleetNumber} {item.routeCode ? `· Route ${item.routeCode}` : ""}
                          </span>
                        </div>
                        <div className="flex items-center gap-1.5 text-[11px] text-emerald-700 dark:text-emerald-400 font-semibold">
                          <ShieldCheck className="h-3.5 w-3.5" /> Verified by Admin
                        </div>
                      </div>

                      <p className="mt-2 text-xs text-ink leading-relaxed">{item.description}</p>

                      <div className="mt-2.5 flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground border-t border-border/40 pt-2">
                        <span>
                          {item.reportedStopName ? `Stop/Location: ${item.reportedStopName} · ` : ""}
                          {item.delayMinutes ? `Delay: +${item.delayMinutes} min · ` : ""}
                          {item.proposedTime ? `Timing: ${item.proposedTime} · ` : ""}
                          Posted {formatClock(item.createdAt)}
                        </span>
                        {item.adminNotes && (
                          <span className="italic text-ink">Note: {item.adminNotes}</span>
                        )}
                      </div>
                    </article>
                  ))}
                </div>
              )}
            </div>
          )}

          {tab === "delay" && (
            <form onSubmit={(e) => { e.preventDefault(); void handleSubmit("DelayReport"); }} className="space-y-3.5">
              <div className="rounded-xl border border-amber-500/20 bg-amber-500/5 p-3 text-xs text-amber-900 dark:text-amber-200">
                <p className="font-semibold">Report Bus Delay</p>
                <p className="mt-0.5">Report delays experienced on the road. Transport controllers will review and publish live alerts.</p>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="text-xs font-medium text-ink">Bus / Fleet Number</label>
                  <input
                    type="text"
                    required
                    placeholder="e.g. 102, HR-62-0101"
                    value={fleetNumber}
                    onChange={(e) => setFleetNumber(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
                <div>
                  <label className="text-xs font-medium text-ink">Delay (Minutes)</label>
                  <input
                    type="number"
                    min="5"
                    max="300"
                    value={delayMinutes}
                    onChange={(e) => setDelayMinutes(Number(e.target.value))}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Current Location / Stop</label>
                <input
                  type="text"
                  placeholder="e.g. Near Barwala bypass, Hisar Bus Stand"
                  value={stopName}
                  onChange={(e) => setStopName(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                />
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Reason / Details</label>
                <textarea
                  rows={3}
                  required
                  placeholder="e.g. Delayed due to heavy fog / highway construction / tire repair."
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card p-3 text-xs text-ink focus:outline-pine"
                />
              </div>

              <Button type="submit" disabled={submitting} className="w-full">
                <Send className="mr-2 h-3.5 w-3.5" /> {submitting ? "Submitting for verification…" : "Submit Delay Report"}
              </Button>
            </form>
          )}

          {tab === "cancelled" && (
            <form onSubmit={(e) => { e.preventDefault(); void handleSubmit("CancelledService"); }} className="space-y-3.5">
              <div className="rounded-xl border border-red-500/20 bg-red-500/5 p-3 text-xs text-red-900 dark:text-red-200">
                <p className="font-semibold">Report Cancelled Bus Service</p>
                <p className="mt-0.5">Let fellow passengers know if a scheduled bus was cancelled at the depot or en route.</p>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="text-xs font-medium text-ink">Bus / Fleet Number</label>
                  <input
                    type="text"
                    required
                    placeholder="e.g. 105, 118"
                    value={fleetNumber}
                    onChange={(e) => setFleetNumber(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
                <div>
                  <label className="text-xs font-medium text-ink">Scheduled Departure Time</label>
                  <input
                    type="text"
                    placeholder="e.g. 09:00 AM, 16:30"
                    value={proposedTime}
                    onChange={(e) => setProposedTime(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Route / Origin & Destination</label>
                <input
                  type="text"
                  placeholder="e.g. Hisar to Delhi ISBT service"
                  value={stopName}
                  onChange={(e) => setStopName(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                />
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Cancellation Details</label>
                <textarea
                  rows={3}
                  required
                  placeholder="e.g. Enquiry booth announced service cancelled today due to mechanical issue."
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card p-3 text-xs text-ink focus:outline-pine"
                />
              </div>

              <Button type="submit" disabled={submitting} className="w-full">
                <Send className="mr-2 h-3.5 w-3.5" /> {submitting ? "Submitting for verification…" : "Submit Cancellation Report"}
              </Button>
            </form>
          )}

          {tab === "timing" && (
            <form onSubmit={(e) => { e.preventDefault(); void handleSubmit("UpdatedTiming"); }} className="space-y-3.5">
              <div className="rounded-xl border border-blue-500/20 bg-blue-500/5 p-3 text-xs text-blue-900 dark:text-blue-200">
                <p className="font-semibold">Submit Updated Bus Timings</p>
                <p className="mt-0.5">Noticed a revised timetable at the bus stand? Help update official schedules.</p>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="text-xs font-medium text-ink">Route / Bus Number</label>
                  <input
                    type="text"
                    required
                    placeholder="e.g. Route 101, Bus 102"
                    value={fleetNumber}
                    onChange={(e) => setFleetNumber(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
                <div>
                  <label className="text-xs font-medium text-ink">Observed / New Departure Time</label>
                  <input
                    type="text"
                    required
                    placeholder="e.g. 08:45 AM (previously 08:30 AM)"
                    value={proposedTime}
                    onChange={(e) => setProposedTime(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Bus Stand / Boarding Point</label>
                <input
                  type="text"
                  placeholder="e.g. Barwala Bus Stand"
                  value={stopName}
                  onChange={(e) => setStopName(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                />
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Source / Details</label>
                <textarea
                  rows={3}
                  required
                  placeholder="e.g. New printed timetable posted at depot inquiry window."
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card p-3 text-xs text-ink focus:outline-pine"
                />
              </div>

              <Button type="submit" disabled={submitting} className="w-full">
                <Send className="mr-2 h-3.5 w-3.5" /> {submitting ? "Submitting for verification…" : "Submit Updated Timings"}
              </Button>
            </form>
          )}

          {tab === "missing_route" && (
            <form onSubmit={(e) => { e.preventDefault(); void handleSubmit("MissingRoute"); }} className="space-y-3.5">
              <div className="rounded-xl border border-purple-500/20 bg-purple-500/5 p-3 text-xs text-purple-900 dark:text-purple-200">
                <p className="font-semibold">Suggest Missing Bus Route</p>
                <p className="mt-0.5">Request a missing corridor or new bus service. Submissions are reviewed by depot schedulers.</p>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="text-xs font-medium text-ink">Origin Stop / City</label>
                  <input
                    type="text"
                    required
                    placeholder="e.g. Bhuna"
                    value={fleetNumber}
                    onChange={(e) => setFleetNumber(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
                <div>
                  <label className="text-xs font-medium text-ink">Destination Stop / City</label>
                  <input
                    type="text"
                    required
                    placeholder="e.g. Chandigarh ISBT"
                    value={stopName}
                    onChange={(e) => setStopName(e.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                  />
                </div>
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Suggested Intermediate Stops & Frequency</label>
                <input
                  type="text"
                  placeholder="e.g. Via Narwana - Kaithal - Ambala (Morning 07:00 AM, Evening 04:00 PM)"
                  value={proposedTime}
                  onChange={(e) => setProposedTime(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card px-3 py-1.5 text-xs text-ink focus:outline-pine"
                />
              </div>

              <div>
                <label className="text-xs font-medium text-ink">Passenger Demand & Justification</label>
                <textarea
                  rows={3}
                  required
                  placeholder="e.g. Heavy student and office commuter demand. Currently commuters have to make 2 transfers."
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  className="mt-1 w-full rounded-lg border border-border bg-card p-3 text-xs text-ink focus:outline-pine"
                />
              </div>

              <Button type="submit" disabled={submitting} className="w-full">
                <Send className="mr-2 h-3.5 w-3.5" /> {submitting ? "Submitting for verification…" : "Submit Route Suggestion"}
              </Button>
            </form>
          )}
        </div>

        {/* Footer info note */}
        <div className="border-t bg-muted/30 px-5 py-2.5 text-[11px] text-muted-foreground flex items-center justify-between">
          <span>Transport Ops verifies all updates before publishing</span>
          <Button variant="ghost" size="sm" onClick={onClose} className="h-7 text-xs">
            Close
          </Button>
        </div>
      </div>
    </div>
  );
}
