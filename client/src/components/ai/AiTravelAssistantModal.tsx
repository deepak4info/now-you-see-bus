import { useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  AlertTriangle,
  ArrowRight,
  Bot,
  Bus,
  CheckCircle2,
  Clock,
  Compass,
  CornerDownLeft,
  ExternalLink,
  Globe,
  HelpCircle,
  MapPin,
  Mic,
  MicOff,
  Navigation,
  Radio,
  RotateCcw,
  Send,
  Sparkles,
  Volume2,
  VolumeX,
  X,
  Zap,
} from "lucide-react";
import { api } from "@/lib/api";
import { Button } from "@/components/ui/button";
import type {
  AiBusCard,
  AiChatMessage,
  AiChatResponse,
  AiLiveStatus,
  JourneyOption,
  StopArrival,
  StopTimetableResponse,
} from "@/lib/types";

// Speech Recognition Type Shim for TypeScript
interface IWindowSpeechRecognition extends Window {
  SpeechRecognition?: any;
  webkitSpeechRecognition?: any;
}

const DEFAULT_SUGGESTIONS_HI = [
  "भुना से दिल्ली (सुबह 8 बजे के बाद)",
  "हिसार से फतेहाबाद की अगली बस",
  "फतेहाबाद बस स्टैंड लाइव टाइमटेबल",
  "Demo-22-R-2202 का लाइव स्टेटस",
  "भुना से दिल्ली कनेक्टिंग बस",
  "हेल्पलाइन नंबर",
];

const DEFAULT_SUGGESTIONS_EN = [
  "Buses from Bhuna to Delhi after 8:00 AM",
  "Hisar to Fatehabad next bus",
  "Fatehabad Bus Stand live board",
  "Live status of bus Demo-22-R-2202",
  "Connecting: Bhuna to Delhi via Hisar",
  "Bus stand enquiry numbers",
];

