export function formatClock(value: string | null | undefined) {
  if (!value) return "—";
  const [hRaw, mRaw] = value.split(":");
  const h = Number(hRaw);
  const m = Number(mRaw);
  if (Number.isNaN(h) || Number.isNaN(m)) return value;
  const suffix = h >= 12 ? "PM" : "AM";
  const hour = h % 12 || 12;
  return `${hour}:${String(m).padStart(2, "0")} ${suffix}`;
}

export function formatEta(mins: number | null | undefined) {
  if (mins == null) return "—";
  if (mins <= 0) return "Now";
  if (mins < 60) return `${mins} min`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  return m ? `${h}h ${m}m` : `${h}h`;
}

export function formatWhen(mins: number | null | undefined) {
  if (mins == null) return "";
  const abs = Math.abs(mins);
  const label = abs < 60 ? `${abs} min` : `${Math.floor(abs / 60)}h ${abs % 60}m`;
  return mins >= 0 ? `in ${label}` : `${label} ago`;
}

export function rupee(amount: number | null | undefined) {
  if (amount == null) return "—";
  return new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency: "INR",
    maximumFractionDigits: 0,
  }).format(amount);
}

export function ago(iso: string | null | undefined) {
  if (!iso) return "waiting for a fix";
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "waiting for a fix";
  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (seconds < 5) return "just now";
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  return `${minutes} min ago`;
}
