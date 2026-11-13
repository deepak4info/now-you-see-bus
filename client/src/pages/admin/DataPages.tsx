import { useEffect, useState, type DragEvent, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api, resolveUrl, useQuery } from "@/lib/api";
import { formatClock, formatEta } from "@/lib/format";
import type { CommunityUpdateItem, TrackSnapshot } from "@/lib/types";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { MapView } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";
import { StopQrModal } from "@/components/StopQrModal";
import { AlertCircle, ArrowDown, ArrowUp, BusFront, Check, CheckCircle2, ChevronDown, Edit3, GripVertical, IndianRupee, MapPin, MessageSquare, QrCode, Route as RouteIcon, Search, ShieldCheck, Sparkles, Trash2, X } from "lucide-react";

type Operator = { id: number; name: string; code: string; contactName: string | null; phone: string | null; email: string | null };
type Bus = { id: number; operatorId: number; operatorName: string; fleetNumber: string; registrationNumber: string; model: string | null; capacity: number; busType: string; assignedDriverId: number | null; driverName: string | null };
type Driver = { id: number; fullName: string; email: string; phone: string | null; driverCode: string; licenseNumber: string; operatorId: number | null; operatorName: string | null };
type Stop = { id: number; name: string; code: string; latitude: number; longitude: number; address: string | null; city: string | null };
type RouteFare = { id: number; fromStopId: number; from: string; toStopId: number; to: string; amount: number };
type RouteRow = { id: number; code: string; name: string; operatorId: number | null; distanceMode: "Auto" | "Manual"; distanceKm: number | null; addedAt: string | null; stops: { stopId: number; name: string; sequenceNo: number; distanceKm: number; segmentDistanceKm: number | null }[]; fares: RouteFare[] };
type Schedule = { id: number; routeId: number; routeCode: string; routeName: string; busId: number | null; fleetNumber: string | null; registrationNumber: string | null; driverId: number | null; driverName: string | null; driverCode: string | null; departureTime: string; serviceDays: string };

type BulkDeleteResponse = { deleted: number; skipped: number; message: string; skippedReasons?: string[] };
type BulkOperationResult = { successCount: number; skippedCount: number; message: string; errors?: string[] };
type BulkFeedback = { message: string; skippedReasons?: string[]; isError?: boolean };

function useTableSelection<T extends { id: number }>(items: T[] = []) {
  const [selectedIds, setSelectedIds] = useState<number[]>([]);

  const toggle = (id: number) => {
    setSelectedIds((prev) =>
      prev.includes(id) ? prev.filter((i) => i !== id) : [...prev, id]
    );
  };

  const itemIds = items.map((i) => i.id);
  const allSelected = items.length > 0 && itemIds.every((id) => selectedIds.includes(id));
  const someSelected = items.some((item) => selectedIds.includes(item.id));

  const toggleAll = () => {
    if (allSelected) {
      setSelectedIds([]);
    } else {
      setSelectedIds(itemIds);
    }
  };

  const clear = () => setSelectedIds([]);

  return { selectedIds, toggle, allSelected, someSelected, toggleAll, clear, count: selectedIds.length };
}

function BulkActionBar({
  count,
  entityName,
  onBulkUpdate,
  onBulkDelete,
  onClear,
  loading = false,
}: {
  count: number;
  entityName: string;
  onBulkUpdate?: () => void;
  onBulkDelete: () => void;
  onClear: () => void;
  loading?: boolean;
}) {
  if (count === 0) return null;
  return (
    <aside aria-label="Bulk actions toolbar" className="fixed bottom-6 left-1/2 -translate-x-1/2 z-50 flex items-center gap-3 rounded-2xl border border-primary/20 bg-background/95 px-4 py-2.5 shadow-2xl backdrop-blur-md ring-1 ring-black/5 animate-in fade-in slide-in-from-bottom-2">
      <div className="flex items-center gap-2">
        <span className="flex h-6 min-w-6 items-center justify-center rounded-full bg-primary text-xs font-bold text-primary-foreground px-1.5">
          {count}
        </span>
        <span className="text-xs sm:text-sm font-medium whitespace-nowrap">
          {entityName}{count > 1 ? "s" : ""} selected
        </span>
      </div>
      <div className="h-4 w-px bg-border" />
      <div className="flex items-center gap-2">
        {onBulkUpdate && (
          <Button size="sm" variant="outline" className="h-8 text-xs" onClick={onBulkUpdate} disabled={loading}>
            <Edit3 className="mr-1.5 h-3.5 w-3.5" />
            Bulk Update
          </Button>
        )}
        <Button size="sm" variant="destructive" className="h-8 text-xs" onClick={onBulkDelete} disabled={loading}>
          <Trash2 className="mr-1.5 h-3.5 w-3.5" />
          Bulk Delete
        </Button>
        <Button size="sm" variant="ghost" className="h-8 text-xs text-muted-foreground hover:text-foreground" onClick={onClear} disabled={loading}>
          Deselect
        </Button>
      </div>
    </aside>
  );
}

