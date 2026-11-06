// PWA Service Worker & Install Manager

export interface BeforeInstallPromptEvent extends Event {
  readonly platforms: string[];
  readonly userChoice: Promise<{
    outcome: 'accepted' | 'dismissed';
    platform: string;
  }>;
  prompt(): Promise<void>;
}

type PwaStateListener = (state: {
  isInstallable: boolean;
  isInstalled: boolean;
  isOffline: boolean;
  hasUpdate: boolean;
}) => void;

let deferredPrompt: BeforeInstallPromptEvent | null = null;
let waitingWorker: ServiceWorker | null = null;
const listeners = new Set<PwaStateListener>();

let pwaState = {
  isInstallable: false,
  isInstalled: false,
  isOffline: typeof navigator !== 'undefined' ? !navigator.onLine : false,
  hasUpdate: false
};

function notifyListeners() {
  for (const listener of listeners) {
    listener({ ...pwaState });
  }
}

export function subscribePwaState(listener: PwaStateListener) {
  listeners.add(listener);
  listener({ ...pwaState });
  return () => {
    listeners.delete(listener);
  };
}

export function registerServiceWorker() {
  if (typeof window === 'undefined' || !('serviceWorker' in navigator)) {
    return;
  }

  // Check if already running standalone PWA
  const isStandalone = window.matchMedia('(display-mode: standalone)').matches ||
    (navigator as unknown as { standalone?: boolean }).standalone === true;
  pwaState.isInstalled = isStandalone;

  // Listen for install prompt
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferredPrompt = e as BeforeInstallPromptEvent;
    pwaState.isInstallable = true;
    notifyListeners();
  });

  // App installed event
  window.addEventListener('appinstalled', () => {
    deferredPrompt = null;
    pwaState.isInstallable = false;
    pwaState.isInstalled = true;
    notifyListeners();
  });

  // Online / Offline listeners
  window.addEventListener('online', () => {
    pwaState.isOffline = false;
    notifyListeners();
  });

  window.addEventListener('offline', () => {
    pwaState.isOffline = true;
    notifyListeners();
  });

  // Register sw.js on window load
  window.addEventListener('load', () => {
    navigator.serviceWorker
      .register('/sw.js', { scope: '/' })
      .then((registration) => {
        // Check for updates
        registration.addEventListener('updatefound', () => {
          const newWorker = registration.installing;
          if (!newWorker) return;

          newWorker.addEventListener('statechange', () => {
            if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
              waitingWorker = newWorker;
              pwaState.hasUpdate = true;
              notifyListeners();
            }
          });
        });

        // If there's already a waiting worker
        if (registration.waiting) {
          waitingWorker = registration.waiting;
          pwaState.hasUpdate = true;
          notifyListeners();
        }
      })
      .catch((err) => {
        console.warn('PWA: Service Worker registration failed:', err);
      });

    // Listen for controllerchange (page refresh after update)
    let refreshing = false;
    navigator.serviceWorker.addEventListener('controllerchange', () => {
      if (!refreshing) {
        refreshing = true;
        window.location.reload();
      }
    });
  });
}

// Prompt native install dialog
export async function promptInstall(): Promise<boolean> {
  if (!deferredPrompt) return false;
  try {
    await deferredPrompt.prompt();
    const { outcome } = await deferredPrompt.userChoice;
    if (outcome === 'accepted') {
      pwaState.isInstallable = false;
      pwaState.isInstalled = true;
      notifyListeners();
      deferredPrompt = null;
      return true;
    }
  } catch (err) {
    console.error('Install prompt error:', err);
  }
  return false;
}

// Trigger update and reload
export function updateApp() {
  if (waitingWorker) {
    waitingWorker.postMessage({ type: 'SKIP_WAITING' });
  } else {
    window.location.reload();
  }
}

// Request Notification Permission
export async function requestPushPermission(): Promise<NotificationPermission> {
  if (typeof window === 'undefined' || !('Notification' in window)) {
    return 'denied';
  }
  return await Notification.requestPermission();
}
