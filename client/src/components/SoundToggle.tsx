import { useEffect, useState } from "react";
import { Volume2, VolumeX } from "lucide-react";
import { transitVoice, type VoiceLang } from "@/lib/voice";
import { cn } from "@/lib/utils";

export function SoundToggle({ className }: { className?: string }) {
  const [enabled, setEnabled] = useState(() => transitVoice.isEnabled());
  const [speaking, setSpeaking] = useState(() => transitVoice.speaking());

  useEffect(() => {
    const handleVoiceState = (e: Event) => {
      const customEvent = e as CustomEvent<{ enabled: boolean; lang: VoiceLang; isSpeaking: boolean }>;
      if (customEvent.detail) {
        setEnabled(customEvent.detail.enabled);
        setSpeaking(customEvent.detail.isSpeaking);
      }
    };

    window.addEventListener("bus:voice-state", handleVoiceState);
    return () => window.removeEventListener("bus:voice-state", handleVoiceState);
  }, []);

  const handleToggle = () => {
    const nextState = transitVoice.toggle();
    setEnabled(nextState);
    if (nextState) {
      transitVoice.speak({
        en: "Voice assistance activated. Tracking live transit updates.",
        hi: "आवाज सहायता शुरू हो गई है। लाइव बस ट्रैकिंग जारी है।",
      });
    }
  };

  const label = enabled ? "Voice is on — tap to mute" : "Voice is off — tap to turn on";

  return (
    <button
      type="button"
      onClick={handleToggle}
      aria-label={label}
      title={label}
      aria-pressed={enabled}
      className={cn(
        "relative grid h-10 w-10 shrink-0 place-items-center rounded-full transition-all duration-200 border cursor-pointer",
        enabled
          ? "border-emerald-300 bg-emerald-50 text-emerald-800 shadow-xs hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950 dark:text-emerald-200"
          : "border-border/80 bg-card text-muted-foreground hover:bg-muted hover:text-ink",
        className
      )}
    >
      {enabled ? (
        <div className="relative flex items-center justify-center">
          <Volume2 className={cn("h-4 w-4 transition-transform", speaking && "scale-110 text-emerald-600 animate-pulse")} />
          {speaking && (
            <span className="absolute -top-1 -right-1 flex h-2 w-2">
              <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75" />
              <span className="relative inline-flex rounded-full h-2 w-2 bg-emerald-500" />
            </span>
          )}
        </div>
      ) : (
        <VolumeX className="h-4 w-4" />
      )}
    </button>
  );
}
