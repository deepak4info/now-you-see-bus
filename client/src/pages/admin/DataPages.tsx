import { useEffect, useState, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api, useQuery } from "@/lib/api";
import { formatClock, formatEta } from "@/lib/format";
import type { TrackSnapshot } from "@/lib/types";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { MapView } from "@/components/MapView";
import { StatusBadge } from "@/components/StatusBadge";

type Operator = { id: number; name: string; code: string; contactName: string | null; phone: string | null; email: string | null };
type Bus = { id: number; operatorId: number; operatorName: string; fleetNumber: string; registrationNumber: string; model: string | null; capacity: number; busType: string; assignedDriverId: number | null; driverName: string | null };
type Driver = { id: number; fullName: string; email: string; phone: string | null; driverCode: string; licenseNumber: string; operatorId: number | null; operatorName: string | null };
type Stop = { id: number; name: string; code: string; latitude: number; longitude: number; address: string | null; city: string | null };
type RouteFare = { id: number; fromStopId: number; from: string; toStopId: number; to: string; amount: number };
type RouteRow = { id: number; code: string; name: string; operatorId: number | null; distanceKm: number | null; stops: { stopId: number; name: string; sequenceNo: number; distanceKm: number }[]; fares: RouteFare[] };
type Schedule = { id: number; routeId: number; routeCode: string; routeName: string; busId: number | null; fleetNumber: string | null; registrationNumber: string | null; driverId: number | null; driverName: string | null; driverCode: string | null; departureTime: string; serviceDays: string };

