import { AlertTriangle, Bell, CheckCircle2, Clock, Info } from "lucide-react";
import { Button } from "@/components/ui/button";
import { api, useQuery } from "@/lib/api";
import { formatClock } from "@/lib/format";
import { useI18n } from "@/lib/i18n";
import type { Notice } from "@/lib/types";

export function NotificationsPage() {
  const { t } = useI18n();
  const query = useQuery<{ unread: number; items: Notice[] }>("/api/notifications");

  async function readAll() {
    await api("/api/notifications/read-all", { method: "POST" });
    await query.reload();
  }

  return (
    <div className="mx-auto max-w-3xl px-4 py-8">
      <div className="flex items-end justify-between gap-3 border-b pb-5">
        <div>
          <h1 className="font-display text-3xl sm:text-4xl text-ink">{t("alertsTitle")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("alertsSubtitle")}</p>
        </div>
        {!!query.data?.unread && (
          <Button variant="outline" size="sm" onClick={() => void readAll()}>
            {t("markAllRead")}
          </Button>
        )}
      </div>

      {query.loading && <p className="mt-6 text-xs text-muted-foreground">Looking for alerts…</p>}
      {query.error && <p className="mt-6 text-xs text-destructive">{query.error}</p>}
      {query.data && query.data.items.length === 0 && (
        <div className="mt-6 rounded-2xl border border-dashed bg-card p-8 text-center">
          <Bell className="mx-auto h-8 w-8 text-muted-foreground/50" />
          <p className="mt-2 text-sm font-semibold text-ink">No alerts right now</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Delay notifications, stop arrival reminders, and trip updates for your saved routes will appear here.
          </p>
        </div>
      )}

      <div className="mt-6 grid gap-3">
        {query.data?.items.map((item) => (
          <article
            key={item.id}
            className={`rounded-xl border bg-card p-4 transition-colors ${
              item.isRead ? "opacity-75" : "border-pine/30 bg-card shadow-xs"
            }`}
          >
            <div className="flex items-center justify-between gap-2 border-b border-border/50 pb-2">
              <span
                className={`inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${
                  item.kind === "delayed"
                    ? "bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-300"
                    : item.kind === "reminder"
                    ? "bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300"
                    : item.kind === "started"
                    ? "bg-emerald-100 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300"
                    : "bg-slate-100 text-slate-800 dark:bg-slate-900 dark:text-slate-300"
                }`}
              >
                {item.kind === "delayed" ? (
                  <AlertTriangle className="h-3 w-3" />
                ) : item.kind === "reminder" ? (
                  <Clock className="h-3 w-3" />
                ) : item.kind === "started" ? (
                  <CheckCircle2 className="h-3 w-3" />
                ) : (
                  <Info className="h-3 w-3" />
                )}
                {item.kind}
              </span>
              <span className="text-[11px] text-muted-foreground">{formatClock(item.createdAt)}</span>
            </div>
            <h2 className="mt-2 font-semibold text-sm text-ink">{item.title}</h2>
            <p className="mt-1 text-xs text-muted-foreground leading-relaxed">{item.body}</p>
          </article>
        ))}
      </div>
    </div>
  );
}
