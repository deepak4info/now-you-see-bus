// Transit Voice Engine: Multilingual Web Speech API Assistant for Bus Tracking

const VOICE_STORAGE_KEY = "wimb_voice_enabled";
const VOICE_LANG_KEY = "wimb_voice_lang";

export type VoiceLang = "en" | "hi";

class TransitVoiceEngine {
  private enabled: boolean = true;
  private lang: VoiceLang = "en";
  private isSpeaking: boolean = false;

  constructor() {
    if (typeof window !== "undefined") {
      try {
        const stored = localStorage.getItem(VOICE_STORAGE_KEY);
        this.enabled = stored !== "false";
        const storedLang = localStorage.getItem(VOICE_LANG_KEY);
        if (storedLang === "hi" || storedLang === "en") {
          this.lang = storedLang;
        }
      } catch {
        this.enabled = true;
      }
    }
  }

  public isEnabled(): boolean {
    return this.enabled;
  }

  public getLang(): VoiceLang {
    return this.lang;
  }

  public setLang(lang: VoiceLang): void {
    this.lang = lang;
    try {
      localStorage.setItem(VOICE_LANG_KEY, lang);
    } catch {}
    this.dispatchState();
  }

  public toggle(): boolean {
    this.enabled = !this.enabled;
    if (!this.enabled) {
      this.cancel();
    }
    try {
      localStorage.setItem(VOICE_STORAGE_KEY, String(this.enabled));
    } catch {}
    this.dispatchState();
    return this.enabled;
  }

  public cancel(): void {
    if (typeof window !== "undefined" && "speechSynthesis" in window) {
      window.speechSynthesis.cancel();
      this.isSpeaking = false;
      this.dispatchState();
    }
  }

  public speak(text: { en: string; hi?: string } | string, explicitLang?: VoiceLang): void {
    if (!this.enabled || typeof window === "undefined" || !("speechSynthesis" in window)) {
      return;
    }

    const synth = window.speechSynthesis;
    synth.cancel();

    const targetLang = explicitLang || this.lang;
    let message = "";
    let speechCode = "en-IN";

    if (typeof text === "string") {
      message = text;
      speechCode = targetLang === "hi" ? "hi-IN" : "en-IN";
    } else {
      if (targetLang === "hi" && text.hi) {
        message = text.hi;
        speechCode = "hi-IN";
      } else {
        message = text.en;
        speechCode = "en-IN";
      }
    }

    if (!message) return;

    try {
      const utterance = new SpeechSynthesisUtterance(message);
      utterance.rate = 0.95;
      utterance.pitch = 1.0;
      utterance.lang = speechCode;

      // Select natural voice matching locale if available
      const voices = synth.getVoices();
      const matchedVoice = voices.find(
        (v) => v.lang.toLowerCase().startsWith(speechCode.toLowerCase()) || v.lang.includes(targetLang)
      );
      if (matchedVoice) {
        utterance.voice = matchedVoice;
      }

      utterance.onstart = () => {
        this.isSpeaking = true;
        this.dispatchState();
      };

      utterance.onend = () => {
        this.isSpeaking = false;
        this.dispatchState();
      };

      utterance.onerror = () => {
        this.isSpeaking = false;
        this.dispatchState();
      };

      synth.speak(utterance);
    } catch (err) {
      console.warn("Speech synthesis error:", err);
      this.isSpeaking = false;
    }
  }

  public speaking(): boolean {
    return this.isSpeaking;
  }

  private dispatchState(): void {
    if (typeof window !== "undefined") {
      window.dispatchEvent(
        new CustomEvent("bus:voice-state", {
          detail: {
            enabled: this.enabled,
            lang: this.lang,
            isSpeaking: this.isSpeaking,
          },
        })
      );
    }
  }
}

export const transitVoice = new TransitVoiceEngine();
