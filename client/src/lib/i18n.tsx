import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

export type AppLanguage = "en" | "hi";

const STORAGE_KEY = "wimb_lang";

const translations = {
  en: {
    brand: "Where is my bus",
    navFindBus: "Find a bus",
    navAiAssistant: "AI Assistant ✨",
    navLiveRoutes: "Live routes",
    navEnquiry: "Bus stand numbers",
    navFavorites: "Favourites",
    navAlerts: "Alerts",
    driverConsole: "Driver console",
    dispatchConsole: "Dispatch console",
    signIn: "Sign in",
    createAccount: "Create account",
    signOut: "Sign out",
    visitorsTitle: "Website visitors",
    visitorsCounting: "Counting visitors…",
    visitorsUnavailable: "Count unavailable",
    visitorsHint: "One count per browser",
    langToggleLabel: "हिन्दी",
    busSpeed: "Bus speed",
    lastGpsUpdate: "Last GPS update",
    delayNotifications: "Delay notifications",
    stopArrivalReminder: "Stop arrival reminder",
    shareWithFamily: "Share live journey with family",
    reportWrongInfo: "Report incorrect bus information",
    favoritesTitle: "Favourites",
    favoritesSubtitle: "Save favorite routes, bus stops, and buses for instant access and alerts.",
    saveRouteTab: "Save route",
    saveStopTab: "Save bus stop",
    alertsTitle: "Alerts",
    alertsSubtitle: "Delay alerts, stop arrival reminders, start and completion updates.",
    markAllRead: "Mark all read",
    searchHeroKicker: "Live State & Regional Bus Tracking",
    searchFromLabel: "From",
    searchToLabel: "To",
    searchDateLabel: "Travel date",
    findBusesBtn: "Find buses",
    navCommunity: "Community",
    communityTitle: "Community Bus Updates",
    communitySubtitle: "Report delays, cancellations, updated timings, or missing routes. Verified by admin before publishing.",
    multiRouteTitle: "Multi-Route Journey Planner",
    directAvailableBadge: "Direct bus available",
    connectingOnlyBadge: "No direct bus · Connecting routes found",
  },
  hi: {
    brand: "मेरी बस कहाँ है",
    navFindBus: "बस खोजें",
    navAiAssistant: "AI सहायक ✨",
    navLiveRoutes: "लाइव रूट",
    navEnquiry: "बस स्टैंड नंबर",
    navFavorites: "पसंदीदा",
    navAlerts: "अलर्ट",
    driverConsole: "ड्राइवर कंसोल",
    dispatchConsole: "डिस्पैच कंसोल",
    signIn: "साइन इन",
    createAccount: "खाता बनाएं",
    signOut: "साइन आउट",
    visitorsTitle: "वेबसाइट विज़िटर",
    visitorsCounting: "गिनती हो रही है…",
    visitorsUnavailable: "गिनती उपलब्ध नहीं",
    visitorsHint: "प्रति ब्राउज़र एक गिनती",
    langToggleLabel: "English",
    busSpeed: "बस की गति",
    lastGpsUpdate: "अंतिम GPS अपडेट",
    delayNotifications: "देरी की सूचनाएं",
    stopArrivalReminder: "स्टॉप आगमन रिमाइंडर",
    shareWithFamily: "परिवार के साथ लाइव यात्रा साझा करें",
    reportWrongInfo: "गलत बस जानकारी की रिपोर्ट करें",
    favoritesTitle: "पसंदीदा",
    favoritesSubtitle: "तुरंत देखने और अलर्ट पाने के लिए पसंदीदा रूट, बस स्टॉप और बसें सेव करें।",
    saveRouteTab: "रूट सेव करें",
    saveStopTab: "बस स्टॉप सेव करें",
    alertsTitle: "अलर्ट और सूचनाएं",
    alertsSubtitle: "देरी के अलर्ट, स्टॉप आगमन रिमाइंडर, और यात्रा शुरू/समाप्ति सूचनाएं।",
    markAllRead: "सभी पढ़ा हुआ मानें",
    searchHeroKicker: "लाइव बस लोकेशन और समय ट्रैकिंग",
    searchFromLabel: "कहाँ से",
    searchToLabel: "कहाँ तक",
    searchDateLabel: "यात्रा की तारीख",
    findBusesBtn: "बसें खोजें",
    navCommunity: "समुदाय अपडेट",
    communityTitle: "सामुदायिक बस अपडेट",
    communitySubtitle: "देरी, रद्द बसें, बदला समय या नए रूट की जानकारी दें। एडमिन जांच के बाद प्रकाशित होगा।",
    multiRouteTitle: "मल्टी-रूट यात्रा योजनाकार",
    directAvailableBadge: "सीधी बस उपलब्ध है",
    connectingOnlyBadge: "सीधी बस नहीं · कनेक्टिंग रूट उपलब्ध हैं",
  },
} as const;

export type TranslationKey = keyof typeof translations.en;

type LanguageContextValue = {
  lang: AppLanguage;
  setLang: (lang: AppLanguage) => void;
  toggleLang: () => void;
  t: (key: TranslationKey) => string;
};

const LanguageContext = createContext<LanguageContextValue>({
  lang: "en",
  setLang: () => {},
  toggleLang: () => {},
  t: (key) => translations.en[key],
});

function readStoredLanguage(): AppLanguage {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw === "hi" ? "hi" : "en";
  } catch {
    return "en";
  }
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<AppLanguage>(readStoredLanguage);

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, lang);
    } catch {}
    if (typeof document !== "undefined") {
      document.documentElement.lang = lang;
    }
  }, [lang]);

  const value: LanguageContextValue = {
    lang,
    setLang: setLangState,
    toggleLang: () => setLangState((prev) => (prev === "en" ? "hi" : "en")),
    t: (key) => translations[lang][key] ?? translations.en[key],
  };

  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>;
}

export function useI18n() {
  return useContext(LanguageContext);
}