export function AiTravelAssistantModal({
  isOpen,
  onClose,
  initialQuery,
}: {
  isOpen: boolean;
  onClose: () => void;
  initialQuery?: string;
}) {
  const [lang, setLang] = useState<"hi" | "en">("hi");
  const [messages, setMessages] = useState<
    (AiChatMessage & {
      buses?: AiBusCard[];
      journeyPlan?: any;
      liveStatus?: AiLiveStatus;
      timetable?: StopTimetableResponse;
      adminSummary?: any;
      quickReplies?: string[];
    })[]
  >([]);
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(false);
  const [isListening, setIsListening] = useState(false);
  const [isSpeaking, setIsSpeaking] = useState<string | null>(null);
  const [speechSupported, setSpeechSupported] = useState(false);

  const recognitionRef = useRef<any>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const messagesRef = useRef(messages);

  useEffect(() => {
    messagesRef.current = messages;
  }, [messages]);

  // Initialize Speech Recognition
  useEffect(() => {
    const win = window as IWindowSpeechRecognition;
    const SpeechRecognition = win.SpeechRecognition || win.webkitSpeechRecognition;
    if (SpeechRecognition) {
      setSpeechSupported(true);
      const rec = new SpeechRecognition();
      rec.continuous = false;
      rec.interimResults = true;

      rec.onstart = () => setIsListening(true);
      rec.onend = () => setIsListening(false);
      rec.onerror = () => setIsListening(false);

      rec.onresult = (e: any) => {
        const transcript = Array.from(e.results)
          .map((r: any) => r[0].transcript)
          .join("");
        setInput(transcript);
        if (e.results[0].isFinal && transcript.trim().length > 2) {
          handleSend(transcript);
        }
      };

      recognitionRef.current = rec;
    }
  }, []);

  // Update speech recognition language when lang changes
  useEffect(() => {
    if (recognitionRef.current) {
      recognitionRef.current.lang = lang === "hi" ? "hi-IN" : "en-IN";
    }
  }, [lang]);

  // Initial welcome message
  useEffect(() => {
    if (messages.length === 0) {
      setMessages([
        {
          role: "assistant",
          content:
            lang === "hi"
              ? "नमस्ते! 😊 मैं आपका **AI बस यात्रा सहायक** हूँ।\n\nआप मुझसे किसी भी बस का समय, लाइव लोकेशन, कनेक्टिंग रूट या बस स्टैंड की जानकारी पूछ सकते हैं। आप हिंदी या अंग्रेजी में लिख सकते हैं या माइक दबाकर बोल सकते हैं!"
              : "Hello! 👋 I am your **AI Bus Travel Assistant**.\n\nAsk me about upcoming buses, live GPS tracking, connecting routes, or bus stand departure boards in English or Hindi. You can type or tap the mic to speak!",
          quickReplies: lang === "hi" ? DEFAULT_SUGGESTIONS_HI.slice(0, 4) : DEFAULT_SUGGESTIONS_EN.slice(0, 4),
        },
      ]);
    }
  }, [lang]);

  // Scroll to bottom on new message
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, loading]);

  // Focus input when opened
  useEffect(() => {
    if (isOpen) {
      setTimeout(() => inputRef.current?.focus(), 150);
      if (initialQuery && initialQuery.trim().length > 0) {
        handleSend(initialQuery);
      }
    } else {
      stopSpeaking();
      stopListening();
    }
  }, [isOpen]);

  const toggleListening = () => {
    if (!speechSupported) {
      alert(lang === "hi" ? "आपके ब्राउज़र में वॉइस इनपुट समर्थित नहीं है।" : "Voice input is not supported in this browser.");
      return;
    }

    if (isListening) {
      recognitionRef.current?.stop();
      setIsListening(false);
    } else {
      try {
        recognitionRef.current?.start();
      } catch {
        recognitionRef.current?.stop();
        setTimeout(() => recognitionRef.current?.start(), 100);
      }
    }
  };

  const stopListening = () => {
    if (isListening) {
      recognitionRef.current?.stop();
      setIsListening(false);
    }
  };

  const handleSpeak = (text: string, msgId: string) => {
    if (!("speechSynthesis" in window)) return;

    if (isSpeaking === msgId) {
      window.speechSynthesis.cancel();
      setIsSpeaking(null);
      return;
    }

    window.speechSynthesis.cancel();
    // Clean markdown asterisks and symbols for cleaner TTS audio
    const cleanText = text.replace(/[*#_~`]/g, "").replace(/https?:\/\/\S+/g, "");
    const utterance = new SpeechSynthesisUtterance(cleanText);
    utterance.lang = lang === "hi" ? "hi-IN" : "en-IN";
    utterance.rate = 0.95;

    utterance.onend = () => setIsSpeaking(null);
    utterance.onerror = () => setIsSpeaking(null);

    setIsSpeaking(msgId);
    window.speechSynthesis.speak(utterance);
  };

  const stopSpeaking = () => {
    if ("speechSynthesis" in window) {
      window.speechSynthesis.cancel();
    }
    setIsSpeaking(null);
  };

  const handleSend = async (textToSend?: string) => {
    const query = (textToSend || input).trim();
    if (!query || loading) return;

    stopListening();
    setInput("");

    // Add user message to UI
    const userMsg: AiChatMessage = { role: "user", content: query };
    setMessages((prev) => [...prev, userMsg]);
    setLoading(true);

    try {
      const historyPayload = messagesRef.current.slice(-6).map((m) => ({
        role: m.role,
        content: m.content,
      }));

      const res = await api<AiChatResponse>("/api/ai/chat", {
        method: "POST",
        body: JSON.stringify({
          message: query,
          history: historyPayload,
          language: lang,
        }),
      });

      if (res && res.reply) {
        setMessages((prev) => [
          ...prev,
          {
            role: "assistant",
            content: res.reply,
            buses: res.buses || undefined,
            journeyPlan: res.journeyPlan || undefined,
            liveStatus: res.liveStatus || undefined,
            timetable: res.timetable || undefined,
            adminSummary: res.adminSummary || undefined,
            quickReplies: res.quickReplies || undefined,
          },
        ]);
      } else {
        throw new Error("No response");
      }
    } catch {
      setMessages((prev) => [
        ...prev,
        {
          role: "assistant",
          content:
            lang === "hi"
              ? "क्षमा करें, बस जानकारी प्राप्त करने में असमर्थ। कृपया पुनः प्रयास करें।"
              : "Sorry, I could not fetch bus information at this moment. Please try again.",
          quickReplies: lang === "hi" ? DEFAULT_SUGGESTIONS_HI.slice(0, 3) : DEFAULT_SUGGESTIONS_EN.slice(0, 3),
        },
      ]);
    } finally {
      setLoading(false);
    }
  };

  const handleClear = () => {
    stopSpeaking();
    setMessages([
      {
        role: "assistant",
        content:
          lang === "hi"
            ? "वार्तालाप साफ़ कर दिया गया। नया प्रश्न पूछें!"
            : "Conversation cleared. Feel free to ask a new question!",
        quickReplies: lang === "hi" ? DEFAULT_SUGGESTIONS_HI.slice(0, 4) : DEFAULT_SUGGESTIONS_EN.slice(0, 4),
      },
    ]);
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-[2500] flex items-end sm:items-center justify-center bg-black/60 backdrop-blur-sm p-0 sm:p-4 animate-in fade-in duration-200">
      <div className="flex flex-col h-[92vh] sm:h-[85vh] w-full max-w-2xl bg-card rounded-t-3xl sm:rounded-2xl border border-border shadow-2xl overflow-hidden">
        {/* Header */}
        <header className="flex items-center justify-between px-4 py-3 border-b border-border bg-gradient-to-r from-pine/10 via-card to-amber-500/10">
          <div className="flex items-center gap-3">
            <div className="relative">
              <span className="grid h-10 w-10 place-items-center rounded-xl bg-pine text-white shadow-md shadow-pine/20">
                <Bot className="h-5 w-5" />
              </span>
              <span className="absolute -bottom-0.5 -right-0.5 h-3 w-3 rounded-full bg-emerald-500 border-2 border-card animate-pulse" />
            </div>
            <div>
              <div className="flex items-center gap-1.5">
                <h2 className="font-display font-semibold text-base sm:text-lg text-foreground">
                  {lang === "hi" ? "AI बस यात्रा सहायक" : "AI Travel Assistant"}
                </h2>
                <span className="inline-flex items-center gap-0.5 rounded-full bg-pine/10 px-2 py-0.5 text-[10px] font-semibold text-pine">
                  <Sparkles className="h-2.5 w-2.5" />
                  Live DB
                </span>
              </div>
              <p className="text-xs text-muted-foreground">
                {lang === "hi" ? "हरियाणा रोडवेज व क्षेत्रीय बसें" : "Haryana Roadways & Regional Transit"}
              </p>
            </div>
          </div>

          <div className="flex items-center gap-1.5">
            <Button
              variant="outline"
              size="sm"
              onClick={() => setLang((prev) => (prev === "hi" ? "en" : "hi"))}
              className="h-8 gap-1 text-xs px-2.5 rounded-full"
              title="Switch English / हिन्दी"
            >
              <Globe className="h-3.5 w-3.5 text-pine" />
              <span>{lang === "hi" ? "EN" : "हिन्दी"}</span>
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={handleClear}
              className="h-8 w-8 p-0 text-muted-foreground hover:text-foreground"
              title="Clear chat"
            >
              <RotateCcw className="h-4 w-4" />
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={onClose}
              className="h-8 w-8 p-0 text-muted-foreground hover:text-foreground"
              title="Close assistant"
            >
              <X className="h-5 w-5" />
            </Button>
          </div>
        </header>

        {/* Messages Body */}
        <div className="flex-1 overflow-y-auto p-4 space-y-4">
          {messages.map((m, idx) => {
            const isUser = m.role === "user";
            const msgId = `msg-${idx}`;
            return (
              <div
                key={idx}
                className={`flex gap-3 ${isUser ? "justify-end" : "justify-start"} animate-in fade-in duration-150`}
              >
                {!isUser && (
                  <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-pine text-white text-sm shadow">
                    <Bot className="h-4 w-4" />
                  </span>
                )}

                <div className={`max-w-[88%] sm:max-w-[80%] space-y-3`}>
                  <div
                    className={`rounded-2xl px-4 py-3 text-sm leading-relaxed shadow-sm ${
                      isUser
                        ? "bg-pine text-white rounded-tr-none ml-auto"
                        : "bg-muted/70 text-foreground border border-border/80 rounded-tl-none"
                    }`}
                  >
                    <div className="whitespace-pre-line break-words">
                      {m.content}
                    </div>

                    {!isUser && (
                      <div className="mt-2 pt-2 border-t border-border/40 flex items-center justify-between text-[11px] text-muted-foreground">
                        <span className="flex items-center gap-1">
                          <CheckCircle2 className="h-3 w-3 text-emerald-500" />
                          Verified from SQL Server DB
                        </span>
                        <button
                          onClick={() => handleSpeak(m.content, msgId)}
                          className="flex items-center gap-1 hover:text-foreground transition-colors p-1"
                          title="Read message aloud"
                        >
                          {isSpeaking === msgId ? (
                            <>
                              <VolumeX className="h-3.5 w-3.5 text-destructive animate-pulse" />
                              <span>Stop</span>
                            </>
                          ) : (
                            <>
                              <Volume2 className="h-3.5 w-3.5" />
                              <span>Speak</span>
                            </>
                          )}
                        </button>
                      </div>
                    )}
                  </div>

                  {/* Rich Bus Search Result Cards */}
                  {m.buses && m.buses.length > 0 && (
                    <div className="space-y-2 pt-1">
                      <div className="flex items-center justify-between text-xs font-semibold text-muted-foreground px-1">
                        <span>Matching Buses ({m.buses.length})</span>
                        <span>Direct from Schedule</span>
                      </div>
                      <div className="grid gap-2">
                        {m.buses.slice(0, 4).map((bus, bIdx) => (
                          <AiBusCardItem key={bIdx} bus={bus} onCloseModal={onClose} />
                        ))}
                      </div>
                    </div>
                  )}

                  {/* Rich Connecting Journey Card */}
                  {m.journeyPlan && m.journeyPlan.allOptions && m.journeyPlan.allOptions.length > 0 && (
                    <div className="pt-1">
                      <AiJourneyPlanItem plan={m.journeyPlan} onCloseModal={onClose} />
                    </div>
                  )}

                  {/* Rich Live Bus Tracking Card */}
                  {m.liveStatus && (
                    <div className="pt-1">
                      <AiLiveStatusCard status={m.liveStatus} onCloseModal={onClose} />
                    </div>
                  )}

                  {/* Rich Stop Timetable Card */}
                  {m.timetable && (
                    <div className="pt-1">
                      <AiTimetableCard timetable={m.timetable} onCloseModal={onClose} />
                    </div>
                  )}

                  {/* Rich Admin Analytics Card */}
                  {m.adminSummary && (
                    <div className="pt-1">
                      <AiAdminAnalyticsCard summary={m.adminSummary} />
                    </div>
                  )}

                  {/* Contextual Quick Suggestions */}
                  {m.quickReplies && m.quickReplies.length > 0 && (
                    <div className="flex flex-wrap gap-1.5 pt-1">
                      {m.quickReplies.map((qr, qrIdx) => (
                        <button
                          key={qrIdx}
                          onClick={() => handleSend(qr)}
                          className="rounded-full bg-secondary hover:bg-secondary/80 border border-border px-3 py-1 text-xs text-foreground transition-all hover:scale-[1.02] active:scale-[0.98]"
                        >
                          {qr}
                        </button>
                      ))}
                    </div>
                  )}
                </div>

                {isUser && (
                  <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-secondary text-secondary-foreground text-xs font-semibold">
                    You
                  </span>
                )}
              </div>
            );
          })}

          {loading && (
            <div className="flex gap-3 justify-start animate-in fade-in">
              <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-pine text-white text-sm">
                <Bot className="h-4 w-4" />
              </span>
              <div className="rounded-2xl rounded-tl-none bg-muted/70 px-4 py-3 border border-border/80 flex items-center gap-2">
                <span className="h-2 w-2 rounded-full bg-pine animate-bounce" />
                <span className="h-2 w-2 rounded-full bg-pine animate-bounce [animation-delay:0.2s]" />
                <span className="h-2 w-2 rounded-full bg-pine animate-bounce [animation-delay:0.4s]" />
                <span className="text-xs text-muted-foreground ml-1">
                  {lang === "hi" ? "डेटाबेस में खोज रहे हैं..." : "Searching database records..."}
                </span>
              </div>
            </div>
          )}

          <div ref={messagesEndRef} />
        </div>

        {/* Listening Banner if Speech active */}
        {isListening && (
          <div className="bg-emerald-500/15 border-t border-emerald-500/30 px-4 py-2 flex items-center justify-between text-xs text-emerald-800 dark:text-emerald-300 animate-pulse">
            <span className="flex items-center gap-2 font-medium">
              <span className="h-2.5 w-2.5 rounded-full bg-emerald-500 animate-ping" />
              {lang === "hi" ? "सुन रहा हूँ... बोलिए (उदा. 'भुना से दिल्ली की बसें')" : "Listening... Speak now (e.g. 'Hisar to Fatehabad next bus')"}
            </span>
            <button onClick={stopListening} className="text-xs underline font-semibold">
              Stop
            </button>
          </div>
        )}

        {/* Input Bar */}
        <footer className="p-3 border-t border-border bg-card">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              handleSend();
            }}
            className="flex items-center gap-2"
          >
            <Button
              type="button"
              variant={isListening ? "destructive" : "outline"}
              size="sm"
              onClick={toggleListening}
              className={`h-10 w-10 p-0 rounded-xl shrink-0 transition-all ${
                isListening ? "animate-pulse ring-2 ring-destructive" : ""
              }`}
              title={lang === "hi" ? "बोलकर पूछें (Voice Search)" : "Voice Search"}
            >
              {isListening ? <MicOff className="h-5 w-5" /> : <Mic className="h-5 w-5 text-pine" />}
            </Button>

            <div className="relative flex-1">
              <input
                ref={inputRef}
                type="text"
                value={input}
                onChange={(e) => setInput(e.target.value)}
                placeholder={
                  lang === "hi"
                    ? "पूछें: 'भुना से दिल्ली सुबह 8 बजे' या बस नंबर..."
                    : "Ask: 'Buses from Bhuna to Delhi after 8am'..."
                }
                className="w-full h-10 rounded-xl border border-input bg-background px-3.5 text-sm text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-pine/30"
              />
            </div>

            <Button
              type="submit"
              disabled={loading || !input.trim()}
              className="h-10 w-10 p-0 rounded-xl bg-pine hover:bg-pine/90 text-white shrink-0 shadow-sm"
              title="Send"
            >
              <Send className="h-4 w-4" />
            </Button>
          </form>
          <div className="mt-1.5 flex items-center justify-between text-[10px] text-muted-foreground px-1">
            <span>Powered by SQL Server & OpenAI Responses Engine</span>
            <span>Accurate Transit Data · Zero Hallucination</span>
          </div>
        </footer>
      </div>
    </div>
  );
}