function Field({ label, value, onChange, type = "text" }: { label: string; value: string; onChange: (value: string) => void; type?: string }) {
  return (
    <div className="grid gap-1.5">
      <Label>{label}</Label>
      <Input type={type} value={value} onChange={(e) => onChange(e.target.value)} />
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

  return (
    <Page title="Operators" subtitle="Company owners that run the buses." action={<Button onClick={() => edit()}>Add operator</Button>} loading={query.loading} error={query.error}>
      <Table headers={["Name", "Code", "Contact", ""]}>
        {query.data?.map((row) => (
          <tr key={row.id} className="border-t">
            <td className="px-3 py-2">{row.name}</td>
            <td className="px-3 py-2">{row.code}</td>
            <td className="px-3 py-2">{row.contactName} {row.phone}</td>
            <td className="px-3 py-2 text-right">
              <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
              <Button size="sm" variant="ghost" onClick={() => void remove(row.id)}>Remove</Button>
            </td>
          </tr>
        ))}
      </Table>
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

  return (
    <Page title="Buses" subtitle="Vehicle records. Fleet 102 is HR39-1020." action={<Button onClick={() => edit()}>Add bus</Button>} loading={query.loading} error={query.error}>
      <Table headers={["Fleet", "Registration", "Type", "Driver", ""]}>
        {query.data?.map((row) => (
          <tr key={row.id} className="border-t">
            <td className="px-3 py-2">{row.fleetNumber}</td>
            <td className="px-3 py-2">{row.registrationNumber}</td>
            <td className="px-3 py-2">{row.busType} · {row.capacity}</td>
            <td className="px-3 py-2">{row.driverName ?? "Unassigned"}</td>
            <td className="px-3 py-2 text-right">
              <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
              <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/buses/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
            </td>
          </tr>
        ))}
      </Table>
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
  const [form, setForm] = useState({ fullName: "", driverCode: "", email: "", phone: "", password: "", licenseNumber: "", operatorId: "" });
  const [error, setError] = useState("");

  async function save() {
    setError("");
    try {
      await api("/api/admin/drivers", {
        method: "POST",
        body: JSON.stringify({ ...form, operatorId: form.operatorId ? Number(form.operatorId) : null }),
      });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
  }

  return (
    <Page title="Drivers" subtitle="Rahul signs in with driver ID DRV-RAHUL." action={<Button onClick={() => { setError(""); setOpen(true); }}>Add driver</Button>} loading={query.loading} error={query.error}>
      <Table headers={["Name", "Driver ID", "Licence", ""]}>
        {query.data?.map((row) => (
          <tr key={row.id} className="border-t">
            <td className="px-3 py-2">{row.fullName}</td>
            <td className="px-3 py-2">{row.driverCode}</td>
            <td className="px-3 py-2">{row.licenseNumber}</td>
            <td className="px-3 py-2 text-right">
              <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/drivers/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
            </td>
          </tr>
        ))}
      </Table>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Add driver</DialogTitle></DialogHeader>
          <Field label="Name" value={form.fullName} onChange={(fullName) => setForm({ ...form, fullName })} />
          <Field label="Driver ID" value={form.driverCode} onChange={(driverCode) => setForm({ ...form, driverCode })} />
          <Field label="Email" value={form.email} onChange={(email) => setForm({ ...form, email })} />
          <Field label="Phone" value={form.phone} onChange={(phone) => setForm({ ...form, phone })} />
          <Field label="Password" value={form.password} onChange={(password) => setForm({ ...form, password })} type="password" />
          <Field label="Licence" value={form.licenseNumber} onChange={(licenseNumber) => setForm({ ...form, licenseNumber })} />
          <SelectField label="Operator" value={form.operatorId} onChange={(operatorId) => setForm({ ...form, operatorId })}>
            <option value="">None</option>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>Save</Button>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

export function StopsPage() {
  const query = useQuery<Stop[]>("/api/admin/stops");
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<Stop | null>(null);
  const [form, setForm] = useState({ name: "", code: "", latitude: "", longitude: "", address: "", city: "" });
  const [error, setError] = useState("");

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

  return (
    <Page title="Stops" subtitle="Pickup and drop points, with a map coordinate for each." action={<Button onClick={() => edit()}>Add stop</Button>} loading={query.loading} error={query.error}>
      <Table headers={["Name", "Code", "City", ""]}>
        {query.data?.map((row) => (
          <tr key={row.id} className="border-t">
            <td className="px-3 py-2">{row.name}</td>
            <td className="px-3 py-2">{row.code}</td>
            <td className="px-3 py-2">{row.city}</td>
            <td className="px-3 py-2 text-right">
              <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
              <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/stops/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
            </td>
          </tr>
        ))}
      </Table>
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
    </Page>
  );
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
  const [pick, setPick] = useState("");
  const [error, setError] = useState("");
  const [fareDraft, setFareDraft] = useState<Record<number, string>>({});

  function edit(row?: RouteRow) {
    setEditing(row ?? null);
    setCode(row?.code ?? "");
    setName(row?.name ?? "");
    setOperatorId(row?.operatorId ? String(row.operatorId) : String(operators.data?.[0]?.id ?? ""));
    setStopIds(row?.stops.map((s) => s.stopId) ?? []);
    setError("");
    setOpen(true);
  }

  async function save() {
    setError("");
    try {
      const body = { code, name, operatorId: operatorId ? Number(operatorId) : null, stopIds };
      const path = editing ? `/api/admin/routes/${editing.id}` : "/api/admin/routes";
      await api(path, { method: editing ? "PUT" : "POST", body: JSON.stringify(body) });
      setOpen(false);
      await query.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save");
    }
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
    <Page title="Routes" subtitle="Which stops connect, in order. Fares are filled from distance and can be edited." action={<Button onClick={() => edit()}>Create route</Button>} loading={query.loading} error={query.error}>
      <div className="grid gap-4">
        {query.data?.map((route) => (
          <article key={route.id} className="rounded-xl border bg-card p-4">
            <div className="flex flex-wrap items-start justify-between gap-2">
              <div>
                <h2 className="font-display text-2xl">Route {route.code}</h2>
                <p className="text-sm text-muted-foreground">{route.name} · {route.distanceKm ?? 0} km</p>
                <p className="mt-1 text-sm">{route.stops.map((s) => s.name).join(" → ")}</p>
              </div>
              <div className="flex gap-2">
                <Button size="sm" variant="outline" onClick={() => edit(route)}>Edit stops</Button>
                <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/routes/${route.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
              </div>
            </div>
            {route.fares.length > 0 && (
              <div className="mt-3 grid gap-2">
                {route.fares.map((fare) => (
                  <label key={fare.id} className="flex items-center justify-between gap-3 text-sm">
                    <span>{fare.from} → {fare.to}</span>
                    <Input className="w-28" defaultValue={String(fare.amount)} onChange={(e) => setFareDraft((d) => ({ ...d, [fare.id]: e.target.value }))} />
                  </label>
                ))}
                <Button size="sm" variant="secondary" onClick={() => void saveFares(route)}>Save fares</Button>
              </div>
            )}
          </article>
        ))}
      </div>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>{editing ? "Edit route" : "Create route"}</DialogTitle></DialogHeader>
          <Field label="Code" value={code} onChange={setCode} />
          <Field label="Name" value={name} onChange={setName} />
          <SelectField label="Operator" value={operatorId} onChange={setOperatorId}>
            {operators.data?.map((op) => <option key={op.id} value={op.id}>{op.name}</option>)}
          </SelectField>
          <div className="grid gap-2">
            <Label>Stops in order</Label>
            <ol className="grid gap-1 text-sm">
              {stopIds.map((id, index) => (
                <li key={`${id}-${index}`} className="flex items-center justify-between rounded-md bg-muted px-2 py-1">
                  <span>{index + 1}. {stops.data?.find((s) => s.id === id)?.name}</span>
                  <button type="button" onClick={() => setStopIds(stopIds.filter((_, i) => i !== index))}>Remove</button>
                </li>
              ))}
            </ol>
            <div className="flex gap-2">
              <select className="h-10 flex-1 rounded-md border bg-card px-3 text-sm" value={pick} onChange={(e) => setPick(e.target.value)}>
                <option value="">Add a stop</option>
                {stops.data?.filter((s) => !stopIds.includes(s.id)).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
              <Button type="button" variant="outline" onClick={() => { if (pick) { setStopIds([...stopIds, Number(pick)]); setPick(""); } }}>Add</Button>
            </div>
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
          <Button onClick={() => void save()}>Save route</Button>
        </DialogContent>
      </Dialog>
    </Page>
  );
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

  function edit(row?: Schedule) {
    setEditing(row ?? null);
    setForm({
      routeId: String(row?.routeId ?? routes.data?.[0]?.id ?? ""),
      busId: row?.busId ? String(row.busId) : "",
      driverId: row?.driverId ? String(row.driverId) : "",
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

  const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

  return (
    <Page title="Schedules" subtitle="Attach a bus and a driver to a departure." action={<Button onClick={() => edit()}>Create schedule</Button>} loading={query.loading} error={query.error}>
      <Table headers={["Time", "Route", "Bus", "Driver", ""]}>
        {query.data?.map((row) => (
          <tr key={row.id} className="border-t">
            <td className="px-3 py-2">{formatClock(row.departureTime)}</td>
            <td className="px-3 py-2">{row.routeCode}</td>
            <td className="px-3 py-2">{row.fleetNumber ? `${row.fleetNumber} · ${row.registrationNumber}` : "Unassigned"}</td>
            <td className="px-3 py-2">{row.driverName ? `${row.driverName} · ${row.driverCode}` : "Unassigned"}</td>
            <td className="px-3 py-2 text-right">
              <Button size="sm" variant="ghost" onClick={() => edit(row)}>Edit</Button>
              <Button size="sm" variant="ghost" onClick={async () => { await api(`/api/admin/schedules/${row.id}`, { method: "DELETE" }); await query.reload(); }}>Remove</Button>
            </td>
          </tr>
        ))}
      </Table>
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
    const conn = new HubConnectionBuilder().withUrl("/hubs/tracking").withAutomaticReconnect().build();
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
  return (
    <Page title="Live board" subtitle="Every bus that is currently running." loading={query.loading} error={query.error}>
      {rows.length === 0 && <p className="rounded-xl border bg-card p-4 text-sm">No bus is on the road. Start operations, then have a driver start a trip.</p>}
      <div className="grid gap-4 lg:grid-cols-[1fr_320px]">
        <div className="h-[420px] overflow-hidden rounded-xl border">
          {first && (
            <MapView
              path={first.path}
              stops={first.stops}
              bus={rows.filter((r) => r.latitude != null && r.longitude != null).length ? {
                latitude: first.latitude!,
                longitude: first.longitude!,
                heading: first.heading,
                label: first.fleetNumber,
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
  const query = useQuery<TrackSnapshot[]>("/api/admin/reports/trips");
  return (
    <Page title="Trip report" subtitle="Today's trips, with delay against the 42 km/h timetable." loading={query.loading} error={query.error}>
      <Table headers={["Bus", "Route", "Departs", "Status", "Now", "Delay", "Driver"]}>
        {query.data?.map((row) => (
          <tr key={row.tripId} className="border-t">
            <td className="px-3 py-2">{row.fleetNumber} · {row.registrationNumber}</td>
            <td className="px-3 py-2">{row.routeCode}</td>
            <td className="px-3 py-2">{formatClock(row.departureTime)}</td>
            <td className="px-3 py-2">{row.status}</td>
            <td className="px-3 py-2">{row.currentStop ?? "—"}</td>
            <td className="px-3 py-2">{row.outsideSlot ? "Outside the printed slot" : row.delayMinutes != null && row.delayMinutes > 2 ? `${row.delayMinutes} min late` : "On time"}</td>
            <td className="px-3 py-2">{row.driverName ?? "—"}</td>
          </tr>
        ))}
      </Table>
      {query.data && query.data.length === 0 && <p className="mt-3 text-sm text-muted-foreground">No trips yet. Start operations from the overview.</p>}
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

function Table({ headers, children }: { headers: string[]; children: ReactNode }) {
  return (
    <div className="overflow-x-auto rounded-xl border bg-card">
      <table className="w-full text-left text-sm">
        <thead className="text-xs uppercase tracking-wider text-muted-foreground">
          <tr>{headers.map((header) => <th key={header} className="px-3 py-2 font-medium">{header}</th>)}</tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}
