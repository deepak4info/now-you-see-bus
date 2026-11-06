import React, { useEffect, useState } from "react";
import { Download, RefreshCw, WifiOff, X, Bell } from "lucide-react";
import { subscribePwaState, promptInstall, updateApp, requestPushPermission } from "../lib/pwa";

export function PwaBanner() {
  const [pwaState, setPwaState] = useState({
    isInstallable: false,
    isInstalled: false,
    isOffline: false,
    hasUpdate: false,
  });

  const [dismissedInstall, setDismissedInstall] = useState(false);
  const [notificationStatus, setNotificationStatus] = useState<NotificationPermission | "unsupported">("default");

  useEffect(() => {
    const unsubscribe = subscribePwaState(setPwaState);
    if (typeof window !== "undefined" && "Notification" in window) {
      setNotificationStatus(Notification.permission);
    } else {
      setNotificationStatus("unsupported");
    }
    return unsubscribe;
  }, []);

  const handleInstall = async () => {
    const installed = await promptInstall();
    if (installed) {
      setDismissedInstall(true);
    }
  };

  const handleEnableNotifications = async () => {
    const permission = await requestPushPermission();
    setNotificationStatus(permission);
    if (permission === "granted" && "serviceWorker" in navigator) {
      const reg = await navigator.serviceWorker.ready;
      reg.showNotification("Notifications Enabled! 🔔", {
        body: "You will receive bus arrival and delay alerts.",
        icon: "/icons/icon-192.png",
        badge: "/icons/icon-192.png",
      });
    }
  };

  return (
    <>
      {/* 1. Offline Mode Indicator */}
      {pwaState.isOffline && (
        <div className="sticky top-0 z-50 flex items-center justify-center gap-2 bg-amber-600 px-4 py-2 text-center text-xs font-semibold text-white shadow-md">
          <WifiOff className="h-4 w-4 shrink-0 animate-pulse" />
          <span>You are offline. Showing cached routes, stops, and schedules.</span>
        </div>
      )}

      {/* 2. New Version Available Update Banner */}
      {pwaState.hasUpdate && (
        <div className="fixed bottom-4 left-4 right-4 z-50 mx-auto max-w-md rounded-2xl border border-primary/20 bg-card p-4 shadow-2xl backdrop-blur-md dark:border-primary/40 dark:bg-slate-900/95">
          <div className="flex items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary text-white">
                <RefreshCw className="h-5 w-5 animate-spin" />
              </div>
              <div>
                <p className="text-sm font-bold text-foreground">New Version Available</p>
                <p className="text-xs text-muted-foreground">Update now to get the latest bus routes & features.</p>
              </div>
            </div>
            <button
              onClick={updateApp}
              className="inline-flex shrink-0 items-center gap-1.5 rounded-xl bg-primary px-3.5 py-2 text-xs font-semibold text-white shadow-sm transition hover:bg-primary/90 active:scale-95"
            >
              Update Now
            </button>
          </div>
        </div>
      )}

      {/* 3. Install App Floating Prompt (Mobile & Desktop) */}
      {pwaState.isInstallable && !dismissedInstall && !pwaState.isInstalled && (
        <div className="fixed bottom-4 right-4 z-40 max-w-sm rounded-2xl border border-emerald-500/30 bg-card/95 p-4 shadow-xl backdrop-blur-md transition-all dark:bg-slate-900/95">
          <div className="flex items-start gap-3">
            <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-emerald-700 text-white shadow-sm">
              <Download className="h-5 w-5" />
            </div>
            <div className="flex-1">
              <div className="flex items-center justify-between">
                <p className="text-sm font-bold text-foreground">Install WhereIsMyBus</p>
                <button
                  onClick={() => setDismissedInstall(true)}
                  className="rounded-lg p-1 text-muted-foreground hover:bg-muted hover:text-foreground"
                  aria-label="Dismiss install prompt"
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
              <p className="mt-0.5 text-xs text-muted-foreground">
                Install on your home screen for quick offline access and instant live tracking.
              </p>
              <div className="mt-3 flex items-center gap-2">
                <button
                  onClick={handleInstall}
                  className="inline-flex items-center gap-1.5 rounded-xl bg-emerald-700 px-3.5 py-1.5 text-xs font-semibold text-white shadow-sm transition hover:bg-emerald-800 active:scale-95"
                >
                  <Download className="h-3.5 w-3.5" />
                  Install App
                </button>
                <button
                  onClick={() => setDismissedInstall(true)}
                  className="rounded-xl px-2.5 py-1.5 text-xs font-medium text-muted-foreground hover:bg-muted"
                >
                  Not now
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

// Compact Install Button for Navbar or Footer
export function InstallAppButton({ className = "" }: { className?: string }) {
  const [pwaState, setPwaState] = useState({
    isInstallable: false,
    isInstalled: false,
    isOffline: false,
    hasUpdate: false,
  });

  useEffect(() => {
    return subscribePwaState(setPwaState);
  }, []);

  if (!pwaState.isInstallable || pwaState.isInstalled) {
    return null;
  }

  return (
    <button
      onClick={() => void promptInstall()}
      className={`inline-flex items-center gap-1.5 rounded-full border border-emerald-600/30 bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-800 transition hover:bg-emerald-100 dark:border-emerald-500/30 dark:bg-emerald-950/40 dark:text-emerald-300 ${className}`}
      title="Install WhereIsMyBus on your device"
    >
      <Download className="h-3.5 w-3.5" />
      Install App
    </button>
  );
}