// ----------------------------------------------------
// Sub-components: Rich Interactive Cards
// ----------------------------------------------------

function AiBusCardItem({ bus, onCloseModal }: { bus: AiBusCard; onCloseModal: () => void }) {
  const navigate = useNavigate();
  const trackUrl = `/bus/${encodeURIComponent(bus.fleetNumber)}?from=${encodeURIComponent(bus.fromStop)}&to=${encodeURIComponent(bus.toStop)}&routeId=${bus.routeId}`;

  return (
    <div className="rounded-xl border border-border bg-card p-3 shadow-sm hover:border-pine/50 transition-all">
      <div className="flex items-start justify-between gap-2">
        <div>
          <div className="flex items-center gap-1.5">
            <span className="font-semibold text-sm text-foreground">{bus.busName}</span>
            <span className="rounded bg-secondary px-1.5 py-0.5 text-[10px] font-mono font-medium text-secondary-foreground">
              {bus.fleetNumber}
            </span>
          </div>
          <p className="text-xs text-muted-foreground mt-0.5">
            {bus.operatorName} • {bus.busType}
          </p>
        </div>

        <span
          className={`rounded-full px-2 py-0.5 text-[11px] font-semibold shrink-0 ${
            bus.isLive
              ? "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border border-emerald-500/30"
              : "bg-muted text-muted-foreground"
          }`}
        >
          {bus.isLive ? "● LIVE" : "Scheduled"}
        </span>
      </div>

      <div className="mt-2.5 flex items-center justify-between rounded-lg bg-muted/40 p-2 text-xs">
        <div>
          <span className="text-[10px] uppercase text-muted-foreground block">Departure</span>
          <span className="font-bold text-sm text-foreground">{bus.departureTime}</span>
          <span className="text-[11px] text-muted-foreground block truncate max-w-[100px]">{bus.fromStop}</span>
        </div>

        <div className="flex flex-col items-center px-2">
          <span className="text-[10px] text-muted-foreground">{bus.durationMinutes}m</span>
          <div className="flex items-center gap-1 text-muted-foreground">
            <div className="h-0.5 w-6 bg-border" />
            <ArrowRight className="h-3 w-3" />
          </div>
          <span className="text-[10px] font-semibold text-pine">₹{bus.estimatedFare}</span>
        </div>

        <div className="text-right">
          <span className="text-[10px] uppercase text-muted-foreground block">Arrival</span>
          <span className="font-bold text-sm text-foreground">{bus.arrivalTime}</span>
          <span className="text-[11px] text-muted-foreground block truncate max-w-[100px]">{bus.toStop}</span>
        </div>
      </div>

      {bus.delayStatus && (
        <div className="mt-2 flex items-center justify-between text-[11px]">
          <span className="text-muted-foreground">
            Status: <span className="font-medium text-foreground">{bus.delayStatus}</span>
          </span>
          {bus.nextStop && (
            <span className="text-muted-foreground">
              Next: <span className="font-medium text-foreground">{bus.nextStop}</span>
            </span>
          )}
        </div>
      )}

      <div className="mt-2.5 pt-2 border-t border-border/60 flex items-center gap-2">
        <Button
          size="sm"
          className="flex-1 h-8 text-xs bg-pine hover:bg-pine/90 text-white gap-1.5"
          onClick={() => {
            onCloseModal();
            navigate(trackUrl);
          }}
        >
          <Radio className="h-3.5 w-3.5 animate-pulse" />
          Track Live
        </Button>
        <Button
          variant="outline"
          size="sm"
          className="h-8 text-xs gap-1"
          onClick={() => {
            onCloseModal();
            navigate(`/routes?routeId=${bus.routeId}`);
          }}
        >
          <Compass className="h-3.5 w-3.5" />
          Route
        </Button>
      </div>
    </div>
  );
}

