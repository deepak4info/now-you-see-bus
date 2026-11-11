import { Badge } from "@/components/ui/badge";

export function StatusBadge({ status }: { status: string }) {
  if (status === "Running") return <Badge variant="live">Running</Badge>;
  if (status === "Completed") return <Badge variant="done">Trip completed</Badge>;
  if (status === "Cancelled") return <Badge variant="outline" className="border-red-500/30 bg-red-500/10 text-red-700">Cancelled</Badge>;
  if (status === "Scheduled") return <Badge variant="wait">Scheduled</Badge>;
  return <Badge variant="outline">{status}</Badge>;
}
