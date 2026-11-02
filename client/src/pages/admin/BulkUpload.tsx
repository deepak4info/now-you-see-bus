import { useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Badge } from "@/components/ui/badge";
import { api, getToken } from "@/lib/api";
import { cn } from "@/lib/utils";
import {
  AlertCircle,
  AlertTriangle,
  ArrowRight,
  Check,
  CheckCircle2,
  ChevronDown,
  Download,
  FileCheck,
  FileSpreadsheet,
  FileText,
  FileWarning,
  Filter,
  History,
  Info,
  LoaderCircle,
  RefreshCw,
  Search,
  ShieldCheck,
  Trash2,
  Upload,
  X,
} from "lucide-react";

type EntityKind = "operators" | "drivers" | "buses" | "stops";
type ImportKind = EntityKind | "all";
type DeleteEntityKind = EntityKind | "routes" | "schedules";

type BulkRowResult = {
  row: number;
  type: string;
  code: string;
  name: string;
  status: "Ready" | "Reused" | "Warning" | "Error" | "Imported" | "Skipped" | "Failed";
  errors: string[];
  warnings: string[];
  data: Record<string, string>;
};

type BulkCommitResult = {
  imported: number;
  updated?: number;
  reused: number;
  skipped: number;
  failed: number;
  deleted?: number;
  routes: number;
  schedules: number;
  completedAt: string;
};

type BulkImportSession = {
  importId: string;
  fileName: string;
  fileSize: number;
  type: string;
  createdAt: string;
  adminName: string;
  status: string;
  totalRows: number;
  readyCount: number;
  reusedCount: number;
  invalidCount: number;
  warningCount: number;
  rows: BulkRowResult[];
  commitResult?: BulkCommitResult;
};

type HistoryItem = {
  importId: string;
  date: string;
  admin: string;
  details: string;
  status: string;
};

const PAGE_SIZE = 50;

const entitySpecs: Record<EntityKind, { label: string; fields: string[] }> = {
  operators: { label: "Operators", fields: ["name", "code", "contactName", "phone", "email"] },
  drivers: { label: "Drivers", fields: ["fullName", "driverCode", "email", "phone", "password", "licenseNumber", "operatorCode"] },
  stops: { label: "Stops", fields: ["name", "code", "latitude", "longitude", "address", "city"] },
  buses: { label: "Buses", fields: ["fleetNumber", "registrationNumber", "model", "capacity", "busType", "operatorCode", "assignedDriverCode"] },
};

function csvCell(value: string) {
  return `"${(value ?? "").toString().replaceAll('"', '""')}"`;
}

// Client-side fallback template generator if offline
const fallbackTemplates: Record<ImportKind, { headers: string[]; rows: string[][] }> = {
  all: {
    headers: [
      "type", "name", "code", "contactName", "phone", "email",
      "fullName", "driverCode", "password", "licenseNumber",
      "operatorCode", "fleetNumber", "registrationNumber", "model",
      "capacity", "busType", "assignedDriverCode",
      "latitude", "longitude", "address", "city"
    ],
    rows: [
      ["operator", "Haryana Roadways", "HR-ROADS", "Satish Kumar", "9876543210", "satish@hr-roadways.in", "", "", "", "", "", "", "", "", "", "", "", "", "", "Central Bus Station", "Fatehabad"],
      ["driver", "", "", "", "", "", "Rahul Sharma", "DRV-RAHUL", "Driver@12345", "HR-2023-998811", "HR-ROADS", "", "", "", "", "", "", "", "", "", ""],
      ["stop", "Fatehabad Depot", "FB-DEPOT", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "29.5155", "75.4553", "Near Bus Stand, NH9", "Fatehabad"],
      ["bus", "", "", "", "", "", "", "", "", "", "HR-ROADS", "HR39-1020", "HR-39-A-1020", "Tata Starbus Ultra", "40", "Ordinary", "DRV-RAHUL", "", "", "", ""]
    ]
  },
  operators: {
    headers: ["name", "code", "contactName", "phone", "email"],
    rows: [
      ["Haryana Roadways", "HR-ROADS", "Satish Kumar", "9876543210", "satish@hr-roadways.in"],
      ["Hisar Express Lines", "HISAR-EXP", "Rajesh Verma", "9812345678", "contact@hisarexpress.com"]
    ]
  },
  drivers: {
    headers: ["fullName", "driverCode", "email", "phone", "password", "licenseNumber", "operatorCode"],
    rows: [
      ["Rahul Sharma", "DRV-RAHUL", "rahul@hr-roadways.in", "9876543211", "Driver@12345", "HR-2023-998811", "HR-ROADS"],
      ["Amit Singh", "DRV-AMIT", "amit@hr-roadways.in", "9876543212", "Driver@12345", "HR-2022-445566", "HR-ROADS"]
    ]
  },
  stops: {
    headers: ["name", "code", "latitude", "longitude", "address", "city"],
    rows: [
      ["Fatehabad Depot", "FB-DEPOT", "29.5155", "75.4553", "Near Bus Stand, NH9", "Fatehabad"],
      ["Agroha Dham", "AGROHA-01", "29.3524", "75.6148", "Main Gate, Agroha", "Hisar"],
      ["Hisar Junction", "HSR-JUNC", "29.1539", "75.7229", "Railway Road", "Hisar"]
    ]
  },
  buses: {
    headers: ["fleetNumber", "registrationNumber", "model", "capacity", "busType", "operatorCode", "assignedDriverCode"],
    rows: [
      ["HR39-1020", "HR-39-A-1020", "Tata Starbus Ultra", "40", "Ordinary", "HR-ROADS", "DRV-RAHUL"],
      ["HR39-1021", "HR-39-A-1021", "Ashok Leyland Viking", "52", "Semi-Deluxe", "HR-ROADS", "DRV-AMIT"]
    ]
  }
};