function AiJourneyPlanItem({ plan, onCloseModal }: { plan: any; onCloseModal: () => void }) {
  const navigate = useNavigate();
  const options = (plan.allOptions || plan.AllOptions || []) as JourneyOption[];
  if (!options || options.length === 0) return null;

  const opt = options[0];

  return (
    <div className="rounded-xl border border-pine/30 bg-card p-3 shadow-sm">
      <div className="flex items-center justify-between">
        <span className="inline-flex items-center gap-1 text-xs font-semibold text-pine">
          <Navigation className="h-3.5 w-3.5" />
          {opt.isDirect ? "Direct Service" : "Connecting Route via " + opt.transferStops.join(", ")}
        </span>
        <span className="text-xs font-bold text-foreground">₹{opt.estimatedTotalFare}</span>
      </div>

      <div className="mt-2 text-xs space-y-1.5">
        <div className="flex justify-between text-muted-foreground">
          <span>Departure: <strong className="text-foreground">{opt.departureTime}</strong></span>
          <span>Arrival: <strong className="text-foreground">{opt.arrivalTime}</strong></span>
          <span>Duration: <strong className="text-foreground">{opt.totalJourneyMinutes}m</strong></span>
        </div>

        {opt.legs && opt.legs.length > 1 && (
          <div className="mt-2 pt-2 border-t border-border/60 space-y-2">
            <span className="text-[11px] font-semibold text-muted-foreground block">Journey Legs:</span>
            {opt.legs.map((leg, lIdx) => (
              <div key={lIdx} className="rounded-lg bg-muted/40 p-2 text-[11px]">
                <div className="flex justify-between font-medium">
                  <span>Leg {lIdx + 1}: {leg.fromStop} ➔ {leg.toStop}</span>
                  <span>{leg.departureTime}</span>
                </div>
                <div className="text-muted-foreground mt-0.5">
                  Route: {leg.routeName} {leg.fleetNumber ? `(${leg.fleetNumber})` : ""}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="mt-3 pt-2 border-t border-border/60">
        <Button
          size="sm"
          className="w-full h-8 text-xs bg-pine hover:bg-pine/90 text-white"
          onClick={() => {
            onCloseModal();
            navigate(`/search?from=${encodeURIComponent(plan.from || plan.From)}&to=${encodeURIComponent(plan.to || plan.To)}`);
          }}
        >
          View Full Journey Schedule
        </Button>
      </div>
    </div>
  );
}

function AiLiveStatusCard({ status, onCloseModal }: { status: AiLiveStatus; onCloseModal: () => void }) {
  const navigate = useNavigate();

  return (
    <div className="rounded-xl border border-emerald-500/30 bg-card p-3 shadow-sm">
      <div className="flex items-start justify-between gap-2">
        <div>
          <div className="flex items-center gap-1.5">
            <span className="font-semibold text-sm text-foreground">{status.busName}</span>
            <span className="rounded bg-secondary px-1.5 py-0.5 text-[10px] font-mono">{status.fleetNumber}</span>
          </div>
          <p className="text-xs text-muted-foreground">{status.routeName}</p>
        </div>
        <span className="inline-flex items-center gap-1 rounded-full bg-emerald-500/15 px-2 py-0.5 text-xs font-semibold text-emerald-600 dark:text-emerald-400 border border-emerald-500/30">
          <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 animate-ping" />
          {status.speedKmph ? `${Math.round(status.speedKmph)} km/h` : "Live"}
        </span>
      </div>

      <div className="mt-2.5 grid grid-cols-2 gap-2 text-xs">
        <div className="rounded-lg bg-muted/40 p-2">
          <span className="text-[10px] text-muted-foreground block">Current Location</span>
          <span className="font-semibold text-foreground truncate block">{status.currentLocation || "In transit"}</span>
        </div>
        <div className="rounded-lg bg-muted/40 p-2">
          <span className="text-[10px] text-muted-foreground block">Next Stop (ETA)</span>
          <span className="font-semibold text-foreground truncate block">
            {status.nextStop || "Destination"} {status.etaMinutes ? `(${status.etaMinutes}m)` : ""}
          </span>
        </div>
      </div>

      <div className="mt-2.5 pt-2 border-t border-border/60">
        <Button
          size="sm"
          className="w-full h-8 text-xs bg-pine hover:bg-pine/90 text-white gap-1.5"
          onClick={() => {
            onCloseModal();
            navigate(`/track/${encodeURIComponent(status.fleetNumber)}`);
          }}
        >
          <Radio className="h-3.5 w-3.5 animate-pulse" />
          Open Live Map
        </Button>
      </div>
    </div>
  );
}

function AiTimetableCard({ timetable, onCloseModal }: { timetable: StopTimetableResponse; onCloseModal: () => void }) {
  const navigate = useNavigate();
  const upcoming = timetable.upcomingBuses || (timetable as any).UpcomingArrivals || [];

  return (
    <div className="rounded-xl border border-border bg-card p-3 shadow-sm">
      <div className="flex items-center justify-between">
        <div>
          <h4 className="font-semibold text-sm text-foreground">{timetable.stop.name}</h4>
          <p className="text-xs text-muted-foreground">
            Code: {timetable.stop.code} • {upcoming.length} departures today
          </p>
        </div>
        <span className="rounded-full bg-pine/10 text-pine text-xs px-2 py-0.5 font-semibold">
          Stand Board
        </span>
      </div>

      {upcoming.length > 0 && (
        <div className="mt-2 space-y-1.5">
          {upcoming.slice(0, 3).map((u: any, idx: number) => (
            <div key={idx} className="flex items-center justify-between text-xs rounded-lg bg-muted/40 p-2">
              <div>
                <span className="font-bold text-foreground">{u.scheduledTime || u.ScheduledTime}</span>
                <span className="text-muted-foreground ml-1.5">➔ {u.destinationStop || u.DestinationStop}</span>
              </div>
              <span className="text-[11px] text-muted-foreground">{u.operatorName || u.OperatorName}</span>
            </div>
          ))}
        </div>
      )}

      <div className="mt-2.5 pt-2 border-t border-border/60">
        <Button
          variant="outline"
          size="sm"
          className="w-full h-8 text-xs gap-1.5"
          onClick={() => {
            onCloseModal();
            navigate(`/stop/${encodeURIComponent(timetable.stop.code)}`);
          }}
        >
          <ExternalLink className="h-3.5 w-3.5" />
          Open Full Stop Board & QR Poster
        </Button>
      </div>
    </div>
  );
}

function AiAdminAnalyticsCard({ summary }: { summary: any }) {
  return (
    <div className="rounded-xl border border-border bg-card p-3 text-xs shadow-sm">
      <h4 className="font-semibold text-sm text-foreground mb-2">Operations Summary</h4>
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
        <div className="rounded-lg bg-muted/40 p-2">
          <span className="text-[10px] text-muted-foreground block">Active Fleet</span>
          <span className="font-bold text-foreground">{summary.activeBuses || summary.ActiveBuses} / {summary.totalBuses || summary.TotalBuses}</span>
        </div>
        <div className="rounded-lg bg-muted/40 p-2">
          <span className="text-[10px] text-muted-foreground block">Active Routes</span>
          <span className="font-bold text-foreground">{summary.activeRoutes || summary.ActiveRoutes}</span>
        </div>
        <div className="rounded-lg bg-muted/40 p-2">
          <span className="text-[10px] text-muted-foreground block">Trips Today</span>
          <span className="font-bold text-foreground">{summary.totalTripsToday || summary.TotalTripsToday}</span>
        </div>
        <div className="rounded-lg bg-muted/40 p-2">
          <span className="text-[10px] text-muted-foreground block">Delayed Buses</span>
          <span className="font-bold text-destructive">{summary.delayedBusesCount || summary.DelayedBusesCount}</span>
        </div>
      </div>
      {summary.routesWithoutSchedules && summary.routesWithoutSchedules.length > 0 && (
        <div className="mt-2 rounded-lg bg-amber-500/10 border border-amber-500/20 p-2 text-amber-800 dark:text-amber-300">
          <span className="font-semibold">⚠️ Routes without active schedules:</span>
          <p className="mt-0.5">{summary.routesWithoutSchedules.slice(0, 2).join(", ")}</p>
        </div>
      )}
    </div>
  );
}