function BulkFeedbackBanner({
  feedback,
  onDismiss,
}: {
  feedback: BulkFeedback | null;
  onDismiss: () => void;
}) {
  if (!feedback) return null;
  return (
    <div className={cn("mb-4 rounded-xl border p-3.5 shadow-sm text-sm", feedback.isError ? "border-destructive/30 bg-destructive/10 text-destructive" : "border-emerald-600/30 bg-emerald-500/10 text-emerald-950")}>
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-start gap-2.5">
          {feedback.isError ? (
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
          ) : (
            <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" />
          )}
          <div className="space-y-1">
            <p className="font-semibold">{feedback.message}</p>
            {feedback.skippedReasons && feedback.skippedReasons.length > 0 && (
              <div className="mt-1.5 rounded-lg border border-amber-600/20 bg-amber-500/10 p-2.5 text-xs text-amber-950">
                <p className="font-medium">Skipped dependency details ({feedback.skippedReasons.length}):</p>
                <ul className="mt-1 list-disc pl-4 space-y-0.5">
                  {feedback.skippedReasons.map((reason, i) => (
                    <li key={i}>{reason}</li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        </div>
        <button type="button" onClick={onDismiss} className="text-xs font-semibold hover:underline shrink-0">
          Dismiss
        </button>
      </div>
    </div>
  );
}

function BulkDeleteDialog({
  open,
  onOpenChange,
  count,
  entityName,
  onConfirm,
  loading,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  count: number;
  entityName: string;
  onConfirm: () => Promise<void>;
  loading: boolean;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Confirm Bulk Delete</DialogTitle>
        </DialogHeader>
        <div className="space-y-3 py-2 text-sm text-muted-foreground">
          <p>
            Are you sure you want to delete <strong>{count}</strong> selected {entityName.toLowerCase()}{count > 1 ? "s" : ""}?
          </p>
          <div className="rounded-lg bg-amber-500/10 border border-amber-500/20 p-3 text-xs text-amber-900 space-y-1">
            <p className="font-semibold">Safe Deletion Policy:</p>
            <p>
              Records currently referenced by active routes, schedules, or live trips cannot be removed and will be automatically skipped with an explanatory notice.
            </p>
          </div>
        </div>
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={() => void onConfirm()} disabled={loading}>
            {loading ? "Deleting..." : `Delete ${count} ${entityName}${count > 1 ? "s" : ""}`}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function Field({ label, value, onChange, type = "text", maxLength, placeholder }: { label: string; value: string; onChange: (value: string) => void; type?: string; maxLength?: number; placeholder?: string }) {
  return (
    <div className="grid gap-1.5">
      <Label>{label}</Label>
      <Input type={type} value={value} maxLength={maxLength} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

function SelectField({ label, value, onChange, children }: { label: string; value: string; onChange: (value: string) => void; children: ReactNode }) {
  return (
    <div className="grid gap-1.5">
      <Label>{label}</Label>
      <select className="h-10 rounded-md border bg-card px-3 text-sm" value={value} onChange={(e) => onChange(e.target.value)}>{children}</select>
    </div>
  );
}

export function OperatorsPage() {
  const query = useQuery<Operator[]>("/api/admin/operators");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<Operator | null>(null);
  const [form, setForm] = useState({ name: "", code: "", contactName: "", phone: "", email: "" });
  const [error, setError] = useState("");

  const selection = useTableSelection(query.data ?? []);
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false);
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false);
  const [bulkLoading, setBulkLoading] = useState(false);
  const [bulkFeedback, setBulkFeedback] = useState<BulkFeedback | null>(null);
  const [bulkForm, setBulkForm] = useState({ contactName: "", phone: "", email: "" });
  const [bulkError, setBulkError] = useState("");

  function edit(row?: Operator) {
    setEditing(row ?? null);
    setForm({
      name: row?.name ?? "",
      code: row?.code ?? "",
      contactName: row?.contactName ?? "",
      phone: row?.phone ?? "",
      email: row?.email ?? "",
    });
    setError("");
    setOpen(true);
  }

  async function save() {
    setError("");
    try {
      const path = editing ? `/api/admin/operators/${editing.id}` : "/api/admin/operators";
      await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(form) });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
  }

  async function remove(id: number) {
    await api(`/api/admin/operators/${id}`, { method: "DELETE" });
    await query.reload();
  }

  async function handleBulkDelete() {
    setBulkLoading(true);
    setBulkFeedback(null);
    try {
      const res = await api<BulkDeleteResponse>("/api/admin/operators/bulk-delete", {
        method: "POST",
        body: JSON.stringify({ ids: selection.selectedIds }),
      });
      setBulkFeedback({
        message: res.message,
        skippedReasons: res.skippedReasons,
      });
      selection.clear();
      setBulkDeleteOpen(false);
      await query.reload();
    } catch (err) {
      setBulkFeedback({
        message: err instanceof Error ? err.message : "Bulk delete failed.",
        isError: true,
      });
    } finally {
      setBulkLoading(false);
    }
  }

  async function handleBulkUpdate() {
    setBulkLoading(true);
    setBulkError("");
    try {
      const body = {
        ids: selection.selectedIds,
        contactName: bulkForm.contactName.trim() || null,
        phone: bulkForm.phone.trim() || null,
        email: bulkForm.email.trim() || null,
      };
      const res = await api<BulkOperationResult>("/api/admin/operators/bulk-update", {
        method: "POST",
        body: JSON.stringify(body),
      });
      setBulkFeedback({ message: res.message });
      selection.clear();
      setBulkUpdateOpen(false);
      await query.reload();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk update failed.");
    } finally {
      setBulkLoading(false);
    }
  }

  return (
    <Page title="Operators" subtitle="Company owners that run the buses." action={<Button onClick={() => edit()}>Add operator</Button>} loading={query.loading} error={query.error}>
      <BulkFeedbackBanner feedback={bulkFeedback} onDismiss={() => setBulkFeedback(null)} />
      <Table
        className="max-w-4xl"
        headers={[
          {
            label: (
              <input
                type="checkbox"
                checked={selection.allSelected}
                onChange={selection.toggleAll}
                aria-label="Select all operators"
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
            ),
            className: "w-10 text-center whitespace-nowrap",
          },
          { label: "#", className: "w-12 text-center whitespace-nowrap" },
          { label: "Name", className: "w-[32%]" },
          { label: "Code", className: "w-[22%] whitespace-nowrap" },
          { label: "Contact", className: "w-[22%]" },
          { label: "", className: "w-[18%] text-right whitespace-nowrap" },
        ]}
      >
        {query.data?.map((row, index) => (
          <tr key={row.id} className={cn("border-t hover:bg-muted/30 transition-colors", selection.selectedIds.includes(row.id) && "bg-primary/5")}>
            <td className="px-3 py-2.5 text-center whitespace-nowrap">
              <input
                type="checkbox"
                checked={selection.selectedIds.includes(row.id)}
                onChange={() => selection.toggle(row.id)}
                aria-label={`Select operator ${row.name}`}
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
            </td>
            <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">{index + 1}</td>
            <td className="px-4 py-2.5 font-medium">{row.name}</td>
            <td className="px-4 py-2.5 whitespace-nowrap font-mono text-xs">{row.code}</td>
            <td className="px-4 py-2.5 text-muted-foreground">{row.contactName} {row.phone}</td>
            <td className="px-4 py-2.5 text-right whitespace-nowrap">
              <div className="inline-flex items-center justify-end gap-1">
                <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
                <Button size="sm" variant="ghost" onClick={() => void remove(row.id)}>Remove</Button>
              </div>
            </td>
          </tr>
        ))}
      </Table>
      <BulkActionBar
        count={selection.count}
        entityName="Operator"
        onBulkUpdate={() => {
          setBulkForm({ contactName: "", phone: "", email: "" });
          setBulkError("");
          setBulkUpdateOpen(true);
        }}
        onBulkDelete={() => setBulkDeleteOpen(true)}
        onClear={selection.clear}
        loading={bulkLoading}
      />
      <BulkDeleteDialog
        open={bulkDeleteOpen}
        onOpenChange={setBulkDeleteOpen}
        count={selection.count}
        entityName="Operator"
        onConfirm={handleBulkDelete}
        loading={bulkLoading}
      />
      <Dialog open={bulkUpdateOpen} onOpenChange={setBulkUpdateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Bulk Update ({selection.count} operators)</DialogTitle></DialogHeader>
          <p className="text-xs text-muted-foreground">
            Enter new values to update across all {selection.count} selected operators. Leave fields empty to keep current values.
          </p>
          <Field label="Contact Person" value={bulkForm.contactName} onChange={(contactName) => setBulkForm({ ...bulkForm, contactName })} />
          <Field label="Phone" value={bulkForm.phone} onChange={(phone) => setBulkForm({ ...bulkForm, phone })} />
          <Field label="Email" value={bulkForm.email} onChange={(email) => setBulkForm({ ...bulkForm, email })} />
          {bulkError && <p className="text-sm text-destructive">{bulkError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkUpdateOpen(false)} disabled={bulkLoading}>Cancel</Button>
            <Button onClick={() => void handleBulkUpdate()} disabled={bulkLoading}>
              {bulkLoading ? "Updating..." : `Update ${selection.count} Operators`}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit operator" : "Add operator"}</DialogTitle></DialogHeader>
          <Field label="Name" value={form.name} onChange={(name) => setForm({ ...form, name })} />
          <Field label="Code" value={form.code} onChange={(code) => setForm({ ...form, code })} />
          <Field label="Contact" value={form.contactName} onChange={(contactName) => setForm({ ...form, contactName })} />
          <Field label="Phone" value={form.phone} onChange={(phone) => setForm({ ...form, phone })} />
          <Field label="Email" value={form.email} onChange={(email) => setForm({ ...form, email })} />
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>Save</Button>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

export function BusesPage() {
  const query = useQuery<Bus[]>("/api/admin/buses");
  const operators = useQuery<Operator[]>("/api/admin/operators");
  const drivers = useQuery<Driver[]>("/api/admin/drivers");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<Bus | null>(null);
  const [form, setForm] = useState({ operatorId: "", fleetNumber: "", registrationNumber: "", model: "", capacity: "52", busType: "Express", assignedDriverId: "" });
  const [error, setError] = useState("");

  const selection = useTableSelection(query.data ?? []);
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false);
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false);
  const [bulkLoading, setBulkLoading] = useState(false);
  const [bulkFeedback, setBulkFeedback] = useState<BulkFeedback | null>(null);
  const [bulkForm, setBulkForm] = useState({ operatorId: "", busType: "", capacity: "", assignedDriverId: "" });
  const [bulkError, setBulkError] = useState("");

  function edit(row?: Bus) {
    setEditing(row ?? null);
    setForm({
      operatorId: String(row?.operatorId ?? operators.data?.[0]?.id ?? ""),
      fleetNumber: row?.fleetNumber ?? "",
      registrationNumber: row?.registrationNumber ?? "",
      model: row?.model ?? "",
      capacity: String(row?.capacity ?? 52),
      busType: row?.busType ?? "Express",
      assignedDriverId: row?.assignedDriverId ? String(row.assignedDriverId) : "",
    });
    setError("");
    setOpen(true);
  }

  async function save() {
    setError("");
    try {
      const body = {
        operatorId: Number(form.operatorId),
        fleetNumber: form.fleetNumber,
        registrationNumber: form.registrationNumber,
        model: form.model,
        capacity: Number(form.capacity),
        busType: form.busType,
        assignedDriverId: form.assignedDriverId ? Number(form.assignedDriverId) : null,
      };
      const path = editing ? `/api/admin/buses/${editing.id}` : "/api/admin/buses";
      await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(body) });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
  }

  async function handleBulkDelete() {
    setBulkLoading(true);
    setBulkFeedback(null);
    try {
      const res = await api<BulkDeleteResponse>("/api/admin/buses/bulk-delete", {
        method: "POST",
        body: JSON.stringify({ ids: selection.selectedIds }),
      });
      setBulkFeedback({
        message: res.message,
        skippedReasons: res.skippedReasons,
      });
      selection.clear();
      setBulkDeleteOpen(false);
      await query.reload();
    } catch (err) {
      setBulkFeedback({
        message: err instanceof Error ? err.message : "Bulk delete failed.",
        isError: true,
      });
    } finally {
      setBulkLoading(false);
    }
  }

  async function handleBulkUpdate() {
    setBulkLoading(true);
    setBulkError("");
    try {
      const body = {
        ids: selection.selectedIds,
        operatorId: bulkForm.operatorId ? Number(bulkForm.operatorId) : null,
        busType: bulkForm.busType.trim() || null,
        capacity: bulkForm.capacity ? Number(bulkForm.capacity) : null,
        assignedDriverId: bulkForm.assignedDriverId === "unassign" ? -1 : bulkForm.assignedDriverId ? Number(bulkForm.assignedDriverId) : null,
      };
      const res = await api<BulkOperationResult>("/api/admin/buses/bulk-update", {
        method: "POST",
        body: JSON.stringify(body),
      });
      setBulkFeedback({ message: res.message });
      selection.clear();
      setBulkUpdateOpen(false);
      await query.reload();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk update failed.");
    } finally {
      setBulkLoading(false);
    }
  }

  return (
    <Page title="Buses" subtitle="Vehicle records. Fleet 102 is HR39-1020." action={<Button onClick={() => edit()}>Add bus</Button>} loading={query.loading} error={query.error}>
      <BulkFeedbackBanner feedback={bulkFeedback} onDismiss={() => setBulkFeedback(null)} />
      <Table
        className="max-w-5xl"
        headers={[
          {
            label: (
              <input
                type="checkbox"
                checked={selection.allSelected}
                onChange={selection.toggleAll}
                aria-label="Select all buses"
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
            ),
            className: "w-10 text-center whitespace-nowrap",
          },
          { label: "#", className: "w-12 text-center whitespace-nowrap" },
          { label: "Fleet", className: "w-[15%] whitespace-nowrap" },
          { label: "Registration", className: "w-[22%] whitespace-nowrap" },
          { label: "Type", className: "w-[22%] whitespace-nowrap" },
          { label: "Driver", className: "w-[18%] whitespace-nowrap" },
          { label: "", className: "w-[17%] text-right whitespace-nowrap" },
        ]}
      >
        {query.data?.map((row, index) => (
          <tr key={row.id} className={cn("border-t hover:bg-muted/30 transition-colors", selection.selectedIds.includes(row.id) && "bg-primary/5")}>
            <td className="px-3 py-2.5 text-center whitespace-nowrap">
              <input
                type="checkbox"
                checked={selection.selectedIds.includes(row.id)}
                onChange={() => selection.toggle(row.id)}
                aria-label={`Select bus ${row.fleetNumber}`}
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
            </td>
            <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">{index + 1}</td>
            <td className="px-4 py-2.5 font-medium whitespace-nowrap">{row.fleetNumber}</td>
            <td className="px-4 py-2.5 whitespace-nowrap font-mono text-xs">{row.registrationNumber}</td>
            <td className="px-4 py-2.5 whitespace-nowrap">{row.busType} · {row.capacity}</td>
            <td className="px-4 py-2.5 whitespace-nowrap text-muted-foreground">{row.driverName ?? "Unassigned"}</td>
            <td className="px-4 py-2.5 text-right whitespace-nowrap">
              <div className="inline-flex items-center justify-end gap-1">
                <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
                <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/buses/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
              </div>
            </td>
          </tr>
        ))}
      </Table>
      <BulkActionBar
        count={selection.count}
        entityName="Bus"
        onBulkUpdate={() => {
          setBulkForm({ operatorId: "", busType: "", capacity: "", assignedDriverId: "" });
          setBulkError("");
          setBulkUpdateOpen(true);
        }}
        onBulkDelete={() => setBulkDeleteOpen(true)}
        onClear={selection.clear}
        loading={bulkLoading}
      />
      <BulkDeleteDialog
        open={bulkDeleteOpen}
        onOpenChange={setBulkDeleteOpen}
        count={selection.count}
        entityName="Bus"
        onConfirm={handleBulkDelete}
        loading={bulkLoading}
      />
      <Dialog open={bulkUpdateOpen} onOpenChange={setBulkUpdateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Bulk Update ({selection.count} buses)</DialogTitle></DialogHeader>
          <p className="text-xs text-muted-foreground">
            Update values across {selection.count} selected buses. Unchanged fields will retain their existing values.
          </p>
          <SelectField label="Operator" value={bulkForm.operatorId} onChange={(operatorId) => setBulkForm({ ...bulkForm, operatorId })}>
            <option value="">Keep unchanged</option>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          <SelectField label="Bus Type" value={bulkForm.busType} onChange={(busType) => setBulkForm({ ...bulkForm, busType })}>
            <option value="">Keep unchanged</option>
            <option value="Express">Express</option>
            <option value="Ordinary">Ordinary</option>
            <option value="Semi-Deluxe">Semi-Deluxe</option>
            <option value="AC Deluxe">AC Deluxe</option>
          </SelectField>
          <Field label="Capacity" value={bulkForm.capacity} onChange={(capacity) => setBulkForm({ ...bulkForm, capacity })} type="number" />
          <SelectField label="Assigned Driver" value={bulkForm.assignedDriverId} onChange={(assignedDriverId) => setBulkForm({ ...bulkForm, assignedDriverId })}>
            <option value="">Keep unchanged</option>
            <option value="unassign">Unassign Driver</option>
            {drivers.data?.map((driver) => <option key={driver.id} value={driver.id}>{driver.fullName} · {driver.driverCode}</option>)}
          </SelectField>
          {bulkError && <p className="text-sm text-destructive">{bulkError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkUpdateOpen(false)} disabled={bulkLoading}>Cancel</Button>
            <Button onClick={() => void handleBulkUpdate()} disabled={bulkLoading}>
              {bulkLoading ? "Updating..." : `Update ${selection.count} Buses`}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit bus" : "Add bus"}</DialogTitle></DialogHeader>
          <SelectField label="Operator" value={form.operatorId} onChange={(operatorId) => setForm({ ...form, operatorId })}>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          <Field label="Fleet number" value={form.fleetNumber} onChange={(fleetNumber) => setForm({ ...form, fleetNumber })} />
          <Field label="Registration" value={form.registrationNumber} onChange={(registrationNumber) => setForm({ ...form, registrationNumber })} />
          <Field label="Model" value={form.model} onChange={(model) => setForm({ ...form, model })} />
          <Field label="Capacity" value={form.capacity} onChange={(capacity) => setForm({ ...form, capacity })} type="number" />
          <Field label="Type" value={form.busType} onChange={(busType) => setForm({ ...form, busType })} />
          <SelectField label="Assigned driver" value={form.assignedDriverId} onChange={(assignedDriverId) => setForm({ ...form, assignedDriverId })}>
            <option value="">None</option>
            {drivers.data?.map((driver) => <option key={driver.id} value={driver.id}>{driver.fullName} · {driver.driverCode}</option>)}
          </SelectField>
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>Save</Button>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

export function DriversPage() {
  const query = useQuery<Driver[]>("/api/admin/drivers");
  const operators = useQuery<Operator[]>("/api/admin/operators");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<Driver | null>(null);
  const [form, setForm] = useState({ fullName: "", driverCode: "", email: "", phone: "", password: "", licenseNumber: "", operatorId: "" });
  const [error, setError] = useState("");

  const selection = useTableSelection(query.data ?? []);
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false);
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false);
  const [bulkLoading, setBulkLoading] = useState(false);
  const [bulkFeedback, setBulkFeedback] = useState<BulkFeedback | null>(null);
  const [bulkForm, setBulkForm] = useState({ operatorId: "" });
  const [bulkError, setBulkError] = useState("");

  function edit(row?: Driver) {
    setEditing(row ?? null);
    setForm({
      fullName: row?.fullName ?? "",
      driverCode: row?.driverCode ?? "",
      email: row?.email ?? "",
      phone: row?.phone ?? "",
      password: "",
      licenseNumber: row?.licenseNumber ?? "",
      operatorId: row?.operatorId ? String(row.operatorId) : "",
    });
    setError("");
    setOpen(true);
  }

  async function save() {
    setError("");
    try {
      const path = editing ? `/api/admin/drivers/${editing.id}` : "/api/admin/drivers";
      await api(path, {
        method: editing ? "PUT" : "POST",
        body: JSON.stringify({ ...form, operatorId: form.operatorId ? Number(form.operatorId) : null }),
      });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
  }

  async function handleBulkDelete() {
    setBulkLoading(true);
    setBulkFeedback(null);
    try {
      const res = await api<BulkDeleteResponse>("/api/admin/drivers/bulk-delete", {
        method: "POST",
        body: JSON.stringify({ ids: selection.selectedIds }),
      });
      setBulkFeedback({
        message: res.message,
        skippedReasons: res.skippedReasons,
      });
      selection.clear();
      setBulkDeleteOpen(false);
      await query.reload();
    } catch (err) {
      setBulkFeedback({
        message: err instanceof Error ? err.message : "Bulk delete failed.",
        isError: true,
      });
    } finally {
      setBulkLoading(false);
    }
  }

  async function handleBulkUpdate() {
    setBulkLoading(true);
    setBulkError("");
    try {
      const body = {
        ids: selection.selectedIds,
        operatorId: bulkForm.operatorId ? Number(bulkForm.operatorId) : null,
      };
      const res = await api<BulkOperationResult>("/api/admin/drivers/bulk-update", {
        method: "POST",
        body: JSON.stringify(body),
      });
      setBulkFeedback({ message: res.message });
      selection.clear();
      setBulkUpdateOpen(false);
      await query.reload();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk update failed.");
    } finally {
      setBulkLoading(false);
    }
  }

  return (
    <Page title="Drivers" subtitle="Rahul signs in with driver ID DRV-RAHUL." action={<Button onClick={() => edit()}>Add driver</Button>} loading={query.loading} error={query.error}>
      <BulkFeedbackBanner feedback={bulkFeedback} onDismiss={() => setBulkFeedback(null)} />
      <Table
        className="max-w-4xl"
        headers={[
          {
            label: (
              <input
                type="checkbox"
                checked={selection.allSelected}
                onChange={selection.toggleAll}
                aria-label="Select all drivers"
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
            ),
            className: "w-10 text-center whitespace-nowrap",
          },
          { label: "#", className: "w-12 text-center whitespace-nowrap" },
          { label: "Name", className: "w-[32%]" },
          { label: "Driver ID", className: "w-[22%] whitespace-nowrap" },
          { label: "Licence", className: "w-[22%] whitespace-nowrap" },
          { label: "", className: "w-[18%] text-right whitespace-nowrap" },
        ]}
      >
        {query.data?.map((row, index) => (
          <tr key={row.id} className={cn("border-t hover:bg-muted/30 transition-colors", selection.selectedIds.includes(row.id) && "bg-primary/5")}>
            <td className="px-3 py-2.5 text-center whitespace-nowrap">
              <input
                type="checkbox"
                checked={selection.selectedIds.includes(row.id)}
                onChange={() => selection.toggle(row.id)}
                aria-label={`Select driver ${row.fullName}`}
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
            </td>
            <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">{index + 1}</td>
            <td className="px-4 py-2.5 font-medium">{row.fullName}</td>
            <td className="px-4 py-2.5 whitespace-nowrap font-mono text-xs">{row.driverCode}</td>
            <td className="px-4 py-2.5 whitespace-nowrap text-muted-foreground">{row.licenseNumber}</td>
            <td className="px-4 py-2.5 text-right whitespace-nowrap">
              <div className="inline-flex items-center justify-end gap-1">
                <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
                <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/drivers/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
              </div>
            </td>
          </tr>
        ))}
      </Table>
      <BulkActionBar
        count={selection.count}
        entityName="Driver"
        onBulkUpdate={() => {
          setBulkForm({ operatorId: "" });
          setBulkError("");
          setBulkUpdateOpen(true);
        }}
        onBulkDelete={() => setBulkDeleteOpen(true)}
        onClear={selection.clear}
        loading={bulkLoading}
      />
      <BulkDeleteDialog
        open={bulkDeleteOpen}
        onOpenChange={setBulkDeleteOpen}
        count={selection.count}
        entityName="Driver"
        onConfirm={handleBulkDelete}
        loading={bulkLoading}
      />
      <Dialog open={bulkUpdateOpen} onOpenChange={setBulkUpdateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Bulk Update ({selection.count} drivers)</DialogTitle></DialogHeader>
          <p className="text-xs text-muted-foreground">
            Reassign the operator for all {selection.count} selected drivers.
          </p>
          <SelectField label="Operator" value={bulkForm.operatorId} onChange={(operatorId) => setBulkForm({ ...bulkForm, operatorId })}>
            <option value="">Keep unchanged</option>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          {bulkError && <p className="text-sm text-destructive">{bulkError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkUpdateOpen(false)} disabled={bulkLoading}>Cancel</Button>
            <Button onClick={() => void handleBulkUpdate()} disabled={bulkLoading}>
              {bulkLoading ? "Updating..." : `Update ${selection.count} Drivers`}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit driver" : "Add driver"}</DialogTitle></DialogHeader>
          <Field label="Name" value={form.fullName} onChange={(fullName) => setForm({ ...form, fullName })} />
          <Field label="Driver ID" value={form.driverCode} onChange={(driverCode) => setForm({ ...form, driverCode })} />
          <Field label="Email" value={form.email} onChange={(email) => setForm({ ...form, email })} />
          <Field label="Phone" value={form.phone} onChange={(phone) => setForm({ ...form, phone })} />
          <Field label={editing ? "New password (optional)" : "Password"} value={form.password} onChange={(password) => setForm({ ...form, password })} type="password" />
          <Field label="Licence" value={form.licenseNumber} onChange={(licenseNumber) => setForm({ ...form, licenseNumber })} />
          <SelectField label="Operator" value={form.operatorId} onChange={(operatorId) => setForm({ ...form, operatorId })}>
            <option value="">None</option>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>{editing ? "Save changes" : "Save"}</Button>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

function normalizeStopKey(name: string): string {
  let clean = name.trim().toLowerCase();
  clean = clean.replace(/\s+(bus stand|bus stop|depot|isbt|bypass|chowk)$/i, "");
  clean = clean.replace(/[\.\,\-\'\(\)]+/g, " ").trim();
  if (clean.endsWith("er") && clean.length > 4) {
    clean = clean.slice(0, -2) + "ar";
  }
  clean = clean.replace(/ss/g, "s").replace(/ee/g, "i").replace(/oo/g, "u").replace(/ph/g, "f");
  return clean;
}

export function StopsPage() {
  const query = useQuery<Stop[]>("/api/admin/stops");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<Stop | null>(null);
  const [form, setForm] = useState({ name: "", code: "", latitude: "", longitude: "", address: "", city: "" });
  const [error, setError] = useState("");
  const [removeError, setRemoveError] = useState("");
  const [removingStopId, setRemovingStopId] = useState<number | null>(null);
  const [cleaning, setCleaning] = useState(false);
  const [cleanMessage, setCleanMessage] = useState("");
  const [search, setSearch] = useState("");
  const [qrStop, setQrStop] = useState<Stop | null>(null);

  // Deduplicate stops in-memory so only unique records are displayed
  const uniqueStops = (query.data ?? []).reduce<Stop[]>((acc, row) => {
    const key = normalizeStopKey(row.name);
    const code = row.code.trim().toUpperCase();
    const isDemo = code.startsWith("DEMO-") || code.startsWith("TEST-") || code.startsWith("S-");

    const existingIndex = acc.findIndex((item) => {
      const itemKey = normalizeStopKey(item.name);
      const itemCode = item.code.trim().toUpperCase();
      return itemKey === key || itemCode === code;
    });

    if (existingIndex < 0) {
      acc.push(row);
    } else if (!isDemo && (acc[existingIndex].code.startsWith("DEMO-") || acc[existingIndex].code.startsWith("TEST-") || acc[existingIndex].code.startsWith("S-"))) {
      acc[existingIndex] = row;
    }
    return acc;
  }, []);

  const hasDuplicates = (query.data?.length ?? 0) > uniqueStops.length;

  const filtered = uniqueStops.filter((row) => {
    if (!search.trim()) return true;
    const q = search.toLowerCase().trim();
    return (
      row.name.toLowerCase().includes(q) ||
      row.code.toLowerCase().includes(q) ||
      (row.city && row.city.toLowerCase().includes(q))
    );
  });

  const selection = useTableSelection(filtered);
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false);
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false);
  const [bulkLoading, setBulkLoading] = useState(false);
  const [bulkFeedback, setBulkFeedback] = useState<BulkFeedback | null>(null);
  const [bulkForm, setBulkForm] = useState({ city: "", address: "" });
  const [bulkError, setBulkError] = useState("");

  async function cleanDuplicates() {
    setCleaning(true);
    setCleanMessage("");
    setRemoveError("");
    try {
      const res = await api<{ merged: number; remainingUniqueStops: number; message: string }>("/api/admin/stops/deduplicate", {
        method: "POST",
      });
      setCleanMessage(res.message);
      await query.reload();
      setTimeout(() => setCleanMessage(""), 6000);
    } catch (err) {
      setRemoveError(err instanceof Error ? err.message : "Failed to merge duplicate stops");
    } finally {
      setCleaning(false);
    }
  }

  function edit(row?: Stop) {
    setEditing(row ?? null);
    setForm({
      name: row?.name ?? "",
      code: row?.code ?? "",
      latitude: row ? String(row.latitude) : "",
      longitude: row ? String(row.longitude) : "",
      address: row?.address ?? "",
      city: row?.city ?? "",
    });
    setError("");
    setOpen(true);
  }

  async function save() {
    setError("");
    try {
      const body = { ...form, latitude: Number(form.latitude), longitude: Number(form.longitude) };
      const path = editing ? `/api/admin/stops/${editing.id}` : "/api/admin/stops";
      await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(body) });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
  }

  async function remove(row: Stop) {
    setRemoveError("");
    setRemovingStopId(row.id);
    try {
      await api(`/api/admin/stops/${row.id}`, { method: "DELETE" });
      await query.reload();
    } catch (err) {
      setRemoveError(err instanceof Error ? err.message : "Could not remove stop");
    } finally {
      setRemovingStopId(null);
    }
  }

  async function handleBulkDelete() {
    setBulkLoading(true);
    setBulkFeedback(null);
    try {
      const res = await api<BulkDeleteResponse>("/api/admin/stops/bulk-delete", {
        method: "POST",
        body: JSON.stringify({ ids: selection.selectedIds }),
      });
      setBulkFeedback({
        message: res.message,
        skippedReasons: res.skippedReasons,
      });
      selection.clear();
      setBulkDeleteOpen(false);
      await query.reload();
    } catch (err) {
      setBulkFeedback({
        message: err instanceof Error ? err.message : "Bulk delete failed.",
        isError: true,
      });
    } finally {
      setBulkLoading(false);
    }
  }

  async function handleBulkUpdate() {
    setBulkLoading(true);
    setBulkError("");
    try {
      const body = {
        ids: selection.selectedIds,
        city: bulkForm.city.trim() || null,
        address: bulkForm.address.trim() || null,
      };
      const res = await api<BulkOperationResult>("/api/admin/stops/bulk-update", {
        method: "POST",
        body: JSON.stringify(body),
      });
      setBulkFeedback({ message: res.message });
      selection.clear();
      setBulkUpdateOpen(false);
      await query.reload();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk update failed.");
    } finally {
      setBulkLoading(false);
    }
  }

  return (
    <Page
      title="Stops"
      subtitle="Pickup and drop points, with a map coordinate for each."
      action={
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => void cleanDuplicates()}
            disabled={cleaning}
            title="Scan database, merge duplicate stops, and rewire all route references"
          >
            <Sparkles className="mr-1.5 h-4 w-4 text-amber-600" />
            {cleaning ? "Merging…" : "Merge Duplicates"}
          </Button>
          <Button onClick={() => edit()}>Add stop</Button>
        </div>
      }
      loading={query.loading}
      error={query.error}
    >
      {cleanMessage && (
        <div className="mb-3 flex items-center gap-2 rounded-lg bg-emerald-50 px-3.5 py-2.5 text-xs font-medium text-emerald-800 border border-emerald-200">
          <CheckCircle2 className="h-4 w-4 text-emerald-600 flex-shrink-0" />
          <span>{cleanMessage}</span>
        </div>
      )}
      {removeError && <p className="mb-3 text-sm text-destructive" role="alert">{removeError}</p>}
      <BulkFeedbackBanner feedback={bulkFeedback} onDismiss={() => setBulkFeedback(null)} />
      <div className="max-w-4xl space-y-4">
        <div className="flex items-center justify-between gap-4">
          <div className="relative max-w-sm flex-1">
            <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              placeholder="Search stop name, code, or city…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-9"
            />
          </div>
          {query.data && (
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <span>
                Showing <strong className="text-ink">{filtered.length}</strong> unique stop{filtered.length === 1 ? "" : "s"}
              </span>
              {hasDuplicates && (
                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-800 border border-amber-200">
                  {query.data.length - uniqueStops.length} duplicate(s) filtered
                </span>
              )}
            </div>
          )}
        </div>
        <Table
          headers={[
            {
              label: (
                <input
                  type="checkbox"
                  checked={selection.allSelected}
                  onChange={selection.toggleAll}
                  aria-label="Select all stops"
                  className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
              ),
              className: "w-10 text-center whitespace-nowrap",
            },
            { label: "#", className: "w-12 text-center whitespace-nowrap" },
            { label: "Name", className: "w-[32%]" },
            { label: "Code", className: "w-[22%] whitespace-nowrap" },
            { label: "City", className: "w-[22%] whitespace-nowrap" },
            { label: "", className: "w-[18%] text-right whitespace-nowrap" },
          ]}
        >
          {filtered.map((row, index) => (
            <tr key={row.id} className={cn("border-t hover:bg-muted/30 transition-colors", selection.selectedIds.includes(row.id) && "bg-primary/5")}>
              <td className="px-3 py-2.5 text-center whitespace-nowrap">
                <input
                  type="checkbox"
                  checked={selection.selectedIds.includes(row.id)}
                  onChange={() => selection.toggle(row.id)}
                  aria-label={`Select stop ${row.name}`}
                  className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
              </td>
              <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">{index + 1}</td>
              <td className="px-4 py-2.5 font-medium">{row.name}</td>
              <td className="px-4 py-2.5 whitespace-nowrap font-mono text-xs">{row.code}</td>
              <td className="px-4 py-2.5 whitespace-nowrap text-muted-foreground">{row.city || "—"}</td>
              <td className="px-4 py-2.5 text-right whitespace-nowrap">
                <div className="inline-flex items-center justify-end gap-1">
                  <Button size="sm" variant="outline" className="h-7 px-2 text-xs gap-1" onClick={() => setQrStop(row)}>
                    <QrCode size={12} className="text-primary" /> QR
                  </Button>
                  <Button size="sm" variant="ghost" asChild className="h-7 px-2 text-xs">
                    <Link to={`/stop/${row.code}`} target="_blank">Board ↗</Link>
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
                  <Button size="sm" variant="ghost" disabled={removingStopId !== null} onClick={() => void remove(row)}>
                    {removingStopId === row.id ? "Removing…" : "Remove"}
                  </Button>
                </div>
              </td>
            </tr>
          ))}
        </Table>
      </div>
      <BulkActionBar
        count={selection.count}
        entityName="Stop"
        onBulkUpdate={() => {
          setBulkForm({ city: "", address: "" });
          setBulkError("");
          setBulkUpdateOpen(true);
        }}
        onBulkDelete={() => setBulkDeleteOpen(true)}
        onClear={selection.clear}
        loading={bulkLoading}
      />
      <BulkDeleteDialog
        open={bulkDeleteOpen}
        onOpenChange={setBulkDeleteOpen}
        count={selection.count}
        entityName="Stop"
        onConfirm={handleBulkDelete}
        loading={bulkLoading}
      />
      <Dialog open={bulkUpdateOpen} onOpenChange={setBulkUpdateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Bulk Update ({selection.count} stops)</DialogTitle></DialogHeader>
          <p className="text-xs text-muted-foreground">
            Update city/address across all {selection.count} selected stops. Leave blank to keep existing values unchanged.
          </p>
          <Field label="City" value={bulkForm.city} onChange={(city) => setBulkForm({ ...bulkForm, city })} />
          <Field label="Address / Landmark" value={bulkForm.address} onChange={(address) => setBulkForm({ ...bulkForm, address })} />
          {bulkError && <p className="text-sm text-destructive">{bulkError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkUpdateOpen(false)} disabled={bulkLoading}>Cancel</Button>
            <Button onClick={() => void handleBulkUpdate()} disabled={bulkLoading}>
              {bulkLoading ? "Updating..." : `Update ${selection.count} Stops`}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit stop" : "Add stop"}</DialogTitle></DialogHeader>
          <Field label="Name" value={form.name} onChange={(name) => setForm({ ...form, name })} />
          <Field label="Code" value={form.code} onChange={(code) => setForm({ ...form, code })} />
          <Field label="Latitude" value={form.latitude} onChange={(latitude) => setForm({ ...form, latitude })} />
          <Field label="Longitude" value={form.longitude} onChange={(longitude) => setForm({ ...form, longitude })} />
          <Field label="Address" value={form.address} onChange={(address) => setForm({ ...form, address })} />
          <Field label="City" value={form.city} onChange={(city) => setForm({ ...form, city })} />
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>Save</Button>
        </DialogContent>
      </Dialog>
      {qrStop && (
        <StopQrModal
          stop={{
            id: qrStop.id,
            name: qrStop.name,
            code: qrStop.code,
            city: qrStop.city,
            address: qrStop.address,
            latitude: Number(qrStop.latitude),
            longitude: Number(qrStop.longitude),
          }}
          isOpen={Boolean(qrStop)}
          onClose={() => setQrStop(null)}
        />
      )}
    </Page>
  );
}

function sameLocalDay(value: string | null) {
  if (!value) return false;
  const added = new Date(value);
  if (Number.isNaN(added.getTime())) return false;
  const today = new Date();
  return added.getFullYear() === today.getFullYear() && added.getMonth() === today.getMonth() && added.getDate() === today.getDate();
}

function routeAgeLabel(value: string | null) {
  if (!sameLocalDay(value)) {
    if (!value) return "Earlier";
    const added = new Date(value);
    if (Number.isNaN(added.getTime())) return "Earlier";
    return `Added ${added.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })}`;
  }
  return "Added today";
}

export function RoutesPage() {
  const query = useQuery<RouteRow[]>("/api/admin/routes");
  const stops = useQuery<Stop[]>("/api/admin/stops");
  const operators = useQuery<Operator[]>("/api/admin/operators");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<RouteRow | null>(null);
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [operatorId, setOperatorId] = useState("");
  const [stopIds, setStopIds] = useState<number[]>([]);
  const [distanceMode, setDistanceMode] = useState<"Auto" | "Manual">("Auto");
  const [segmentDistances, setSegmentDistances] = useState<string[]>([]);
  const [draggedStopId, setDraggedStopId] = useState<number | null>(null);
  const [stopOrderMessage, setStopOrderMessage] = useState("");
  const [pick, setPick] = useState("");
  const [error, setError] = useState("");
  const [fareDraft, setFareDraft] = useState<Record<number, string>>({});
  const [search, setSearch] = useState("");
  const [routeView, setRouteView] = useState<"all" | "new" | "earlier">("all");
  const [rebuildingRoads, setRebuildingRoads] = useState(false);
  const [rebuildStatus, setRebuildStatus] = useState<string | null>(null);
  const [savingRoute, setSavingRoute] = useState(false);

  async function snapAllRoutesToRoads() {
    setRebuildingRoads(true);
    setRebuildStatus(null);
    try {
      const res = await api<{ count: number; message?: string }>("/api/admin/routes/rebuild-roads", { method: "POST" });
      setRebuildStatus(res.message ?? `Recalculated road geometry for ${res.count} route(s).`);
      await query.reload();
    } catch (err) {
      setRebuildStatus(err instanceof Error ? err.message : "Failed to rebuild road geometries");
    } finally {
      setRebuildingRoads(false);
    }
  }

  const rankedRoutes = (query.data ?? []).map((route) => ({ route, isNew: sameLocalDay(route.addedAt) }));
  const newCount = rankedRoutes.filter((item) => item.isNew).length;
  const earlierCount = rankedRoutes.length - newCount;
  const filteredRoutes = rankedRoutes.filter(({ route, isNew }) => {
    if (routeView === "new" && !isNew) return false;
    if (routeView === "earlier" && isNew) return false;
    const operatorName = operators.data?.find((operator) => operator.id === route.operatorId)?.name ?? "";
    const searchable = [route.code, route.name, operatorName, ...route.stops.map((stop) => stop.name)].join(" ").toLowerCase();
    return searchable.includes(search.trim().toLowerCase());
  });
  const newRoutes = filteredRoutes.filter((item) => item.isNew);
  const earlierRoutes = filteredRoutes.filter((item) => !item.isNew);

  const selection = useTableSelection(filteredRoutes.map((i) => i.route));
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false);
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false);
  const [bulkLoading, setBulkLoading] = useState(false);
  const [bulkFeedback, setBulkFeedback] = useState<BulkFeedback | null>(null);
  const [bulkForm, setBulkForm] = useState({ operatorId: "", distanceMode: "" });
  const [bulkError, setBulkError] = useState("");

  async function handleBulkDelete() {
    setBulkLoading(true);
    setBulkFeedback(null);
    try {
      const res = await api<BulkDeleteResponse>("/api/admin/routes/bulk-delete", {
        method: "POST",
        body: JSON.stringify({ ids: selection.selectedIds }),
      });
      setBulkFeedback({
        message: res.message,
        skippedReasons: res.skippedReasons,
      });
      selection.clear();
      setBulkDeleteOpen(false);
      await query.reload();
    } catch (err) {
      setBulkFeedback({
        message: err instanceof Error ? err.message : "Bulk delete failed.",
        isError: true,
      });
    } finally {
      setBulkLoading(false);
    }
  }

  async function handleBulkUpdate() {
    setBulkLoading(true);
    setBulkError("");
    try {
      const body = {
        ids: selection.selectedIds,
        operatorId: bulkForm.operatorId ? Number(bulkForm.operatorId) : null,
        distanceMode: bulkForm.distanceMode || null,
      };
      const res = await api<BulkOperationResult>("/api/admin/routes/bulk-update", {
        method: "POST",
        body: JSON.stringify(body),
      });
      setBulkFeedback({ message: res.message });
      selection.clear();
      setBulkUpdateOpen(false);
      await query.reload();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk update failed.");
    } finally {
      setBulkLoading(false);
    }
  }

  function edit(row?: RouteRow) {
    setEditing(row ?? null);
    setCode(row?.code ?? "");
    setName(row?.name ?? "");
    setOperatorId(row?.operatorId ? String(row.operatorId) : String(operators.data?.[0]?.id ?? ""));
    const routeStops = row?.stops ?? [];
    setStopIds(routeStops.map((s) => s.stopId));
    setDistanceMode(row?.distanceMode ?? "Auto");
    setSegmentDistances(routeStops.slice(1).map((stop, index) =>
      String(stop.segmentDistanceKm ?? Math.max(0, stop.distanceKm - routeStops[index].distanceKm)),
    ));
    setError("");
    setStopOrderMessage("");
    setDraggedStopId(null);
    setOpen(true);
  }

  async function save() {
    setError("");
    const trimmedCode = code.trim();
    const trimmedName = name.trim();
    if (!trimmedCode) {
      setError("Enter a route code.");
      return;
    }
    if (trimmedCode.length > 20) {
      setError("Route code must be 20 characters or fewer.");
      return;
    }
    if (trimmedName.length < 2) {
      setError("Enter a route name.");
      return;
    }
    if (trimmedName.length > 150) {
      setError("Route name must be 150 characters or fewer.");
      return;
    }
    if (stopIds.length < 2) {
      setError("A route needs at least two stops.");
      return;
    }
    if (distanceMode === "Manual" && segmentDistances.length !== Math.max(0, stopIds.length - 1)) {
      setError("Enter a distance for every leg between stops.");
      return;
    }
    if (distanceMode === "Manual" && segmentDistances.some((distance) => !Number.isFinite(Number(distance)) || Number(distance) <= 0)) {
      setError("Each manual leg distance must be greater than 0 km.");
      return;
    }
    setSavingRoute(true);
    try {
      const body = {
        code: trimmedCode,
        name: trimmedName,
        operatorId: operatorId ? Number(operatorId) : null,
        stopIds,
        distanceMode,
        segmentDistancesKm: distanceMode === "Manual" ? segmentDistances.map(Number) : [],
      };
      const path = editing ? `/api/admin/routes/${editing.id}` : "/api/admin/routes";
      await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(body) });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    } finally {
      setSavingRoute(false);
    }
  }

  function removeStop(index: number) {
    const nextDistances = segmentDistances.slice();
    if (index === 0) {
      nextDistances.shift();
    } else if (index === stopIds.length - 1) {
      nextDistances.pop();
    } else {
      const before = Number(nextDistances[index - 1]);
      const after = Number(nextDistances[index]);
      const joined = Number.isFinite(before) && Number.isFinite(after) && before > 0 && after > 0
        ? String(Number((before + after).toFixed(2)))
        : "";
      nextDistances.splice(index - 1, 2, joined);
    }
    setStopIds(stopIds.filter((_, stopIndex) => stopIndex !== index));
    setSegmentDistances(nextDistances);
  }

  function moveStop(fromIndex: number, toIndex: number) {
    if (fromIndex < 0 || fromIndex >= stopIds.length || toIndex < 0 || toIndex >= stopIds.length || fromIndex === toIndex) return;

    const distancesByStopPair = new Map<string, string>();
    stopIds.slice(1).forEach((stopId, index) => {
      const pair = [stopIds[index], stopId].sort((a, b) => a - b);
      distancesByStopPair.set(`${pair[0]}:${pair[1]}`, segmentDistances[index] ?? "");
    });
    const nextStopIds = stopIds.slice();
    const [movedStopId] = nextStopIds.splice(fromIndex, 1);
    nextStopIds.splice(toIndex, 0, movedStopId);
    const nextDistances = nextStopIds.slice(1).map((stopId, index) => {
      const pair = [nextStopIds[index], stopId].sort((a, b) => a - b);
      return distancesByStopPair.get(`${pair[0]}:${pair[1]}`) ?? "";
    });

    setStopIds(nextStopIds);
    setSegmentDistances(nextDistances);
    const movedStopName = stops.data?.find((stop) => stop.id === movedStopId)?.name ?? "Stop";
    setStopOrderMessage(distanceMode === "Manual" && nextDistances.some((distance) => !distance.trim())
      ? `${movedStopName} moved to position ${toIndex + 1}. Enter a distance for each new neighboring stop pair.`
      : `${movedStopName} moved to position ${toIndex + 1}.`);
  }

  function dropStop(targetIndex: number, event: DragEvent<HTMLLIElement>) {
    event.preventDefault();
    const draggedId = Number(event.dataTransfer.getData("text/plain"));
    const sourceIndex = stopIds.indexOf(draggedId);
    if (sourceIndex >= 0) moveStop(sourceIndex, targetIndex);
    setDraggedStopId(null);
  }

  async function saveFares(route: RouteRow) {
    const fares = route.fares.map((fare) => ({
      fromStopId: fare.fromStopId,
      toStopId: fare.toStopId,
      amount: Number(fareDraft[fare.id] ?? fare.amount),
    }));
    await api(`/api/admin/routes/${route.id}/fares`, { method: "PUT", body: JSON.stringify({ fares }) });
    await query.reload();
  }

  return (
    <Page
      title="Routes"
      subtitle="New routes from today's CSV upload are listed first. Earlier routes stay in the second group."
      action={
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            onClick={snapAllRoutesToRoads}
            disabled={rebuildingRoads}
            title="Recalculate route paths along actual highways and roads"
          >
            {rebuildingRoads ? "Snapping to roads..." : "Snap Routes to Roads"}
          </Button>
          <Button onClick={() => edit()}>Create route</Button>
        </div>
      }
      loading={query.loading}
      error={query.error}
    >
      <BulkFeedbackBanner feedback={bulkFeedback} onDismiss={() => setBulkFeedback(null)} />
      <div className="grid gap-5">
        {rebuildStatus && (
          <div className="flex items-center justify-between rounded-xl border border-emerald-600/30 bg-emerald-500/10 p-3 text-sm text-emerald-900">
            <span>{rebuildStatus}</span>
            <button
              type="button"
              onClick={() => setRebuildStatus(null)}
              className="text-xs font-semibold text-emerald-700 hover:underline"
            >
              Dismiss
            </button>
          </div>
        )}
        <div className="grid gap-3 sm:grid-cols-3">
          <button type="button" onClick={() => setRouteView("new")} className={`flex items-center gap-3 rounded-xl border p-4 text-left shadow-sm ${routeView === "new" ? "border-emerald-600 bg-emerald-600/10" : "bg-card"}`}>
            <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-emerald-600/15 text-emerald-800"><RouteIcon size={20} /></span>
            <div><p className="text-sm text-muted-foreground">New from CSV</p><p className="text-2xl font-semibold">{newCount}</p></div>
          </button>
          <button type="button" onClick={() => setRouteView("earlier")} className={`flex items-center gap-3 rounded-xl border p-4 text-left shadow-sm ${routeView === "earlier" ? "border-primary bg-primary/5" : "bg-card"}`}>
            <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-muted text-muted-foreground"><MapPin size={20} /></span>
            <div><p className="text-sm text-muted-foreground">Already in the system</p><p className="text-2xl font-semibold">{earlierCount}</p></div>
          </button>
          <button type="button" onClick={() => setRouteView("all")} className={`flex items-center gap-3 rounded-xl border p-4 text-left shadow-sm ${routeView === "all" ? "border-primary bg-primary/5" : "bg-card"}`}>
            <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-primary/10 text-primary"><BusFront size={20} /></span>
            <div><p className="text-sm text-muted-foreground">All routes</p><p className="text-2xl font-semibold">{query.data?.length ?? 0}</p></div>
          </button>
        </div>

        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" size={18} aria-hidden="true" />
          <Input
            className="pl-10"
            type="search"
            aria-label="Search routes"
            placeholder="Search by route, operator, or stop"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
        </label>

        {filteredRoutes.length === 0 ? (
          <div className="rounded-xl border border-dashed bg-card px-6 py-12 text-center">
            <RouteIcon className="mx-auto mb-3 text-muted-foreground" size={28} aria-hidden="true" />
            <h2 className="font-semibold">{search ? "No routes match your search" : "No routes yet"}</h2>
            <p className="mt-1 text-sm text-muted-foreground">{search ? "Try another route code, operator, or stop name." : "Create a route to connect stops and set fares."}</p>
          </div>
        ) : (
          <div className="grid gap-4">
            <div className="flex items-center justify-between rounded-xl border bg-card px-4 py-2.5 text-xs shadow-xs">
              <label className="flex items-center gap-2 cursor-pointer font-medium select-none">
                <input
                  type="checkbox"
                  checked={selection.allSelected}
                  onChange={selection.toggleAll}
                  className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
                <span>Select all {filteredRoutes.length} visible route(s)</span>
              </label>
              {selection.count > 0 && (
                <span className="text-primary font-semibold">{selection.count} selected</span>
              )}
            </div>
            {(routeView === "new" ? newRoutes : routeView === "earlier" ? earlierRoutes : [...newRoutes, ...earlierRoutes]).map(({ route, isNew }, index, visible) => {
              const firstStop = route.stops[0]?.name ?? "No starting stop";
              const lastStop = route.stops[route.stops.length - 1]?.name ?? "No destination";
              const operatorName = operators.data?.find((operator) => operator.id === route.operatorId)?.name ?? "Operator not assigned";
              const showHeading = index === 0 || visible[index - 1].isNew !== isNew;
              return (
                <div key={route.id} className="grid gap-3">
                {showHeading && <div className={index > 0 ? "mt-4 border-t pt-5" : ""}>
                  <h2 className="text-lg font-semibold">{isNew ? "New from the CSV upload" : "Already in the system"}</h2>
                  <p className="text-sm text-muted-foreground">{isNew ? "Added today from the file you imported." : "Saved before today's upload."}</p>
                </div>}
                <article className={cn("overflow-hidden rounded-2xl border bg-card shadow-sm transition-shadow hover:shadow-md", isNew && "border-emerald-600/40", selection.selectedIds.includes(route.id) && "ring-2 ring-primary bg-primary/[0.02]")}>
                  <div className="p-4 sm:p-5">
                    <div className="flex flex-wrap items-start justify-between gap-4">
                      <div className="flex items-start gap-3 min-w-0 flex-1">
                        <input
                          type="checkbox"
                          checked={selection.selectedIds.includes(route.id)}
                          onChange={() => selection.toggle(route.id)}
                          aria-label={`Select route ${route.code}`}
                          className="mt-1 h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                        />
                        <div className="min-w-0 flex-1">
                          <div className="mb-2 flex flex-wrap items-center gap-2">
                            <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${isNew ? "bg-emerald-600/15 text-emerald-800" : "bg-muted text-muted-foreground"}`}>{isNew ? "New" : "Earlier"} · {routeAgeLabel(route.addedAt)}</span>
                            <span className="rounded-full bg-primary/10 px-2.5 py-1 text-xs font-semibold text-primary">{operatorName}</span>
                            <span className="rounded-full bg-muted px-2.5 py-1 text-xs text-muted-foreground">Route {route.code}</span>
                            <span className="rounded-full bg-muted px-2.5 py-1 text-xs text-muted-foreground">{route.distanceMode === "Manual" ? "Manual distance" : "By road"}</span>
                          </div>
                          <h2 className="font-display text-xl font-semibold leading-snug sm:text-2xl">{route.name}</h2>
                        </div>
                      </div>
                      <div className="flex shrink-0 items-baseline gap-1 rounded-xl bg-secondary/70 px-3 py-2">
                        <span className="text-lg font-semibold">{(route.distanceKm ?? 0).toFixed(2)}</span>
                        <span className="text-xs text-muted-foreground">{route.distanceMode === "Manual" ? "km entered" : "km by road"}</span>
                      </div>
                    </div>

                    <div className="mt-5 grid gap-3 rounded-xl bg-muted/70 p-4 sm:grid-cols-[1fr_auto_1fr] sm:items-center">
                      <div className="flex min-w-0 items-start gap-2.5">
                        <MapPin className="mt-0.5 shrink-0 text-primary" size={18} aria-hidden="true" />
                        <div className="min-w-0">
                          <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Start</p>
                          <p className="break-words font-medium">{firstStop}</p>
                        </div>
                      </div>
                      <div className="hidden items-center gap-2 text-muted-foreground sm:flex" aria-hidden="true">
                        <span className="h-px w-8 bg-border" /><RouteIcon size={17} /><span className="h-px w-8 bg-border" />
                      </div>
                      <div className="flex min-w-0 items-start gap-2.5 sm:justify-end sm:text-right">
                        <MapPin className="mt-0.5 shrink-0 text-primary sm:order-2" size={18} aria-hidden="true" />
                        <div className="min-w-0">
                          <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Destination</p>
                          <p className="break-words font-medium">{lastStop}</p>
                        </div>
                      </div>
                    </div>

                    <div className="mt-4">
                      <div className="mb-2 flex items-center justify-between gap-2">
                        <h3 className="text-sm font-semibold">Stops in order</h3>
                        <span className="text-xs text-muted-foreground">{route.stops.length} {route.stops.length === 1 ? "stop" : "stops"}</span>
                      </div>
                      {route.stops.length > 0 ? (
                        <ol className="flex gap-2 overflow-x-auto pb-2" aria-label={`Stops on route ${route.code}, in order`}>
                          {route.stops.map((stop, index) => (
                            <li key={`${stop.stopId}-${index}`} className="flex shrink-0 items-center gap-2">
                              <span className="inline-flex max-w-52 items-center gap-2 rounded-lg border bg-background/70 px-2.5 py-2 text-sm">
                                <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-primary/10 text-[11px] font-semibold text-primary">{index + 1}</span>
                                <span className="truncate">{stop.name}</span>
                              </span>
                              {index < route.stops.length - 1 && <span className="text-muted-foreground" aria-hidden="true">→</span>}
                            </li>
                          ))}
                        </ol>
                      ) : <p className="text-sm text-muted-foreground">No stops have been added to this route.</p>}
                    </div>

                    <div className="mt-3 flex flex-wrap items-center justify-between gap-3 border-t pt-4">
                      <p className="text-sm text-muted-foreground">{route.fares.length} {route.fares.length === 1 ? "fare" : "fares"} available</p>
                      <div className="flex flex-wrap gap-2">
                        <Button size="sm" variant="outline" onClick={() => edit(route)}>Edit route</Button>
                        <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/routes/${route.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
                      </div>
                    </div>

                    {route.fares.length > 0 && (
                      <details className="group mt-3 rounded-xl border bg-background/50">
                        <summary className="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 text-sm font-medium [&::-webkit-details-marker]:hidden">
                          <span className="flex items-center gap-2"><IndianRupee size={16} className="text-primary" />View and edit fares</span>
                          <ChevronDown size={17} className="text-muted-foreground transition-transform group-open:rotate-180" aria-hidden="true" />
                        </summary>
                        <div className="grid gap-2 border-t px-4 py-3 sm:grid-cols-2">
                          {route.fares.map((fare) => (
                            <label key={fare.id} className="flex items-center justify-between gap-3 rounded-lg bg-muted/60 px-3 py-2 text-sm">
                              <span className="min-w-0 truncate">{fare.from} <span className="text-muted-foreground">to</span> {fare.to}</span>
                              <span className="flex shrink-0 items-center gap-1">
                                <IndianRupee size={14} className="text-muted-foreground" aria-hidden="true" />
                                <Input className="h-9 w-24" type="number" min="0" step="0.01" aria-label={`Fare from ${fare.from} to ${fare.to}`} defaultValue={String(fare.amount)} onChange={(event) => setFareDraft((draft) => ({ ...draft, [fare.id]: event.target.value }))} />
                              </span>
                            </label>
                          ))}
                          <div className="flex items-center justify-between gap-2 sm:col-span-2">
                            <p className="text-xs text-muted-foreground">Amounts are in Indian rupees.</p>
                            <Button size="sm" variant="secondary" onClick={() => void saveFares(route)}>Save fares</Button>
                          </div>
                        </div>
                      </details>
                    )}
                  </div>
                </article>
                </div>
              );
            })}
          </div>
        )}
      </div>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit route" : "Create route"}</DialogTitle></DialogHeader>
          <Field label="Code (max 20 characters)" value={code} onChange={setCode} maxLength={20} placeholder="e.g. FTB-HSR-01" />
          <Field label="Name" value={name} onChange={setName} maxLength={150} placeholder="e.g. Fatehabad to Hisar Express" />
          <SelectField label="Operator" value={operatorId} onChange={setOperatorId}>
            <option value="">Unassigned</option>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          <SelectField label="Distance calculation" value={distanceMode} onChange={(value) => {
            if (value === "Auto" || value === "Manual") setDistanceMode(value);
          }}>
            <option value="Auto">Automatic from stop coordinates</option>
            <option value="Manual">Manual distances for each leg</option>
          </SelectField>
          <p className="text-xs text-muted-foreground">
            Automatic mode estimates each leg from stop coordinates. Manual mode lets you enter the road distance between each pair of stops.
          </p>
          <div className="grid gap-2">
            <div>
              <Label>Stops in order</Label>
              <p className="mt-1 text-xs text-muted-foreground">Drag a stop to reorder, or use the up and down buttons.</p>
              {stopOrderMessage && <p className="mt-1 rounded-md bg-primary/5 px-2 py-1 text-xs text-primary" role="status" aria-live="polite">{stopOrderMessage}</p>}
            </div>
            <ol className="grid gap-1 text-sm">
              {stopIds.map((id, index) => (
                <li
                  key={`${id}-${index}`}
                  draggable
                  onDragStart={(event) => {
                    event.dataTransfer.setData("text/plain", String(id));
                    event.dataTransfer.effectAllowed = "move";
                    setDraggedStopId(id);
                  }}
                  onDragEnd={() => setDraggedStopId(null)}
                  onDragOver={(event) => {
                    event.preventDefault();
                    event.dataTransfer.dropEffect = "move";
                  }}
                  onDrop={(event) => dropStop(index, event)}
                  className={`flex min-w-0 items-center gap-2 rounded-md bg-muted px-2 py-1.5 transition-opacity ${draggedStopId === id ? "opacity-40" : ""}`}
                >
                  <GripVertical size={16} className="shrink-0 cursor-grab text-muted-foreground active:cursor-grabbing" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate">{index + 1}. {stops.data?.find((s) => s.id === id)?.name}</span>
                  <div className="flex shrink-0 items-center">
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      aria-label={`Move ${stops.data?.find((stop) => stop.id === id)?.name ?? "stop"} up`}
                      disabled={index === 0}
                      onClick={() => moveStop(index, index - 1)}
                    >
                      <ArrowUp size={15} aria-hidden="true" />
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      aria-label={`Move ${stops.data?.find((stop) => stop.id === id)?.name ?? "stop"} down`}
                      disabled={index === stopIds.length - 1}
                      onClick={() => moveStop(index, index + 1)}
                    >
                      <ArrowDown size={15} aria-hidden="true" />
                    </Button>
                    <button type="button" className="rounded px-2 py-1 text-muted-foreground hover:bg-background hover:text-foreground" onClick={() => removeStop(index)}>Remove</button>
                  </div>
                </li>
              ))}
            </ol>
            <div className="flex gap-2">
              <select className="h-10 flex-1 rounded-md border bg-card px-3 text-sm" value={pick} onChange={(e) => setPick(e.target.value)}>
                <option value="">Add a stop</option>
                {stops.data?.filter((s) => !stopIds.includes(s.id)).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
              <Button type="button" variant="outline" onClick={() => {
                if (pick) {
                  setStopIds([...stopIds, Number(pick)]);
                  if (stopIds.length > 0) setSegmentDistances([...segmentDistances, ""]);
                  setPick("");
                }
              }}>Add</Button>
            </div>
          </div>
          {distanceMode === "Manual" && stopIds.length > 1 && (
            <div className="grid gap-2">
              <Label>Distance for each leg (km)</Label>
              {stopIds.slice(1).map((stopId, index) => {
                const from = stops.data?.find((stop) => stop.id === stopIds[index])?.name ?? "Stop";
                const to = stops.data?.find((stop) => stop.id === stopId)?.name ?? "Stop";
                return (
                  <label key={`${stopIds[index]}-${stopId}-${index}`} className="flex items-center justify-between gap-3 text-sm">
                    <span>{from} → {to}</span>
                    <Input
                      aria-label={`${from} to ${to} distance in kilometers`}
                      className="w-28"
                      type="number"
                      min="0.01"
                      step="0.01"
                      value={segmentDistances[index] ?? ""}
                      onChange={(event) => setSegmentDistances((current) => current.map((distance, distanceIndex) =>
                        distanceIndex === index ? event.target.value : distance,
                      ))}
                    />
                  </label>
                );
              })}
            </div>
          )}
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()} disabled={savingRoute}>
            {savingRoute ? "Saving…" : "Save route"}
          </Button>
        </DialogContent>
      </Dialog>
      <BulkActionBar
        count={selection.count}
        entityName="Route"
        onBulkUpdate={() => {
          setBulkForm({ operatorId: "", distanceMode: "" });
          setBulkError("");
          setBulkUpdateOpen(true);
        }}
        onBulkDelete={() => setBulkDeleteOpen(true)}
        onClear={selection.clear}
        loading={bulkLoading}
      />
      <BulkDeleteDialog
        open={bulkDeleteOpen}
        onOpenChange={setBulkDeleteOpen}
        count={selection.count}
        entityName="Route"
        onConfirm={handleBulkDelete}
        loading={bulkLoading}
      />
      <Dialog open={bulkUpdateOpen} onOpenChange={setBulkUpdateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Bulk Update ({selection.count} routes)</DialogTitle></DialogHeader>
          <p className="text-xs text-muted-foreground">
            Update attributes across all {selection.count} selected routes.
          </p>
          <SelectField label="Operator" value={bulkForm.operatorId} onChange={(operatorId) => setBulkForm({ ...bulkForm, operatorId })}>
            <option value="">Keep unchanged</option>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          <SelectField label="Distance Calculation Mode" value={bulkForm.distanceMode} onChange={(distanceMode) => setBulkForm({ ...bulkForm, distanceMode })}>
            <option value="">Keep unchanged</option>
            <option value="Auto">Automatic (Coordinates & Highways)</option>
            <option value="Manual">Manual (Manual Segment Distances)</option>
          </SelectField>
          {bulkError && <p className="text-sm text-destructive">{bulkError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkUpdateOpen(false)} disabled={bulkLoading}>Cancel</Button>
            <Button onClick={() => void handleBulkUpdate()} disabled={bulkLoading}>
              {bulkLoading ? "Updating..." : `Update ${selection.count} Routes`}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

const SCHEDULE_DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

const SCHEDULE_TIME_WINDOWS: Record<string, [number, number]> = {
  morning: [0, 12],
  afternoon: [12, 17],
  evening: [17, 21],
  night: [21, 24],
};

function matchesAssigneeFilter(entityId: number | null, filter: string): boolean {
  if (!filter) return true;
  if (filter === "unassigned") return entityId == null;
  if (filter === "assigned") return entityId != null;
  return String(entityId ?? "") === filter;
}

function parseScheduleHour24(departureTime: string): number | null {
  if (!departureTime) return null;
  const trimmed = departureTime.trim();
  const [hRaw] = trimmed.split(":");
  const h = Number(hRaw);
  if (Number.isNaN(h)) return null;
  const upper = trimmed.toUpperCase();
  if (upper.includes("PM") && h < 12) return h + 12;
  if (upper.includes("AM") && h === 12) return 0;
  return h;
}

function formatScheduleDays(daysMask: string): string {
  if (!daysMask || daysMask === "1111111") return "Daily";
  if (daysMask === "1111100") return "Mon–Fri";
  if (daysMask === "0000011") return "Sat–Sun";
  const active = SCHEDULE_DAYS.filter((_, idx) => daysMask[idx] === "1");
  return active.length > 0 ? active.join(", ") : "None";
}

export function SchedulesPage() {
  const query = useQuery<Schedule[]>("/api/admin/schedules");
  const routes = useQuery<RouteRow[]>("/api/admin/routes");
  const buses = useQuery<Bus[]>("/api/admin/buses");
  const drivers = useQuery<Driver[]>("/api/admin/drivers");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<Schedule | null>(null);
  const [form, setForm] = useState({ routeId: "", busId: "", driverId: "", departureTime: "08:00", serviceDays: "1111111" });
  const [error, setError] = useState("");

  const [search, setSearch] = useState("");
  const [routeFilter, setRouteFilter] = useState("");
  const [busFilter, setBusFilter] = useState("");
  const [driverFilter, setDriverFilter] = useState("");
  const [timeFilter, setTimeFilter] = useState("");
  const [dayFilter, setDayFilter] = useState("");

  const allSchedules = query.data ?? [];
  const filteredSchedules = allSchedules.filter((row) => {
    if (routeFilter && String(row.routeId) !== routeFilter) return false;
    if (!matchesAssigneeFilter(row.busId, busFilter)) return false;
    if (!matchesAssigneeFilter(row.driverId, driverFilter)) return false;

    if (timeFilter) {
      const hour = parseScheduleHour24(row.departureTime);
      const range = SCHEDULE_TIME_WINDOWS[timeFilter];
      if (hour === null || !range || hour < range[0] || hour >= range[1]) return false;
    }

    if (dayFilter) {
      const dayIndex = Number(dayFilter);
      if (!row.serviceDays || row.serviceDays[dayIndex] !== "1") return false;
    }

    if (search.trim()) {
      const q = search.trim().toLowerCase();
      const haystack = [
        row.routeCode,
        row.routeName,
        row.fleetNumber ?? "unassigned",
        row.registrationNumber ?? "",
        row.driverName ?? "unassigned",
        row.driverCode ?? "",
        row.departureTime,
        formatClock(row.departureTime),
      ]
        .join(" ")
        .toLowerCase();
      if (!haystack.includes(q)) return false;
    }

    return true;
  });

  const hasActiveFilters = Boolean(search.trim() || routeFilter || busFilter || driverFilter || timeFilter || dayFilter);

  function clearFilters() {
    setSearch("");
    setRouteFilter("");
    setBusFilter("");
    setDriverFilter("");
    setTimeFilter("");
    setDayFilter("");
  }

  const selection = useTableSelection(filteredSchedules);
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false);
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false);
  const [bulkLoading, setBulkLoading] = useState(false);
  const [bulkFeedback, setBulkFeedback] = useState<BulkFeedback | null>(null);
  const [bulkForm, setBulkForm] = useState({ busId: "", driverId: "", updateDays: false, serviceDays: "1111111" });
  const [bulkError, setBulkError] = useState("");

  function edit(row?: Schedule) {
    setEditing(row ?? null);
    const defaultRouteId = row?.routeId ?? (routeFilter ? Number(routeFilter) : routes.data?.[0]?.id);
    const defaultBusId = row?.busId ?? (busFilter && busFilter !== "unassigned" && busFilter !== "assigned" ? Number(busFilter) : undefined);
    const defaultDriverId = row?.driverId ?? (driverFilter && driverFilter !== "unassigned" && driverFilter !== "assigned" ? Number(driverFilter) : undefined);
    setForm({
      routeId: defaultRouteId ? String(defaultRouteId) : "",
      busId: defaultBusId ? String(defaultBusId) : "",
      driverId: defaultDriverId ? String(defaultDriverId) : "",
      departureTime: row?.departureTime ?? "08:00",
      serviceDays: row?.serviceDays ?? "1111111",
    });
    setError("");
    setOpen(true);
  }

  async function save() {
    setError("");
    try {
      const body = {
        routeId: Number(form.routeId),
        busId: form.busId ? Number(form.busId) : null,
        driverId: form.driverId ? Number(form.driverId) : null,
        departureTime: form.departureTime,
        serviceDays: form.serviceDays,
      };
      const path = editing ? `/api/admin/schedules/${editing.id}` : "/api/admin/schedules";
      await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(body) });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
  }

  async function handleBulkDelete() {
    setBulkLoading(true);
    setBulkFeedback(null);
    try {
      const res = await api<BulkDeleteResponse>("/api/admin/schedules/bulk-delete", {
        method: "POST",
        body: JSON.stringify({ ids: selection.selectedIds }),
      });
      setBulkFeedback({
        message: res.message,
        skippedReasons: res.skippedReasons,
      });
      selection.clear();
      setBulkDeleteOpen(false);
      await query.reload();
    } catch (err) {
      setBulkFeedback({
        message: err instanceof Error ? err.message : "Bulk delete failed.",
        isError: true,
      });
    } finally {
      setBulkLoading(false);
    }
  }

  async function handleBulkUpdate() {
    setBulkLoading(true);
    setBulkError("");
    try {
      const body = {
        ids: selection.selectedIds,
        busId: bulkForm.busId === "unassign" ? -1 : bulkForm.busId ? Number(bulkForm.busId) : null,
        driverId: bulkForm.driverId === "unassign" ? -1 : bulkForm.driverId ? Number(bulkForm.driverId) : null,
        serviceDays: bulkForm.updateDays ? bulkForm.serviceDays : null,
      };
      const res = await api<BulkOperationResult>("/api/admin/schedules/bulk-update", {
        method: "POST",
        body: JSON.stringify(body),
      });
      setBulkFeedback({ message: res.message });
      selection.clear();
      setBulkUpdateOpen(false);
      await query.reload();
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Bulk update failed.");
    } finally {
      setBulkLoading(false);
    }
  }

  const days = SCHEDULE_DAYS;

  return (
    <Page title="Schedules" subtitle="Attach a bus and a driver to a departure." action={<Button onClick={() => edit()}>Create schedule</Button>} loading={query.loading} error={query.error}>
      <BulkFeedbackBanner feedback={bulkFeedback} onDismiss={() => setBulkFeedback(null)} />
      <div className="mb-4 max-w-5xl space-y-3 rounded-xl border bg-card p-3.5 shadow-sm">
        <div className="flex flex-wrap items-center gap-2.5">
          <div className="relative min-w-[210px] flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              type="search"
              aria-label="Search schedules"
              placeholder="Search route, bus, driver, or time…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-9 pr-8"
            />
            {search && (
              <button
                type="button"
                aria-label="Clear schedule search"
                onClick={() => setSearch("")}
                className="absolute right-2.5 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-ink cursor-pointer"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            )}
          </div>

          <select
            aria-label="Filter by route"
            className="h-10 min-w-[150px] max-w-[220px] rounded-md border bg-background px-3 text-sm"
            value={routeFilter}
            onChange={(e) => setRouteFilter(e.target.value)}
          >
            <option value="">All routes</option>
            {routes.data?.map((route) => (
              <option key={route.id} value={String(route.id)}>
                {route.code} · {route.name}
              </option>
            ))}
          </select>

          <select
            aria-label="Filter by bus"
            className="h-10 min-w-[140px] max-w-[200px] rounded-md border bg-background px-3 text-sm"
            value={busFilter}
            onChange={(e) => setBusFilter(e.target.value)}
          >
            <option value="">All buses</option>
            <option value="unassigned">Unassigned bus</option>
            <option value="assigned">Assigned bus</option>
            {buses.data?.map((bus) => (
              <option key={bus.id} value={String(bus.id)}>
                {bus.fleetNumber} · {bus.registrationNumber}
              </option>
            ))}
          </select>

          <select
            aria-label="Filter by driver"
            className="h-10 min-w-[140px] max-w-[200px] rounded-md border bg-background px-3 text-sm"
            value={driverFilter}
            onChange={(e) => setDriverFilter(e.target.value)}
          >
            <option value="">All drivers</option>
            <option value="unassigned">Unassigned driver</option>
            <option value="assigned">Assigned driver</option>
            {drivers.data?.map((driver) => (
              <option key={driver.id} value={String(driver.id)}>
                {driver.fullName} · {driver.driverCode}
              </option>
            ))}
          </select>

          <select
            aria-label="Filter by time of day"
            className="h-10 min-w-[135px] rounded-md border bg-background px-3 text-sm"
            value={timeFilter}
            onChange={(e) => setTimeFilter(e.target.value)}
          >
            <option value="">Any time</option>
            <option value="morning">Morning (12 AM – 12 PM)</option>
            <option value="afternoon">Afternoon (12 PM – 5 PM)</option>
            <option value="evening">Evening (5 PM – 9 PM)</option>
            <option value="night">Night (9 PM – 12 AM)</option>
          </select>

          <select
            aria-label="Filter by service day"
            className="h-10 min-w-[115px] rounded-md border bg-background px-3 text-sm"
            value={dayFilter}
            onChange={(e) => setDayFilter(e.target.value)}
          >
            <option value="">Any day</option>
            {days.map((day, idx) => (
              <option key={day} value={String(idx)}>
                {day}
              </option>
            ))}
          </select>
        </div>

        <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
          <span>
            Showing <strong className="text-ink">{filteredSchedules.length}</strong> of{" "}
            <strong className="text-ink">{allSchedules.length}</strong> schedule{allSchedules.length === 1 ? "" : "s"}
          </span>
          {hasActiveFilters && (
            <button
              type="button"
              onClick={clearFilters}
              className="inline-flex items-center gap-1 font-medium text-primary hover:underline cursor-pointer"
            >
              <X className="h-3.5 w-3.5" />
              Clear filters
            </button>
          )}
        </div>
      </div>

      {filteredSchedules.length === 0 ? (
        <div className="max-w-5xl rounded-xl border border-dashed bg-card px-6 py-10 text-center shadow-sm">
          <p className="font-semibold text-ink">
            {hasActiveFilters ? "No schedules match your current filters" : "No schedules yet"}
          </p>
          <p className="mt-1 text-sm text-muted-foreground">
            {hasActiveFilters
              ? "Try adjusting your search or clearing active filters."
              : "Create a schedule to attach a bus and driver to a departure."}
          </p>
          {hasActiveFilters ? (
            <Button variant="outline" size="sm" className="mt-3" onClick={clearFilters}>
              Clear filters
            </Button>
          ) : (
            <Button size="sm" className="mt-3" onClick={() => edit()}>
              Create schedule
            </Button>
          )}
        </div>
      ) : (
        <Table
          className="max-w-5xl"
          headers={[
            {
              label: (
                <input
                  type="checkbox"
                  checked={selection.allSelected}
                  onChange={selection.toggleAll}
                  aria-label="Select all schedules"
                  className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
              ),
              className: "w-10 text-center whitespace-nowrap",
            },
            { label: "#", className: "w-12 text-center whitespace-nowrap" },
            { label: "Time", className: "w-[15%] whitespace-nowrap" },
            { label: "Route", className: "w-[18%] whitespace-nowrap" },
            { label: "Bus", className: "w-[22%] whitespace-nowrap" },
            { label: "Driver", className: "w-[22%] whitespace-nowrap" },
            { label: "", className: "w-[17%] text-right whitespace-nowrap" },
          ]}
        >
          {filteredSchedules.map((row, index) => (
            <tr key={row.id} className={cn("border-t hover:bg-muted/30 transition-colors", selection.selectedIds.includes(row.id) && "bg-primary/5")}>
              <td className="px-3 py-2.5 text-center whitespace-nowrap">
                <input
                  type="checkbox"
                  checked={selection.selectedIds.includes(row.id)}
                  onChange={() => selection.toggle(row.id)}
                  aria-label={`Select schedule ${row.id}`}
                  className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
                />
              </td>
              <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">{index + 1}</td>
              <td className="px-4 py-2.5 whitespace-nowrap">
                <span className="block font-medium">{formatClock(row.departureTime)}</span>
                <span className="block text-[11px] text-muted-foreground">{formatScheduleDays(row.serviceDays)}</span>
              </td>
              <td className="px-4 py-2.5 whitespace-nowrap" title={row.routeName}>
                <span className="block font-mono text-xs">{row.routeCode}</span>
                {row.routeName && row.routeName !== row.routeCode && (
                  <span className="block max-w-[170px] truncate text-[11px] text-muted-foreground">{row.routeName}</span>
                )}
              </td>
              <td className="px-4 py-2.5 whitespace-nowrap">{row.fleetNumber ? `${row.fleetNumber} · ${row.registrationNumber}` : "Unassigned"}</td>
              <td className="px-4 py-2.5 whitespace-nowrap text-muted-foreground">{row.driverName ? `${row.driverName} · ${row.driverCode}` : "Unassigned"}</td>
              <td className="px-4 py-2.5 text-right whitespace-nowrap">
                <div className="inline-flex items-center justify-end gap-1">
                  <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
                  <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/schedules/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
                </div>
              </td>
            </tr>
          ))}
        </Table>
      )}
      <BulkActionBar
        count={selection.count}
        entityName="Schedule"
        onBulkUpdate={() => {
          setBulkForm({ busId: "", driverId: "", updateDays: false, serviceDays: "1111111" });
          setBulkError("");
          setBulkUpdateOpen(true);
        }}
        onBulkDelete={() => setBulkDeleteOpen(true)}
        onClear={selection.clear}
        loading={bulkLoading}
      />
      <BulkDeleteDialog
        open={bulkDeleteOpen}
        onOpenChange={setBulkDeleteOpen}
        count={selection.count}
        entityName="Schedule"
        onConfirm={handleBulkDelete}
        loading={bulkLoading}
      />
      <Dialog open={bulkUpdateOpen} onOpenChange={setBulkUpdateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Bulk Update ({selection.count} schedules)</DialogTitle></DialogHeader>
          <p className="text-xs text-muted-foreground">
            Assign vehicles, drivers, or service days across all {selection.count} selected schedules.
          </p>
          <SelectField label="Bus" value={bulkForm.busId} onChange={(busId) => setBulkForm({ ...bulkForm, busId })}>
            <option value="">Keep unchanged</option>
            <option value="unassign">Unassign Bus</option>
            {buses.data?.map((bus) => <option key={bus.id} value={bus.id}>{bus.fleetNumber} · {bus.registrationNumber}</option>)}
          </SelectField>
          <SelectField label="Driver" value={bulkForm.driverId} onChange={(driverId) => setBulkForm({ ...bulkForm, driverId })}>
            <option value="">Keep unchanged</option>
            <option value="unassign">Unassign Driver</option>
            {drivers.data?.map((driver) => <option key={driver.id} value={driver.id}>{driver.fullName} · {driver.driverCode}</option>)}
          </SelectField>
          <div className="grid gap-2 pt-1 border-t">
            <label className="flex items-center gap-2 text-sm font-medium cursor-pointer">
              <input
                type="checkbox"
                checked={bulkForm.updateDays}
                onChange={(e) => setBulkForm({ ...bulkForm, updateDays: e.target.checked })}
                className="h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
              <span>Update Service Days</span>
            </label>
            {bulkForm.updateDays && (
              <div className="flex flex-wrap gap-2 pt-1">
                {days.map((day, index) => {
                  const on = bulkForm.serviceDays[index] === "1";
                  return (
                    <button
                      type="button"
                      key={day}
                      className={`rounded-full border px-3 py-1 text-xs ${on ? "bg-pine text-white" : "bg-card"}`}
                      onClick={() => {
                        const chars = bulkForm.serviceDays.padEnd(7, "0").slice(0, 7).split("");
                        chars[index] = on ? "0" : "1";
                        setBulkForm({ ...bulkForm, serviceDays: chars.join("") });
                      }}
                    >
                      {day}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
          {bulkError && <p className="text-sm text-destructive">{bulkError}</p>}
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkUpdateOpen(false)} disabled={bulkLoading}>Cancel</Button>
            <Button onClick={() => void handleBulkUpdate()} disabled={bulkLoading}>
              {bulkLoading ? "Updating..." : `Update ${selection.count} Schedules`}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit schedule" : "Create schedule"}</DialogTitle></DialogHeader>
          <SelectField label="Route" value={form.routeId} onChange={(routeId) => setForm({ ...form, routeId })}>
            {routes.data?.map((route) => <option key={route.id} value={route.id}>{route.code} · {route.name}</option>)}
          </SelectField>
          <SelectField label="Bus" value={form.busId} onChange={(busId) => setForm({ ...form, busId })}>
            <option value="">Unassigned</option>
            {buses.data?.map((bus) => <option key={bus.id} value={bus.id}>{bus.fleetNumber} · {bus.registrationNumber}</option>)}
          </SelectField>
          <SelectField label="Driver" value={form.driverId} onChange={(driverId) => setForm({ ...form, driverId })}>
            <option value="">Unassigned</option>
            {drivers.data?.map((driver) => <option key={driver.id} value={driver.id}>{driver.fullName} · {driver.driverCode}</option>)}
          </SelectField>
          <Field label="Departure" value={form.departureTime} onChange={(departureTime) => setForm({ ...form, departureTime })} type="time" />
          <div className="grid gap-1.5">
            <Label>Service days</Label>
            <div className="flex flex-wrap gap-2">
              {days.map((day, index) => {
                const on = form.serviceDays[index] === "1";
                return (
                  <button
                    type="button"
                    key={day}
                    className={`rounded-full border px-3 py-1 text-xs ${on ? "bg-pine text-white" : "bg-card"}`}
                    onClick={() => {
                      const chars = form.serviceDays.padEnd(7, "0").slice(0, 7).split("");
                      chars[index] = on ? "0" : "1";
                      setForm({ ...form, serviceDays: chars.join("") });
                    }}
                  >
                    {day}
                  </button>
                );
              })}
            </div>
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>Save schedule</Button>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

export function LivePage() {
  const query = useQuery<TrackSnapshot[]>("/api/admin/live");
  const [rows, setRows] = useState<TrackSnapshot[]>([]);
  useEffect(() => { if (query.data) setRows(query.data); }, [query.data]);
  useEffect(() => {
    let active = true;
    const conn = new HubConnectionBuilder().withUrl(resolveUrl("/hubs/tracking")).withAutomaticReconnect().build();
    conn.on("location", (snap: TrackSnapshot) => {
      setRows((prev) => {
        const index = prev.findIndex((row) => row.fleetNumber === snap.fleetNumber);
        if (snap.status !== "Running") {
          return index === -1 ? prev : prev.filter((row) => row.fleetNumber !== snap.fleetNumber);
        }
        if (index === -1) return [...prev, snap];
        const next = prev.slice();
        next[index] = snap;
        return next;
      });
    });
    conn.start()
      .then(() => {
        if (!active) return conn.stop();
        return conn.invoke("JoinLive");
      })
      .catch(() => undefined);
    return () => {
      active = false;
      if (conn.state === "Connected") void conn.stop();
    };
  }, []);

  const first = rows[0];
  const mappedBus = rows.find((row) =>
    row.recordedAt != null && row.latitude != null && row.longitude != null,
  );
  return (
    <Page title="Live board" subtitle="Every bus that is currently running." loading={query.loading} error={query.error}>
      {rows.length === 0 && <p className="rounded-xl border bg-card p-4 text-sm">No bus is on the road. Start operations, then have a driver start a trip.</p>}
      <div className="grid gap-4 lg:grid-cols-[1fr_320px]">
        <div className="h-[420px] overflow-hidden rounded-xl border">
          {first && (
            <MapView
              path={(mappedBus ?? first).path}
              stops={(mappedBus ?? first).stops}
              bus={mappedBus?.recordedAt != null && mappedBus.latitude != null && mappedBus.longitude != null ? {
                latitude: mappedBus.latitude,
                longitude: mappedBus.longitude,
                heading: mappedBus.heading,
                label: mappedBus.fleetNumber,
              } : null}
              className="h-full w-full"
            />
          )}
        </div>
        <div className="grid gap-2">
          {rows.map((row) => (
            <article key={row.fleetNumber} className="rounded-xl border bg-card p-3 text-sm">
              <div className="flex items-center justify-between">
                <p className="font-medium">Bus {row.fleetNumber}</p>
                <StatusBadge status={row.status} />
              </div>
              <p>{row.currentStop ?? "Between stops"} → {row.nextStop ?? "end"}</p>
              <p className="text-muted-foreground">ETA {formatEta(row.etaMinutes)}{row.outsideSlot ? " · outside the printed slot" : row.delayMinutes != null && row.delayMinutes > 2 ? ` · ${row.delayMinutes} min late` : ""}</p>
            </article>
          ))}
        </div>
      </div>
    </Page>
  );
}

export function ReportsPage() {
  const [reportTab, setReportTab] = useState<"trips" | "community">("trips");
  const [statusFilter, setStatusFilter] = useState("all");
  const [noteInputs, setNoteInputs] = useState<Record<number, string>>({});
  const [updatingId, setUpdatingId] = useState<number | null>(null);

  const query = useQuery<TrackSnapshot[]>(reportTab === "trips" ? "/api/admin/reports/trips" : null);
  const communityQuery = useQuery<CommunityUpdateItem[]>(
    reportTab === "community" ? `/api/admin/reports/feedback?status=${encodeURIComponent(statusFilter)}` : null,
  );

  async function verifyUpdate(id: number, status: "Published" | "Rejected" | "Resolved") {
    setUpdatingId(id);
    try {
      await api(`/api/admin/reports/feedback/${id}/status`, {
        method: "POST",
        body: JSON.stringify({
          status,
          adminNotes: noteInputs[id] || null,
          publishAlert: status === "Published",
        }),
      });
      await communityQuery.reload();
    } finally {
      setUpdatingId(null);
    }
  }

  return (
    <Page
      title={reportTab === "trips" ? "Trip & Community Reports" : "Community Updates Queue"}
      subtitle={
        reportTab === "trips"
          ? "Today's trips, with delay against the 42 km/h timetable."
          : "Verify passenger-submitted bus delays, cancellations, route suggestions & updated timings before publishing."
      }
      action={
        <div className="flex items-center gap-2 rounded-xl border bg-muted/40 p-1">
          <button
            type="button"
            onClick={() => setReportTab("trips")}
            className={`rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors ${
              reportTab === "trips" ? "bg-card text-ink shadow-xs" : "text-muted-foreground hover:text-ink"
            }`}
          >
            Trip Dispatch Report
          </button>
          <button
            type="button"
            onClick={() => setReportTab("community")}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors ${
              reportTab === "community" ? "bg-card text-ink shadow-xs" : "text-muted-foreground hover:text-ink"
            }`}
          >
            <MessageSquare className="h-3.5 w-3.5 text-pine" /> Community Updates
          </button>
        </div>
      }
      loading={reportTab === "trips" ? query.loading : communityQuery.loading}
      error={reportTab === "trips" ? query.error : communityQuery.error}
    >
      {reportTab === "trips" ? (
        <>
          <Table
            className="max-w-6xl"
            headers={[
              { label: "#", className: "w-12 text-center whitespace-nowrap" },
              { label: "Bus", className: "whitespace-nowrap" },
              { label: "Route", className: "whitespace-nowrap" },
              { label: "Departs", className: "whitespace-nowrap" },
              { label: "Status", className: "whitespace-nowrap" },
              { label: "Now", className: "whitespace-nowrap" },
              { label: "Delay", className: "whitespace-nowrap" },
              { label: "Driver", className: "whitespace-nowrap" },
            ]}
          >
            {query.data?.map((row, index) => (
              <tr key={row.tripId} className="border-t hover:bg-muted/30 transition-colors">
                <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">{index + 1}</td>
                <td className="px-4 py-2.5 whitespace-nowrap font-medium">{row.fleetNumber} · {row.registrationNumber}</td>
                <td className="px-4 py-2.5 whitespace-nowrap font-mono text-xs">{row.routeCode}</td>
                <td className="px-4 py-2.5 whitespace-nowrap">{formatClock(row.departureTime)}</td>
                <td className="px-4 py-2.5 whitespace-nowrap">{row.status}</td>
                <td className="px-4 py-2.5 whitespace-nowrap">{row.currentStop ?? "—"}</td>
                <td className="px-4 py-2.5 whitespace-nowrap">{row.outsideSlot ? "Outside the printed slot" : row.delayMinutes != null && row.delayMinutes > 2 ? `${row.delayMinutes} min late` : "On time"}</td>
                <td className="px-4 py-2.5 whitespace-nowrap text-muted-foreground">{row.driverName ?? "—"}</td>
              </tr>
            ))}
          </Table>
          {query.data && query.data.length === 0 && <p className="mt-3 text-sm text-muted-foreground">No trips yet. Start operations from the overview.</p>}
        </>
      ) : (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-2 text-xs">
            <span className="font-semibold text-muted-foreground mr-1">Filter Status:</span>
            {["all", "Pending", "Published", "Rejected", "Open", "Resolved"].map((st) => (
              <button
                key={st}
                type="button"
                onClick={() => setStatusFilter(st)}
                className={`rounded-full px-3 py-1 font-medium transition-colors ${
                  statusFilter === st ? "bg-pine text-white" : "border bg-card text-muted-foreground hover:bg-muted"
                }`}
              >
                {st === "all" ? "All Submissions" : st}
              </button>
            ))}
          </div>

          {communityQuery.data && communityQuery.data.length === 0 && (
            <p className="rounded-xl border border-dashed bg-card p-6 text-center text-sm text-muted-foreground">
              No community submissions match this status filter.
            </p>
          )}

          <div className="grid gap-3 max-w-5xl">
            {communityQuery.data?.map((item) => (
              <article key={item.id} className="rounded-xl border bg-card p-4 shadow-2xs">
                <div className="flex flex-wrap items-start justify-between gap-2 border-b pb-2.5">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="rounded-md bg-muted px-2 py-0.5 text-[11px] font-bold uppercase tracking-wider text-ink">
                      {item.category}
                    </span>
                    <span className="font-semibold text-sm text-ink">
                      Bus/Corridor: {item.fleetNumber} {item.routeCode ? `(Route ${item.routeCode})` : ""}
                    </span>
                    <span
                      className={`rounded-full px-2.5 py-0.5 text-[11px] font-semibold ${
                        item.status === "Published" || item.status === "Verified"
                          ? "bg-emerald-100 text-emerald-800"
                          : item.status === "Rejected"
                          ? "bg-red-100 text-red-800"
                          : "bg-amber-100 text-amber-800"
                      }`}
                    >
                      {item.status}
                    </span>
                  </div>
                  <span className="text-xs text-muted-foreground">{formatClock(item.createdAt)}</span>
                </div>

                <p className="mt-2.5 text-sm text-ink leading-relaxed">{item.description}</p>

                <div className="mt-2 flex flex-wrap gap-4 text-xs text-muted-foreground">
                  {item.reportedStopName && <span><b>Stop/Route:</b> {item.reportedStopName}</span>}
                  {item.delayMinutes && <span><b>Delay reported:</b> +{item.delayMinutes} min</span>}
                  {item.proposedTime && <span><b>Proposed time:</b> {item.proposedTime}</span>}
                  {item.verifiedBy && <span><b>Verified by:</b> {item.verifiedBy}</span>}
                </div>

                <div className="mt-3 flex flex-wrap items-center gap-2 border-t pt-3">
                  <Input
                    placeholder="Admin verification note (optional, visible when published)..."
                    value={noteInputs[item.id] ?? item.adminNotes ?? ""}
                    onChange={(e) => setNoteInputs((prev) => ({ ...prev, [item.id]: e.target.value }))}
                    className="h-8 flex-1 text-xs min-w-[200px]"
                  />
                  <Button
                    size="sm"
                    disabled={updatingId === item.id || item.status === "Published"}
                    onClick={() => void verifyUpdate(item.id, "Published")}
                    className="h-8 text-xs bg-emerald-600 hover:bg-emerald-700 text-white"
                  >
                    <ShieldCheck className="mr-1 h-3.5 w-3.5" /> Verify & Publish
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={updatingId === item.id || item.status === "Rejected"}
                    onClick={() => void verifyUpdate(item.id, "Rejected")}
                    className="h-8 text-xs text-destructive hover:bg-destructive/10"
                  >
                    <X className="mr-1 h-3.5 w-3.5" /> Reject
                  </Button>
                </div>
              </article>
            ))}
          </div>
        </div>
      )}
    </Page>
  );
}

function Page({ title, subtitle, action, loading, error, children }: { title: string; subtitle: string; action?: ReactNode; loading: boolean; error: string | null; children: ReactNode }) {
  return (
    <div>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="font-display text-4xl">{title}</h1>
          <p className="mt-1 text-muted-foreground">{subtitle}</p>
        </div>
        {action}
      </div>
      {loading && <p className="mt-6 text-muted-foreground">Loading…</p>}
      {error && <p className="mt-6 text-destructive">{error}</p>}
      {!loading && !error && <div className="mt-5">{children}</div>}
    </div>
  );
}

type HeaderItem = string | { label: ReactNode; className?: string };

function Table({ headers, children, className }: { headers: HeaderItem[]; children: ReactNode; className?: string }) {
  return (
    <div className={cn("overflow-x-auto rounded-xl border bg-card shadow-sm", className)}>
      <table className="w-full text-left text-sm">
        <thead className="border-b bg-muted/40 text-xs uppercase tracking-wider text-muted-foreground">
          <tr>
            {headers.map((header, idx) => {
              const label = typeof header === "string" ? header : header.label;
              const customClass = typeof header === "object" ? header.className : "";
              const isActionCol = idx === headers.length - 1 && !label;
              return (
                <th
                  key={typeof label === "string" ? `${label}-${idx}` : `col-${idx}`}
                  className={cn(
                    "px-4 py-3 font-medium",
                    isActionCol && "text-right",
                    customClass,
                  )}
                >
                  {label}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}