export function BulkUploadPage() {
  const [kind, setKind] = useState<ImportKind>("all");
  const [activeTab, setActiveTab] = useState<"upload" | "delete" | "history">("upload");
  const [file, setFile] = useState<File | null>(null);
  const [fileName, setFileName] = useState("");
  const [fileSize, setFileSize] = useState(0);
  const [error, setError] = useState("");
  const [validating, setValidating] = useState(false);
  const [committing, setCommitting] = useState(false);
  const [progress, setProgress] = useState(0);
  const [importSession, setImportSession] = useState<BulkImportSession | null>(null);
  const [commitResult, setCommitResult] = useState<BulkCommitResult | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [previewFilter, setPreviewFilter] = useState<"all" | "ready" | "reused" | "errors" | "warnings">("all");
  const [currentPage, setCurrentPage] = useState(0);
  const [historyList, setHistoryList] = useState<HistoryItem[]>([]);
  const [loadingHistory, setLoadingHistory] = useState(false);
  const [updateExisting, setUpdateExisting] = useState(false);

  // Bulk delete tab state
  const [deleteEntity, setDeleteEntity] = useState<DeleteEntityKind>("stops");
  const [deleteCodesInput, setDeleteCodesInput] = useState("");
  const [deleteConfirmCheck, setDeleteConfirmCheck] = useState(false);
  const [deletingCodes, setDeletingCodes] = useState(false);
  const [deleteResult, setDeleteResult] = useState<{ deleted: number; skipped: number; message: string; reasons?: string[] } | null>(null);
  const [deleteError, setDeleteError] = useState("");

  type SelectableItem = {
    code: string;
    label: string;
    sub?: string;
    detail?: string;
  };

  const [availableItems, setAvailableItems] = useState<SelectableItem[]>([]);
  const [loadingItems, setLoadingItems] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [dropdownSearch, setDropdownSearch] = useState("");
  const dropdownRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setDropdownOpen(false);
      }
    }
    if (dropdownOpen) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => document.removeEventListener("mousedown", handleClickOutside);
    }
  }, [dropdownOpen]);

  useEffect(() => {
    if (activeTab !== "delete") return;
    let cancelled = false;

    async function loadEntityItems() {
      setLoadingItems(true);
      try {
        let items: SelectableItem[] = [];
        if (deleteEntity === "stops") {
          const res = await api<Array<{ id: number; name: string; code: string; city: string | null }>>("/api/admin/stops");
          items = (res || []).map((s) => ({
            code: s.code,
            label: s.name,
            sub: s.code,
            detail: s.city ? `(${s.city})` : "",
          }));
        } else if (deleteEntity === "buses") {
          const res = await api<Array<{ id: number; fleetNumber: string; registrationNumber: string; model: string | null; busType: string }>>("/api/admin/buses");
          items = (res || []).map((b) => ({
            code: b.registrationNumber || b.fleetNumber,
            label: b.registrationNumber,
            sub: `Fleet #${b.fleetNumber}`,
            detail: b.model ? `${b.model} (${b.busType})` : b.busType,
          }));
        } else if (deleteEntity === "drivers") {
          const res = await api<Array<{ id: number; fullName: string; driverCode: string; licenseNumber: string }>>("/api/admin/drivers");
          items = (res || []).map((d) => ({
            code: d.driverCode || d.licenseNumber,
            label: d.fullName,
            sub: d.driverCode || d.licenseNumber,
            detail: `Lic: ${d.licenseNumber}`,
          }));
        } else if (deleteEntity === "operators") {
          const res = await api<Array<{ id: number; name: string; code: string; contactName: string | null }>>("/api/admin/operators");
          items = (res || []).map((o) => ({
            code: o.code,
            label: o.name,
            sub: o.code,
            detail: o.contactName ? `Contact: ${o.contactName}` : "",
          }));
        } else if (deleteEntity === "routes") {
          const res = await api<Array<{ id: number; name: string; code: string; distanceKm: number | null; stops?: Array<any> }>>("/api/admin/routes");
          items = (res || []).map((r) => ({
            code: r.code,
            label: r.name,
            sub: r.code,
            detail: `${r.distanceKm ? `${r.distanceKm} km` : ""} ${r.stops ? `· ${r.stops.length} stops` : ""}`.trim(),
          }));
        } else if (deleteEntity === "schedules") {
          const res = await api<Array<{ id: number; routeCode: string; routeName: string; departureTime: string; registrationNumber: string | null; fleetNumber: string | null }>>("/api/admin/schedules");
          items = (res || []).map((s) => ({
            code: `SCH-${s.id}`,
            label: `${s.routeCode} (${s.departureTime})`,
            sub: `SCH-${s.id}`,
            detail: `${s.routeName} ${s.registrationNumber ? `· Bus: ${s.registrationNumber}` : s.fleetNumber ? `· Fleet #${s.fleetNumber}` : ""}`.trim(),
          }));
        }

        if (!cancelled) {
          setAvailableItems(items);
        }
      } catch (err) {
        console.error("Failed to load records for deletion", err);
      } finally {
        if (!cancelled) setLoadingItems(false);
      }
    }

    void loadEntityItems();
    return () => {
      cancelled = true;
    };
  }, [activeTab, deleteEntity]);

  const currentSelectedCodes = useMemo(() => {
    return Array.from(
      new Set(
        deleteCodesInput
          .split(/[\r\n,;\t]+/)
          .map((c) => c.trim().toUpperCase())
          .filter(Boolean)
      )
    );
  }, [deleteCodesInput]);

  const filteredDropdownItems = useMemo(() => {
    const q = dropdownSearch.trim().toLowerCase();
    if (!q) return availableItems;
    return availableItems.filter(
      (item) =>
        item.code.toLowerCase().includes(q) ||
        item.label.toLowerCase().includes(q) ||
        (item.detail && item.detail.toLowerCase().includes(q)) ||
        (item.sub && item.sub.toLowerCase().includes(q))
    );
  }, [availableItems, dropdownSearch]);

  function toggleItemCode(code: string) {
    const upper = code.trim().toUpperCase();
    const existing = new Set(currentSelectedCodes);
    if (existing.has(upper)) {
      existing.delete(upper);
    } else {
      existing.add(upper);
    }
    setDeleteCodesInput(Array.from(existing).join(", "));
  }

  function selectAllFiltered() {
    const existing = new Set(currentSelectedCodes);
    filteredDropdownItems.forEach((item) => existing.add(item.code.toUpperCase()));
    setDeleteCodesInput(Array.from(existing).join(", "));
  }

  function deselectAll() {
    setDeleteCodesInput("");
  }

  const fileInputRef = useRef<HTMLInputElement>(null);

  async function handleExecuteBulkDeleteCodes() {
    setDeleteError("");
    setDeleteResult(null);
    const rawCodes = deleteCodesInput
      .split(/[\r\n,;\t]+/)
      .map((c) => c.trim())
      .filter((c) => c.length > 0);
    const codes = Array.from(new Set(rawCodes));

    if (codes.length === 0) {
      setDeleteError("Please enter at least one record code or identifier to delete.");
      return;
    }
    if (!deleteConfirmCheck) {
      setDeleteError("Please check the confirmation box before deleting records.");
      return;
    }

    setDeletingCodes(true);
    try {
      const token = getToken();
      const res = await fetch("/api/admin/bulk-upload/delete-records", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        body: JSON.stringify({
          type: deleteEntity,
          codes,
        }),
      });

      if (!res.ok) {
        const errJson = await res.json().catch(() => ({}));
        throw new Error(errJson.message || "Bulk deletion failed.");
      }

      const data = await res.json();
      setDeleteResult(data);
      setDeleteCodesInput("");
      setDeleteConfirmCheck(false);
    } catch (err) {
      setDeleteError(err instanceof Error ? err.message : "Deletion failed.");
    } finally {
      setDeletingCodes(false);
    }
  }

  useEffect(() => {
    if (activeTab === "history") {
      void loadHistory();
    }
  }, [activeTab]);

  async function loadHistory() {
    setLoadingHistory(true);
    try {
      const token = getToken();
      const res = await fetch("/api/admin/bulk-upload/history", {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      if (res.ok) {
        const data = await res.json();
        const active: HistoryItem[] = (data.activeSessions ?? []).map((s: any) => ({
          importId: s.importId,
          date: s.date,
          admin: s.admin || "Admin",
          details: s.details,
          status: s.status,
        }));
        const audit: HistoryItem[] = (data.auditHistory ?? []).map((a: any) => ({
          importId: a.importId,
          date: a.date,
          admin: a.admin,
          details: a.details,
          status: a.status,
        }));
        setHistoryList([...active, ...audit]);
      }
    } catch {
      // Ignore network errors on history
    } finally {
      setLoadingHistory(false);
    }
  }

  // 1. Template Download (GET /api/admin/bulk-upload/templates/{type})
  async function downloadTemplate() {
    setError("");
    try {
      const token = getToken();
      const res = await fetch(`/api/admin/bulk-upload/templates/${kind}`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });

      if (res.ok) {
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = `${kind}-template.csv`;
        link.click();
        URL.revokeObjectURL(url);
        return;
      }
    } catch {
      // Fallback below
    }

    // Client-side fallback with headers & example rows
    const template = fallbackTemplates[kind];
    const lines = [
      template.headers.map(csvCell).join(","),
      ...template.rows.map((row) => row.map(csvCell).join(",")),
    ];
    const csv = lines.join("\r\n") + "\r\n";
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `${kind}-template.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  // 2. Select & Validate CSV (POST /api/admin/bulk-upload/validate)
  async function handleFileSelect(event: ChangeEvent<HTMLInputElement>) {
    const selectedFile = event.target.files?.[0];
    setError("");
    setImportSession(null);
    setCommitResult(null);
    setCurrentPage(0);
    setPreviewFilter("all");

    if (!selectedFile) return;

    // Extension Check
    const ext = selectedFile.name.toLowerCase();
    if (!ext.endsWith(".csv") && !ext.endsWith(".txt")) {
      setError("Please select a .csv file. Excel workbooks (.xlsx) should be saved as CSV UTF-8 before uploading.");
      if (fileInputRef.current) fileInputRef.current.value = "";
      return;
    }

    // Size Check (Max 15 MB)
    if (selectedFile.size > 15 * 1024 * 1024) {
      setError(`File size (${(selectedFile.size / (1024 * 1024)).toFixed(1)} MB) exceeds the 15 MB limit.`);
      if (fileInputRef.current) fileInputRef.current.value = "";
      return;
    }

    setFile(selectedFile);
    setFileName(selectedFile.name);
    setFileSize(selectedFile.size);
    setValidating(true);

    try {
      const formData = new FormData();
      formData.append("file", selectedFile);

      const token = getToken();
      const res = await fetch(`/api/admin/bulk-upload/validate?type=${kind}`, {
        method: "POST",
        headers: token ? { Authorization: `Bearer ${token}` } : {},
        body: formData,
      });

      if (!res.ok) {
        const errorJson = await res.json().catch(() => ({}));
        throw new Error(errorJson.message || "Failed to validate CSV file.");
      }

      const session: BulkImportSession = await res.json();
      setImportSession(session);
      if (session.type !== kind && (session.type === "all" || session.type in entitySpecs)) {
        setKind(session.type as ImportKind);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not validate CSV file.");
    } finally {
      setValidating(false);
      if (fileInputRef.current) fileInputRef.current.value = "";
    }
  }

  // 4 & 5. Confirm and Commit Import (POST /api/admin/bulk-upload/{importId}/commit)
  async function handleCommit() {
    if (!importSession) return;
    setCommitting(true);
    setError("");
    setProgress(15);

    const interval = setInterval(() => {
      setProgress((cur) => (cur < 85 ? cur + 10 : cur));
    }, 250);

    try {
      const token = getToken();
      const res = await fetch(`/api/admin/bulk-upload/${importSession.importId}/commit?updateExisting=${updateExisting}`, {
        method: "POST",
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });

      clearInterval(interval);
      setProgress(100);

      if (!res.ok) {
        const errJson = await res.json().catch(() => ({}));
        throw new Error(errJson.message || "Commit failed.");
      }

      const result: BulkCommitResult = await res.json();
      setCommitResult(result);
      setConfirmOpen(false);

      // Refresh rows status to reflect committed state
      setImportSession((prev) => {
        if (!prev) return null;
        return {
          ...prev,
          status: "Completed",
          commitResult: result,
          rows: prev.rows.map((row) => {
            if (row.errors.length > 0) return { ...row, status: "Skipped" };
            if (row.status === "Reused") return updateExisting ? { ...row, status: "Imported" } : row;
            return { ...row, status: "Imported" };
          }),
        };
      });

      void loadHistory();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Execution failed during commit.");
    } finally {
      clearInterval(interval);
      setCommitting(false);
      setTimeout(() => setProgress(0), 1200);
    }
  }

  // 6. Download Error Report CSV (GET /api/admin/bulk-upload/{importId}/errors)
  async function downloadErrorReport() {
    if (!importSession) return;
    try {
      const token = getToken();
      const res = await fetch(`/api/admin/bulk-upload/${importSession.importId}/errors`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });

      if (res.ok) {
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = `import-errors-${importSession.importId}.csv`;
        link.click();
        URL.revokeObjectURL(url);
        return;
      }
    } catch {
      // Fallback below
    }

    // Client-side fallback generator for error CSV
    const errorRows = importSession.rows.filter((r) => r.errors.length > 0 || r.status === "Error" || r.status === "Skipped");
    if (errorRows.length === 0) return;

    const allKeys = Object.keys(errorRows[0]?.data ?? {});
    const headers = ["CSV Row", "Record Type", "Identifier", "Status", "Errors", "Warnings", ...allKeys];
    const lines = [
      headers.map(csvCell).join(","),
      ...errorRows.map((r) => [
        String(r.row),
        r.type,
        r.code,
        r.status,
        r.errors.join("; "),
        r.warnings.join("; "),
        ...allKeys.map((k) => r.data[k] ?? ""),
      ].map(csvCell).join(",")),
    ];

    const csv = lines.join("\r\n") + "\r\n";
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `import-errors-${importSession.importId}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  // Download Full Audit Log CSV
  function downloadFullAuditLog() {
    if (!importSession) return;
    const allKeys = Object.keys(importSession.rows[0]?.data ?? {});
    const headers = ["CSV Row", "Record Type", "Identifier", "Status", "Validation Messages", ...allKeys];
    const lines = [
      headers.map(csvCell).join(","),
      ...importSession.rows.map((r) => [
        String(r.row),
        r.type,
        r.code,
        r.status,
        [...r.errors, ...r.warnings].join("; ") || "OK",
        ...allKeys.map((k) => (k === "password" ? "[HIDDEN]" : (r.data[k] ?? ""))),
      ].map(csvCell).join(",")),
    ];

    const csv = lines.join("\r\n") + "\r\n";
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `import-audit-log-${importSession.importId}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  // Filtered rows for Step 3 Preview
  const filteredRows = useMemo(() => {
    if (!importSession) return [];
    return importSession.rows.filter((r) => {
      if (previewFilter === "ready") return r.status === "Ready" || r.status === "Imported";
      if (previewFilter === "reused") return r.status === "Reused";
      if (previewFilter === "errors") return r.status === "Error" || r.status === "Failed" || r.errors.length > 0;
      if (previewFilter === "warnings") return r.warnings.length > 0 && r.errors.length === 0 && r.status !== "Reused";
      return true;
    });
  }, [importSession, previewFilter]);

  const totalPages = Math.max(1, Math.ceil(filteredRows.length / PAGE_SIZE));
  const visibleRows = filteredRows.slice(currentPage * PAGE_SIZE, (currentPage + 1) * PAGE_SIZE);

  return (
    <div className="grid w-full min-w-0 max-w-full grid-cols-1">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-display text-4xl">Bulk upload</h1>
          <p className="mt-1 text-muted-foreground">
            Validate records, identify errors, and import data safely without creating duplicate records or corrupting existing data.
          </p>
        </div>
        <div className="inline-flex rounded-lg border bg-card p-1">
          <Button
            size="sm"
            variant={activeTab === "upload" ? "default" : "ghost"}
            onClick={() => setActiveTab("upload")}
          >
            <Upload className="mr-1.5 h-4 w-4" />
            Upload & Import
          </Button>
          <Button
            size="sm"
            variant={activeTab === "delete" ? "default" : "ghost"}
            onClick={() => setActiveTab("delete")}
          >
            <Trash2 className="mr-1.5 h-4 w-4" />
            Bulk Delete
          </Button>
          <Button
            size="sm"
            variant={activeTab === "history" ? "default" : "ghost"}
            onClick={() => setActiveTab("history")}
          >
            <History className="mr-1.5 h-4 w-4" />
            Import History
          </Button>
        </div>
      </div>

      {activeTab === "history" && (
        <section className="mt-6 w-full max-w-5xl rounded-xl border bg-card p-5">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="text-xl font-semibold">Import Audit History</h2>
            <Button size="sm" variant="outline" onClick={() => void loadHistory()} disabled={loadingHistory}>
              <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${loadingHistory ? "animate-spin" : ""}`} />
              Refresh
            </Button>
          </div>
          {historyList.length === 0 ? (
            <p className="text-sm text-muted-foreground">No recent bulk import events recorded.</p>
          ) : (
            <div className="overflow-x-auto rounded-lg border">
              <table className="w-full text-left text-sm">
                <thead className="border-b bg-muted/40 text-xs uppercase text-muted-foreground">
                  <tr>
                    <th className="px-4 py-2.5">Date & Time</th>
                    <th className="px-4 py-2.5">Admin</th>
                    <th className="px-4 py-2.5">Summary / Details</th>
                    <th className="px-4 py-2.5">Status</th>
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {historyList.map((item, idx) => (
                    <tr key={`${item.importId}-${idx}`} className="hover:bg-muted/30">
                      <td className="whitespace-nowrap px-4 py-2.5 font-mono text-xs">
                        {new Date(item.date).toLocaleString()}
                      </td>
                      <td className="px-4 py-2.5">{item.admin}</td>
                      <td className="px-4 py-2.5 text-muted-foreground">{item.details}</td>
                      <td className="px-4 py-2.5">
                        <Badge variant="live" className="text-xs">
                          {item.status}
                        </Badge>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      {activeTab === "delete" && (
        <section className="mt-6 w-full max-w-4xl space-y-5 rounded-xl border bg-card p-6 shadow-sm">
          <div>
            <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wider text-destructive">
              <Trash2 className="h-4 w-4" />
              Bulk Delete by Codes / Identifiers
            </div>
            <h2 className="mt-1 text-2xl font-semibold">Deactivate multiple records in batch</h2>
            <p className="text-sm text-muted-foreground mt-1">
              Specify a list of codes (e.g. stop codes, bus fleet/registration numbers, driver IDs, or operator codes) to deactivate simultaneously. Active dependencies will be checked and safely protected.
            </p>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <label className="grid gap-1.5 text-sm font-medium">
              Entity type to delete
              <select
                className="h-10 rounded-md border bg-card px-3 text-sm font-normal focus:outline-none focus:ring-2 focus:ring-primary"
                value={deleteEntity}
                onChange={(e) => {
                  setDeleteEntity(e.target.value as any);
                  setDeleteCodesInput("");
                  setDropdownSearch("");
                  setDropdownOpen(false);
                  setDeleteResult(null);
                  setDeleteError("");
                }}
                disabled={deletingCodes}
              >
                <option value="stops">Stops (by Stop Code, e.g. HSR-BS-01, FB-DEPOT)</option>
                <option value="buses">Buses (by Fleet or Registration Number, e.g. HR39-1020)</option>
                <option value="drivers">Drivers (by Driver Code or License, e.g. DRV-RAHUL)</option>
                <option value="operators">Operators (by Operator Code, e.g. HR-ROADS)</option>
                <option value="routes">Routes (by Route Code, e.g. 101, ROUTE-11)</option>
                <option value="schedules">Schedules (by Schedule Code / Time, e.g. SCH-13, 101@08:00)</option>
              </select>
            </label>
            <div className="flex flex-col justify-end text-xs text-muted-foreground">
              <span>Enter codes separated by commas, spaces, or newlines. Duplicate codes are automatically deduplicated.</span>
            </div>
          </div>

          {/* Multi-Select Dropdown Picker */}
          <div className="grid gap-1.5" ref={dropdownRef}>
            <div className="flex items-center justify-between text-sm font-medium">
              <label>Select {deleteEntity} from dropdown (Multi-select)</label>
              <span className="text-xs text-muted-foreground">
                {currentSelectedCodes.length} of {availableItems.length} selected
              </span>
            </div>

            <div className="relative">
              <button
                type="button"
                onClick={() => setDropdownOpen((prev) => !prev)}
                disabled={deletingCodes || loadingItems}
                className={cn(
                  "flex min-h-10 w-full items-center justify-between rounded-md border bg-card px-3 py-2 text-sm text-foreground shadow-sm transition-colors hover:bg-muted/30 focus:outline-none focus:ring-2 focus:ring-primary",
                  dropdownOpen && "ring-2 ring-primary border-primary"
                )}
              >
                <div className="flex flex-1 flex-wrap items-center gap-1.5 overflow-hidden text-left">
                  {loadingItems ? (
                    <span className="flex items-center gap-2 text-muted-foreground">
                      <LoaderCircle className="h-4 w-4 animate-spin text-primary" />
                      Loading {deleteEntity}...
                    </span>
                  ) : currentSelectedCodes.length === 0 ? (
                    <span className="text-muted-foreground">
                      Click to choose {deleteEntity} (search & select multiple values)...
                    </span>
                  ) : (
                    <span className="font-medium text-foreground">
                      {currentSelectedCodes.length} {deleteEntity} selected
                    </span>
                  )}
                </div>
                <ChevronDown
                  className={cn(
                    "ml-2 h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-200",
                    dropdownOpen && "rotate-180"
                  )}
                />
              </button>

              {dropdownOpen && (
                <div className="absolute z-50 mt-1.5 w-full rounded-xl border bg-card p-2 shadow-xl ring-1 ring-black/5">
                  {/* Search input inside dropdown */}
                  <div className="relative mb-2">
                    <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                    <input
                      type="text"
                      className="h-9 w-full rounded-md border bg-muted/30 pl-8 pr-7 text-xs placeholder:text-muted-foreground focus:bg-card focus:outline-none focus:ring-1 focus:ring-primary"
                      placeholder={`Search ${deleteEntity} by name or code...`}
                      value={dropdownSearch}
                      onChange={(e) => setDropdownSearch(e.target.value)}
                      autoFocus
                    />
                    {dropdownSearch && (
                      <button
                        type="button"
                        onClick={() => setDropdownSearch("")}
                        className="absolute right-2.5 top-2.5 text-xs text-muted-foreground hover:text-foreground"
                      >
                        ✕
                      </button>
                    )}
                  </div>

                  {/* Actions Header Bar */}
                  <div className="mb-2 flex items-center justify-between border-b pb-2 px-1 text-xs">
                    <span className="font-medium text-muted-foreground">
                      {filteredDropdownItems.length} {deleteEntity} available
                    </span>
                    <div className="flex gap-2">
                      <button
                        type="button"
                        onClick={selectAllFiltered}
                        className="font-medium text-primary hover:underline"
                      >
                        Select all {filteredDropdownItems.length > 0 && `(${filteredDropdownItems.length})`}
                      </button>
                      <span className="text-muted-foreground">|</span>
                      <button
                        type="button"
                        onClick={deselectAll}
                        className="font-medium text-muted-foreground hover:text-destructive hover:underline"
                      >
                        Deselect all
                      </button>
                    </div>
                  </div>

                  {/* List of selectable records */}
                  <div className="max-h-60 overflow-y-auto space-y-0.5 pr-1">
                    {filteredDropdownItems.length === 0 ? (
                      <div className="py-6 text-center text-xs text-muted-foreground">
                        No {deleteEntity} found matching &ldquo;{dropdownSearch}&rdquo;
                      </div>
                    ) : (
                      filteredDropdownItems.map((item) => {
                        const isChecked = currentSelectedCodes.includes(item.code.toUpperCase());
                        return (
                          <div
                            key={item.code}
                            onClick={() => toggleItemCode(item.code)}
                            className={cn(
                              "flex items-center justify-between rounded-lg px-2.5 py-1.5 text-xs cursor-pointer select-none transition-colors",
                              isChecked
                                ? "bg-primary/10 text-primary font-medium"
                                : "hover:bg-muted text-foreground"
                            )}
                          >
                            <div className="flex items-center gap-2.5 min-w-0">
                              <input
                                type="checkbox"
                                checked={isChecked}
                                onChange={() => {}}
                                className="h-3.5 w-3.5 rounded border-gray-300 text-primary focus:ring-primary pointer-events-none"
                              />
                              <span className="font-mono text-[11px] font-semibold tracking-wider rounded bg-muted px-1.5 py-0.5">
                                {item.code}
                              </span>
                              <span className="truncate">{item.label}</span>
                            </div>
                            {item.detail && (
                              <span className="ml-2 shrink-0 text-[11px] text-muted-foreground truncate max-w-[140px]">
                                {item.detail}
                              </span>
                            )}
                          </div>
                        );
                      })
                    )}
                  </div>
                </div>
              )}
            </div>

            {/* Selected Chips */}
            {currentSelectedCodes.length > 0 && (
              <div className="mt-1 flex flex-wrap gap-1.5 max-h-24 overflow-y-auto p-1">
                {currentSelectedCodes.map((code) => {
                  const matched = availableItems.find(
                    (i) => i.code.toUpperCase() === code
                  );
                  return (
                    <span
                      key={code}
                      className="inline-flex items-center gap-1.5 rounded-md border border-primary/20 bg-primary/5 px-2 py-1 font-mono text-[11px] font-medium text-foreground"
                    >
                      <span>{code}</span>
                      {matched && (
                        <span className="font-sans font-normal text-muted-foreground">
                          · {matched.label}
                        </span>
                      )}
                      <button
                        type="button"
                        onClick={() => toggleItemCode(code)}
                        className="text-muted-foreground hover:text-destructive ml-0.5"
                        title="Remove"
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </span>
                  );
                })}
              </div>
            )}
          </div>

          <div className="grid gap-1.5">
            <div className="flex items-center justify-between text-sm font-medium">
              <label htmlFor="delete-codes-area">Codes to delete (preview & manual entry)</label>
              <span className="text-xs text-muted-foreground">
                {currentSelectedCodes.length} identifier(s) detected
              </span>
            </div>
            <textarea
              id="delete-codes-area"
              rows={4}
              className="w-full rounded-md border bg-card p-3 font-mono text-xs focus:outline-none focus:ring-2 focus:ring-primary"
              placeholder={`Selected codes from dropdown will appear here, or paste codes separated by commas/newlines...\nExample:\nHR-ROADS-01\nHR-ROADS-02`}
              value={deleteCodesInput}
              onChange={(e) => setDeleteCodesInput(e.target.value)}
              disabled={deletingCodes}
            />
          </div>

          <div className="flex items-start gap-2 rounded-lg bg-amber-500/10 border border-amber-500/20 p-3 text-xs text-amber-900">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700" />
            <span>
              <strong>Dependency Protection:</strong> Records in active routes or schedules will not be deleted; they will be skipped and listed in the deletion report.
            </span>
          </div>

          <label className="flex items-center gap-2 text-sm cursor-pointer select-none">
            <input
              type="checkbox"
              checked={deleteConfirmCheck}
              onChange={(e) => setDeleteConfirmCheck(e.target.checked)}
              disabled={deletingCodes}
              className="h-4 w-4 rounded border-gray-300 text-destructive focus:ring-destructive cursor-pointer"
            />
            <span className="text-xs sm:text-sm font-medium text-foreground">
              I understand that matching unreferenced records will be deactivated.
            </span>
          </label>

          {deleteError && (
            <div className="flex items-center gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive" role="alert">
              <AlertCircle className="h-4 w-4 shrink-0" />
              <span>{deleteError}</span>
            </div>
          )}

          {deleteResult && (
            <div
              className={`rounded-xl border p-4 space-y-2 ${
                deleteResult.deleted > 0
                  ? "border-emerald-600/30 bg-emerald-600/10 text-emerald-950"
                  : "border-amber-600/30 bg-amber-500/10 text-amber-950"
              }`}
            >
              <div className="flex items-center gap-2 font-semibold">
                {deleteResult.deleted > 0 ? (
                  <CheckCircle2 className="h-5 w-5 text-emerald-700" />
                ) : (
                  <AlertTriangle className="h-5 w-5 text-amber-700" />
                )}
                <span>{deleteResult.message}</span>
              </div>
              <div className="flex gap-4 text-xs font-medium">
                <span className="rounded-md bg-emerald-600/20 px-2 py-1 text-emerald-900">
                  Deleted: {deleteResult.deleted}
                </span>
                <span className="rounded-md bg-amber-600/20 px-2 py-1 text-amber-900">
                  Skipped: {deleteResult.skipped}
                </span>
              </div>
              {deleteResult.reasons && deleteResult.reasons.length > 0 && (
                <div className="mt-2 rounded-lg border border-amber-600/20 bg-amber-500/10 p-2.5 text-xs text-amber-950">
                  <p className="font-semibold">Skipped reasons ({deleteResult.reasons.length}):</p>
                  <ul className="mt-1 list-disc pl-4 space-y-0.5">
                    {deleteResult.reasons.map((r, i) => (
                      <li key={i}>{r}</li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-2">
            <Button
              variant="outline"
              onClick={() => {
                setDeleteCodesInput("");
                setDropdownSearch("");
                setDeleteResult(null);
                setDeleteError("");
              }}
              disabled={deletingCodes}
            >
              Clear
            </Button>
            <Button
              variant="destructive"
              onClick={() => void handleExecuteBulkDeleteCodes()}
              disabled={deletingCodes || !deleteConfirmCheck || !deleteCodesInput.trim()}
            >
              {deletingCodes ? "Processing batch delete..." : "Execute Bulk Delete"}
            </Button>
          </div>
        </section>
      )}

      {activeTab === "upload" && (
        <>
          {/* Step 1 & 2: Template Selection & File Picker Card */}
          <section className="mt-6 grid w-full max-w-4xl gap-4 rounded-xl border bg-card p-5 shadow-sm">
            <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wider text-pine">
              <span className="flex h-5 w-5 items-center justify-center rounded-full bg-pine/15 text-[11px]">1</span>
              Step 1 & 2: Select Data Type & CSV File
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <label className="grid gap-1.5 text-sm font-medium">
                Data type
                <select
                  className="h-10 rounded-md border bg-card px-3 text-sm font-normal focus:outline-none focus:ring-2 focus:ring-primary"
                  value={kind}
                  onChange={(e) => setKind(e.target.value as ImportKind)}
                  disabled={validating || committing}
                >
                  <option value="all">All in one CSV (Operators, Drivers, Stops, Buses)</option>
                  <option value="operators">Operators Only</option>
                  <option value="drivers">Drivers Only</option>
                  <option value="stops">Stops Only</option>
                  <option value="buses">Buses Only</option>
                </select>
              </label>

              <div className="grid gap-1.5">
                <span className="text-sm font-medium">Template with example values</span>
                <Button
                  type="button"
                  variant="outline"
                  className="h-10 justify-start"
                  onClick={() => void downloadTemplate()}
                  disabled={validating || committing}
                >
                  <Download className="mr-2 h-4 w-4 text-muted-foreground" />
                  Download {kind === "all" ? "All-in-One" : entitySpecs[kind]?.label} Template (.csv)
                </Button>
              </div>
            </div>

            <div className="rounded-lg border border-dashed bg-muted/20 p-5 text-center">
              <FileSpreadsheet className="mx-auto h-8 w-8 text-muted-foreground" />
              <div className="mt-2 text-sm font-medium">Choose a CSV file to validate and import</div>
              <p className="mt-1 text-xs text-muted-foreground">
                Only .csv files up to 15 MB are supported. Encoding (UTF-8, UTF-16) and delimiters are handled automatically.
              </p>

              <div className="mt-4 flex flex-wrap items-center justify-center gap-3">
                <label className={`cursor-pointer ${buttonVariants({ variant: "default" })}${validating || committing ? " pointer-events-none opacity-50" : ""}`}>
                  <Upload className="mr-2 h-4 w-4" />
                  Select CSV file
                  <input
                    ref={fileInputRef}
                    className="sr-only"
                    type="file"
                    accept=".csv,text/csv,text/plain"
                    onChange={(e) => void handleFileSelect(e)}
                    disabled={validating || committing}
                  />
                </label>
                {fileName && (
                  <span className="inline-flex items-center gap-1.5 rounded-md bg-muted px-3 py-1.5 text-xs font-medium">
                    <FileCheck className="h-3.5 w-3.5 text-primary" />
                    {fileName} ({(fileSize / 1024).toFixed(1)} KB)
                  </span>
                )}
              </div>
            </div>

            <div className="flex items-start gap-2 rounded-lg bg-muted/40 p-3 text-xs text-muted-foreground">
              <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-pine" />
              <span>
                <strong>Safe Import Policy:</strong> Forward and backward dependencies (e.g. buses referring to operators or drivers) are resolved across the file and database. Existing records are safely preserved without duplication or silent overwrites.
              </span>
            </div>

            <div className="flex items-start gap-3 rounded-lg border bg-card p-3 shadow-xs">
              <input
                type="checkbox"
                id="upsert-toggle"
                checked={updateExisting}
                onChange={(e) => setUpdateExisting(e.target.checked)}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-primary focus:ring-primary cursor-pointer"
              />
              <label htmlFor="upsert-toggle" className="text-xs sm:text-sm cursor-pointer select-none">
                <span className="font-semibold text-foreground">Bulk Update Mode (Upsert)</span>
                <p className="text-muted-foreground text-xs mt-0.5">
                  When enabled, existing records matching by code will be updated with values from this CSV instead of preserving the old data.
                </p>
              </label>
            </div>

            {error && (
              <div className="flex items-center gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive" role="alert">
                <AlertCircle className="h-4 w-4 shrink-0" />
                <span>{error}</span>
              </div>
            )}

            {validating && (
              <div className="flex items-center gap-2 text-sm text-muted-foreground" role="status">
                <LoaderCircle className="h-4 w-4 animate-spin text-primary" />
                Reading, validating headers, checking business rules & dependencies…
              </div>
            )}
          </section>

          {/* Step 3: Validation KPI Summary & Filters */}
          {importSession && (
            <section className="mt-6 w-full max-w-5xl space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wider text-pine">
                    <span className="flex h-5 w-5 items-center justify-center rounded-full bg-pine/15 text-[11px]">3</span>
                    Validation Results & Preview
                  </div>
                  <h2 className="mt-1 text-2xl font-semibold">
                    {importSession.fileName} · {importSession.totalRows} rows
                  </h2>
                </div>

                <div className="flex flex-wrap items-center gap-2">
                  {importSession.invalidCount > 0 && (
                    <Button variant="outline" size="sm" onClick={() => void downloadErrorReport()}>
                      <Download className="mr-1.5 h-3.5 w-3.5 text-destructive" />
                      Download Error CSV ({importSession.invalidCount})
                    </Button>
                  )}
                  {commitResult ? (
                    <Button variant="outline" size="sm" onClick={() => downloadFullAuditLog()}>
                      <Download className="mr-1.5 h-3.5 w-3.5" />
                      Download Audit Log
                    </Button>
                  ) : (
                    <Button
                      size="sm"
                      onClick={() => setConfirmOpen(true)}
                      disabled={committing || (importSession.readyCount === 0 && importSession.reusedCount === 0)}
                    >
                      {committing ? (
                        <>
                          <LoaderCircle className="mr-1.5 h-4 w-4 animate-spin" />
                          Importing… {progress}%
                        </>
                      ) : (
                        <>
                          <CheckCircle2 className="mr-1.5 h-4 w-4" />
                          Confirm & Import ({importSession.readyCount} new)
                        </>
                      )}
                    </Button>
                  )}
                </div>
              </div>

              {/* KPI Cards */}
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
                <div className="rounded-xl border bg-card p-3 shadow-sm">
                  <div className="text-xs uppercase text-muted-foreground">Total Rows</div>
                  <div className="font-display text-3xl">{importSession.totalRows}</div>
                </div>
                <div className="rounded-xl border border-emerald-600/30 bg-emerald-600/5 p-3 shadow-sm">
                  <div className="text-xs uppercase text-emerald-800">Ready to Import</div>
                  <div className="font-display text-3xl text-emerald-700">{importSession.readyCount}</div>
                </div>
                <div className="rounded-xl border bg-card p-3 shadow-sm">
                  <div className="text-xs uppercase text-muted-foreground">Already on File</div>
                  <div className="font-display text-3xl text-muted-foreground">{importSession.reusedCount}</div>
                </div>
                <div className="rounded-xl border border-amber-600/30 bg-amber-600/5 p-3 shadow-sm">
                  <div className="text-xs uppercase text-amber-800">Warnings</div>
                  <div className="font-display text-3xl text-amber-800">{importSession.warningCount}</div>
                </div>
                <div className="rounded-xl border border-destructive/30 bg-destructive/5 p-3 shadow-sm">
                  <div className="text-xs uppercase text-destructive">Errors (Skipped)</div>
                  <div className="font-display text-3xl text-destructive">{importSession.invalidCount}</div>
                </div>
              </div>

              {/* Final Report Banner if committed */}
              {commitResult && (
                <div className="rounded-xl border border-emerald-600/30 bg-emerald-600/10 p-4 text-emerald-900 shadow-sm">
                  <div className="flex items-center gap-2 font-semibold">
                    <CheckCircle2 className="h-5 w-5 text-emerald-700" />
                    Import Execution Complete
                  </div>
                  <p className="mt-1 text-sm">
                    Successfully imported <strong>{commitResult.imported}</strong> new record(s)
                    {(commitResult.updated ?? 0) > 0 ? <>, updated <strong>{commitResult.updated}</strong> existing record(s)</> : null}
                    , preserved <strong>{commitResult.reused}</strong> existing record(s), and skipped <strong>{commitResult.skipped}</strong> invalid row(s).
                  </p>
                </div>
              )}

              {/* Progress bar during commit */}
              {committing && (
                <div className="rounded-xl border bg-card p-4 space-y-2">
                  <div className="flex justify-between text-sm font-medium">
                    <span className="flex items-center gap-2">
                      <LoaderCircle className="h-4 w-4 animate-spin text-primary" />
                      Executing phased dependency import (Operators → Drivers → Stops → Buses)…
                    </span>
                    <span>{progress}%</span>
                  </div>
                  <div className="h-2 w-full overflow-hidden rounded-full bg-muted">
                    <div className="h-full bg-primary transition-[width] duration-300" style={{ width: `${progress}%` }} />
                  </div>
                </div>
              )}

              {/* Filter Tabs */}
              <div className="flex flex-wrap items-center justify-between gap-2 border-b pb-2">
                <div className="flex flex-wrap gap-1">
                  <Button
                    size="sm"
                    variant={previewFilter === "all" ? "default" : "outline"}
                    onClick={() => { setPreviewFilter("all"); setCurrentPage(0); }}
                  >
                    All ({importSession.totalRows})
                  </Button>
                  <Button
                    size="sm"
                    variant={previewFilter === "ready" ? "default" : "outline"}
                    onClick={() => { setPreviewFilter("ready"); setCurrentPage(0); }}
                  >
                    Ready ({importSession.readyCount})
                  </Button>
                  <Button
                    size="sm"
                    variant={previewFilter === "reused" ? "default" : "outline"}
                    onClick={() => { setPreviewFilter("reused"); setCurrentPage(0); }}
                  >
                    Already on File ({importSession.reusedCount})
                  </Button>
                  {importSession.invalidCount > 0 && (
                    <Button
                      size="sm"
                      variant={previewFilter === "errors" ? "destructive" : "outline"}
                      onClick={() => { setPreviewFilter("errors"); setCurrentPage(0); }}
                    >
                      Errors ({importSession.invalidCount})
                    </Button>
                  )}
                  {importSession.warningCount > 0 && (
                    <Button
                      size="sm"
                      variant={previewFilter === "warnings" ? "secondary" : "outline"}
                      onClick={() => { setPreviewFilter("warnings"); setCurrentPage(0); }}
                    >
                      Warnings ({importSession.warningCount})
                    </Button>
                  )}
                </div>

                <span className="text-xs text-muted-foreground">
                  Showing {filteredRows.length ? currentPage * PAGE_SIZE + 1 : 0}–{Math.min((currentPage + 1) * PAGE_SIZE, filteredRows.length)} of {filteredRows.length}
                </span>
              </div>

              {/* Preview Table with Serial Number */}
              <div className="overflow-x-auto rounded-xl border bg-card shadow-sm">
                <table className="w-full text-left text-sm">
                  <thead className="border-b bg-muted/40 text-xs uppercase text-muted-foreground">
                    <tr>
                      <th className="w-12 px-3 py-2.5 text-center">#</th>
                      <th className="w-20 px-3 py-2.5">CSV Row</th>
                      <th className="w-24 px-3 py-2.5">Type</th>
                      <th className="w-40 px-3 py-2.5">Identifier</th>
                      <th className="w-48 px-3 py-2.5">Name / Details</th>
                      <th className="w-28 px-3 py-2.5">Status</th>
                      <th className="px-4 py-2.5">Validation Details</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {visibleRows.length === 0 ? (
                      <tr>
                        <td colSpan={7} className="px-4 py-8 text-center text-muted-foreground">
                          No rows match this filter.
                        </td>
                      </tr>
                    ) : (
                      visibleRows.map((item, idx) => {
                        const serialNum = currentPage * PAGE_SIZE + idx + 1;
                        const isError = item.status === "Error" || item.status === "Failed" || item.errors.length > 0;
                        const isReused = item.status === "Reused";
                        const isImported = item.status === "Imported";
                        const isWarning = item.warnings.length > 0 && !isError && !isReused;

                        return (
                          <tr key={`${item.row}-${idx}`} className="hover:bg-muted/30 transition-colors">
                            <td className="px-3 py-2.5 text-center text-xs text-muted-foreground whitespace-nowrap">
                              {serialNum}
                            </td>
                            <td className="px-3 py-2.5 font-mono text-xs text-muted-foreground">
                              Row {item.row}
                            </td>
                            <td className="px-3 py-2.5 uppercase text-xs font-semibold text-muted-foreground">
                              {item.type}
                            </td>
                            <td className="px-3 py-2.5 font-mono text-xs font-medium whitespace-nowrap">
                              {item.code || "—"}
                            </td>
                            <td className="max-w-xs truncate px-3 py-2.5 font-medium">
                              {item.name || Object.values(item.data)[0] || "—"}
                            </td>
                            <td className="px-3 py-2.5 whitespace-nowrap">
                              {isImported && <Badge variant="live">Imported</Badge>}
                              {item.status === "Ready" && <Badge variant="live">Ready</Badge>}
                              {isReused && <Badge variant="outline">On File</Badge>}
                              {isError && <Badge variant="default" className="bg-destructive text-destructive-foreground">Invalid</Badge>}
                              {isWarning && <Badge variant="wait">Warning</Badge>}
                            </td>
                            <td className="px-4 py-2.5 text-xs">
                              {item.errors.length > 0 ? (
                                <div className="flex items-center gap-1.5 text-destructive font-medium">
                                  <AlertCircle className="h-3.5 w-3.5 shrink-0" />
                                  <span>{item.errors.join("; ")}</span>
                                </div>
                              ) : item.warnings.length > 0 ? (
                                <div className="flex items-center gap-1.5 text-amber-800">
                                  <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
                                  <span>{item.warnings.join("; ")}</span>
                                </div>
                              ) : isReused ? (
                                <span className="text-muted-foreground">Existing record safely preserved.</span>
                              ) : (
                                <span className="text-emerald-700">Valid & ready for import.</span>
                              )}
                            </td>
                          </tr>
                        );
                      })
                    )}
                  </tbody>
                </table>
              </div>

              {/* Pagination */}
              {totalPages > 1 && (
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">
                    Page {currentPage + 1} of {totalPages}
                  </span>
                  <div className="flex gap-2">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => setCurrentPage((p) => Math.max(0, p - 1))}
                      disabled={currentPage === 0}
                    >
                      Previous
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => setCurrentPage((p) => Math.min(totalPages - 1, p + 1))}
                      disabled={currentPage >= totalPages - 1}
                    >
                      Next
                    </Button>
                  </div>
                </div>
              )}
            </section>
          )}

          {/* Step 4: Confirmation Dialog */}
          <Dialog open={confirmOpen} onOpenChange={setConfirmOpen}>
            <DialogContent className="max-w-md">
              <DialogHeader>
                <DialogTitle>Confirm Safe Bulk Import</DialogTitle>
              </DialogHeader>
              <div className="space-y-3 text-sm">
                <p className="text-muted-foreground">
                  Please review the record counts before committing changes to the database:
                </p>

                <div className="rounded-lg border bg-muted/40 p-3.5 space-y-2">
                  <div className="flex justify-between font-medium">
                    <span className="text-emerald-700">New records to create:</span>
                    <span className="font-bold text-emerald-800">{importSession?.readyCount ?? 0}</span>
                  </div>
                  <div className="flex justify-between text-muted-foreground">
                    <span>Existing records {updateExisting ? "to update (upsert):" : "protected (safe reuse):"}</span>
                    <span className={updateExisting ? "font-bold text-amber-800" : ""}>{importSession?.reusedCount ?? 0}</span>
                  </div>
                  {(importSession?.invalidCount ?? 0) > 0 && (
                    <div className="flex justify-between text-destructive">
                      <span>Invalid rows that will be skipped:</span>
                      <span className="font-bold">{importSession?.invalidCount ?? 0}</span>
                    </div>
                  )}
                </div>

                <div className="rounded-lg bg-amber-600/10 p-3 text-xs text-amber-900 flex items-start gap-2">
                  <Info className="mt-0.5 h-4 w-4 shrink-0 text-amber-800" />
                  <span>
                    <strong>Dependency Policy:</strong> Operators will be created first, followed by Drivers, Stops, and Buses. Any rows that failed validation will be skipped cleanly without corrupting valid data.
                  </span>
                </div>
              </div>

              <div className="flex justify-end gap-2 pt-3">
                <Button variant="outline" onClick={() => setConfirmOpen(false)}>
                  Cancel
                </Button>
                <Button onClick={() => void handleCommit()}>
                  Proceed with Import
                </Button>
              </div>
            </DialogContent>
          </Dialog>
        </>
      )}
    </div>
  );
}
