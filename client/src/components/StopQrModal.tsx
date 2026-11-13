import { useEffect, useState } from "react";
import { createPortal } from "react-dom";
import { Check, Copy, Download, Printer, QrCode, ShieldCheck, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { StopSummary } from "@/lib/types";

export function StopQrModal({
  stop,
  isOpen,
  onClose,
}: {
  stop: StopSummary;
  isOpen: boolean;
  onClose: () => void;
}) {
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!isOpen) return;

    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }

    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    window.addEventListener("keydown", handleKeyDown);

    return () => {
      document.body.style.overflow = prevOverflow;
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [isOpen, onClose]);

  if (!isOpen) return null;

  const stopCode = stop.code || stop.id.toString();
  const origin = typeof window !== "undefined" ? window.location.origin : "";
  const stopUrl = `${origin}/stop/${encodeURIComponent(stopCode)}`;
  const qrImageUrl = `https://api.qrserver.com/v1/create-qr-code/?size=320x320&data=${encodeURIComponent(stopUrl)}&margin=10`;

  async function copyLink() {
    try {
      await navigator.clipboard.writeText(stopUrl);
      setCopied(true);
      setTimeout(() => setCopied(false), 2500);
    } catch {
      // ignore clipboard error
    }
  }

  function handlePrint() {
    window.print();
  }

  const modalContent = (
    <div
      className="fixed inset-0 z-[2000] flex items-center justify-center bg-black/70 p-3 sm:p-5 backdrop-blur-xs print:p-0 print:bg-white print:fixed print:inset-0"
      role="dialog"
      aria-modal="true"
      aria-label="Bus Stop QR Code Poster"
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className="relative flex max-h-[92vh] w-full max-w-lg flex-col rounded-3xl border border-border bg-card shadow-2xl overflow-hidden print:max-h-none print:w-full print:border-none print:shadow-none">
        {/* Header */}
        <div className="flex items-center justify-between border-b px-5 py-4 print:hidden">
          <div className="flex items-center gap-2">
            <span className="flex h-8 w-8 items-center justify-center rounded-xl bg-pine/10 text-pine">
              <QrCode className="h-4 w-4" aria-hidden="true" />
            </span>
            <div>
              <h2 className="font-display text-base font-bold text-ink">Bus Stop QR Poster</h2>
              <p className="text-xs text-muted-foreground">Print or scan to view live arrivals</p>
            </div>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close dialog">
            <X className="h-4 w-4" />
          </Button>
        </div>

        {/* Printable Poster Canvas */}
        <div className="flex-1 overflow-y-auto p-6 text-center print:p-8">
          <div className="mx-auto max-w-sm rounded-2xl border-2 border-dashed border-primary/30 bg-primary/5 p-6 shadow-inner print:border-solid print:border-black print:bg-white print:shadow-none">
            {/* Transit Authority Brand */}
            <div className="flex items-center justify-center gap-2 mb-3">
              <span className="grid h-8 w-8 place-items-center rounded-lg bg-pine text-white text-base font-bold shadow-xs">
                🚌
              </span>
              <span className="font-display font-bold text-lg tracking-tight text-ink">
                Where Is My Bus
              </span>
            </div>

            <p className="text-[11px] font-bold uppercase tracking-widest text-primary">
              Live Bus Stand Timetable & Tracking
            </p>

            {/* Stop Identification */}
            <div className="mt-3 rounded-xl bg-card p-3 border shadow-xs print:border-black">
              <h3 className="font-display text-xl font-bold text-ink leading-snug">
                {stop.name}
              </h3>
              <div className="mt-1 flex items-center justify-center gap-2 text-xs text-muted-foreground">
                <span className="font-semibold text-primary">Stop Code: {stop.code}</span>
                {stop.city && <span>• {stop.city}</span>}
              </div>
            </div>

            {/* QR Code Container */}
            <div className="my-5 flex flex-col items-center justify-center">
              <div className="rounded-2xl border-4 border-card bg-white p-3 shadow-md print:border-2 print:border-black">
                <img
                  src={qrImageUrl}
                  alt={`QR code for ${stop.name}`}
                  className="h-56 w-56 object-contain"
                  loading="eager"
                />
              </div>
              <p className="mt-3 text-xs font-semibold text-ink flex items-center gap-1.5">
                <ShieldCheck className="h-4 w-4 text-emerald-600" />
                Scan with any smartphone camera
              </p>
              <p className="mt-0.5 text-[11px] text-muted-foreground">
                Instantly see upcoming buses, live ETAs & complete routes
              </p>
            </div>

            {/* Direct URL Footnote */}
            <div className="rounded-lg bg-muted/40 p-2 text-[11px] font-mono text-muted-foreground break-all">
              {stopUrl}
            </div>
          </div>
        </div>

        {/* Footer Actions */}
        <div className="border-t bg-muted/30 px-5 py-3 flex flex-wrap items-center justify-between gap-2 print:hidden">
          <Button variant="outline" size="sm" onClick={copyLink} className="gap-1.5 text-xs">
            {copied ? <Check className="h-3.5 w-3.5 text-emerald-600" /> : <Copy className="h-3.5 w-3.5" />}
            {copied ? "Link Copied!" : "Copy Link"}
          </Button>

          <div className="flex items-center gap-2">
            <Button size="sm" onClick={handlePrint} className="gap-1.5 text-xs">
              <Printer className="h-3.5 w-3.5" />
              Print QR Poster
            </Button>
            <Button variant="ghost" size="sm" onClick={onClose} className="text-xs">
              Close
            </Button>
          </div>
        </div>
      </div>
    </div>
  );

  return typeof document !== "undefined" ? createPortal(modalContent, document.body) : null;
}
