import { useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  ArrowRight,
  Bot,
  Bus,
  CheckCircle2,
  Clock,
  Compass,
  ExternalLink,
  Globe,
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
} from "lucide-react";
import { api } from "@/lib/api";
import { Button } from "@/components/ui/button";
import type {
  AiBusCard,
  AiChatMessage,
  AiChatResponse,
  AiLiveStatus,
  JourneyOption,
  StopTimetableResponse,
} from "@/lib/types";

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

export function AiAssistantPage() {
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

  useEffect(() => {
    if (recognitionRef.current) {
      recognitionRef.current.lang = lang === "hi" ? "hi-IN" : "en-IN";
    }
  }, [lang]);

  useEffect(() => {
    if (messages.length === 0) {
      setMessages([
        {
          role: "assistant",
          content:
            lang === "hi"
              ? "नमस्ते! 😊 मैं आपका **AI बस यात्रा सहायक** हूँ।\n\nआप मुझसे हरियाणा रोडवेज व क्षेत्रीय बसों के रूट, समय, लाइव लोकेशन या कनेक्टिंग बसों के बारे में हिंदी या अंग्रेजी में पूछ सकते हैं। आप टाइप कर सकते हैं या माइक दबाकर बोल सकते हैं!"
              : "Hello! 👋 I am your **AI Bus Travel Assistant**.\n\nAsk me about bus timings, routes, live GPS tracking, connecting buses, or station timetables in English or Hindi. Type your question or use the microphone to speak!",
          quickReplies: lang === "hi" ? DEFAULT_SUGGESTIONS_HI.slice(0, 4) : DEFAULT_SUGGESTIONS_EN.slice(0, 4),
        },
      ]);
    }
  }, [lang]);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, loading]);

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
    const cleanText = text.replace(/[*#_~`]/g, "").replace(/https?:\/\/\S+/g, "");
    const utterance = new SpeechSynthesisUtterance(cleanText);
    utterance.lang = lang === "hi" ? "hi-IN" : "en-IN";
    utterance.rate = 0.95;

    utterance.onend = () => setIsSpeaking(null);
    utterance.onerror = () => setIsSpeaking(null);

    setIsSpeaking(msgId);
    window.speechSynthesis.speak(utterance);
  };

  const handleSend = async (textToSend?: string) => {
    const query = (textToSend || input).trim();
    if (!query || loading) return;

    stopListening();
    setInput("");

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
              ? "क्षमा करें, जानकारी लोड करने में त्रुटि हुई। कृपया पुनः प्रयास करें।"
              : "Sorry, could not fetch bus information right now. Please try again.",
          quickReplies: lang === "hi" ? DEFAULT_SUGGESTIONS_HI.slice(0, 3) : DEFAULT_SUGGESTIONS_EN.slice(0, 3),
        },
      ]);
    } finally {
      setLoading(false);
    }
  };

  const handleClear = () => {
    if ("speechSynthesis" in window) window.speechSynthesis.cancel();
    setMessages([
      {
        role: "assistant",
        content:
          lang === "hi"
            ? "वार्तालाप रीसेट कर दिया गया। नया प्रश्न पूछें!"
            : "Conversation reset. Feel free to ask another transit question!",
        quickReplies: lang === "hi" ? DEFAULT_SUGGESTIONS_HI.slice(0, 4) : DEFAULT_SUGGESTIONS_EN.slice(0, 4),
      },
    ]);
  };

  return (
    <div className="mx-auto max-w-4xl px-4 py-6">
      <div className="flex flex-col h-[82vh] bg-card rounded-2xl border border-border shadow-xl overflow-hidden">
        {/* Top Header */}
        <header className="flex items-center justify-between px-5 py-4 border-b border-border bg-gradient-to-r from-pine/15 via-card to-amber-500/10">
          <div className="flex items-center gap-3">
            <span className="grid h-11 w-11 place-items-center rounded-xl bg-pine text-white shadow-md shadow-pine/20">
              <Bot className="h-6 w-6" />
            </span>
            <div>
              <div className="flex items-center gap-2">
                <h1 className="font-display font-bold text-lg sm:text-xl text-foreground">
                  {lang === "hi" ? "AI बस यात्रा सहायक" : "AI Travel Assistant"}
                </h1>
                <span className="inline-flex items-center gap-1 rounded-full bg-emerald-500/15 border border-emerald-500/30 px-2.5 py-0.5 text-xs font-semibold text-emerald-600 dark:text-emerald-400">
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 animate-ping" />
                  Live SQL DB
                </span>
              </div>
              <p className="text-xs text-muted-foreground">
                {lang === "hi"
                  ? "OpenAI फंक्शन कॉलिंग व वास्तविक डेटाबेस आधारित बस खोज व ट्रैकिंग"
                  : "Database-backed bus schedules, real-time GPS, and intelligent journey planning"}
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => setLang((prev) => (prev === "hi" ? "en" : "hi"))}
              className="gap-1.5 text-xs rounded-full"
            >
              <Globe className="h-3.5 w-3.5 text-pine" />
              <span>{lang === "hi" ? "English" : "हिन्दी"}</span>
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={handleClear}
              className="h-8 w-8 p-0 text-muted-foreground hover:text-foreground"
              title="Reset conversation"
            >
              <RotateCcw className="h-4 w-4" />
            </Button>
          </div>
        </header>

        {/* Message Stream */}
        <div className="flex-1 overflow-y-auto p-4 sm:p-6 space-y-4">
          {messages.map((m, idx) => {
            const isUser = m.role === "user";
            const msgId = `page-msg-${idx}`;
            return (
              <div
                key={idx}
                className={`flex gap-3 ${isUser ? "justify-end" : "justify-start"} animate-in fade-in duration-150`}
              >
                {!isUser && (
                  <span className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-pine text-white shadow">
                    <Bot className="h-5 w-5" />
                  </span>
                )}

                <div className="max-w-[92%] sm:max-w-[82%] space-y-3">
                  <div
                    className={`rounded-2xl px-5 py-3.5 text-sm leading-relaxed shadow-sm ${
                      isUser
                        ? "bg-pine text-white rounded-tr-none ml-auto"
                        : "bg-muted/70 text-foreground border border-border/80 rounded-tl-none"
                    }`}
                  >
                    <div className="whitespace-pre-line break-words">{m.content}</div>

                    {!isUser && (
                      <div className="mt-2.5 pt-2 border-t border-border/40 flex items-center justify-between text-[11px] text-muted-foreground">
                        <span className="flex items-center gap-1">
                          <CheckCircle2 className="h-3.5 w-3.5 text-emerald-500" />
                          Verified from Database
                        </span>
                        <button
                          onClick={() => handleSpeak(m.content, msgId)}
                          className="flex items-center gap-1 hover:text-foreground transition-colors p-1"
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

                  {/* Bus Cards */}
                  {m.buses && m.buses.length > 0 && (
                    <div className="space-y-2 pt-1">
                      <span className="text-xs font-semibold text-muted-foreground block px-1">
                        Matching Buses ({m.buses.length})
                      </span>
                      <div className="grid sm:grid-cols-2 gap-2">
                        {m.buses.slice(0, 4).map((bus, bIdx) => (
                          <PageBusCard key={bIdx} bus={bus} />
                        ))}
                      </div>
                    </div>
                  )}

                  {/* Quick Suggestions */}
                  {m.quickReplies && m.quickReplies.length > 0 && (
                    <div className="flex flex-wrap gap-2 pt-1">
                      {m.quickReplies.map((qr, qrIdx) => (
                        <button
                          key={qrIdx}
                          onClick={() => handleSend(qr)}
                          className="rounded-full bg-secondary hover:bg-secondary/80 border border-border px-3.5 py-1 text-xs text-foreground transition-all hover:scale-[1.02] active:scale-[0.98]"
                        >
                          {qr}
                        </button>
                      ))}
                    </div>
                  )}
                </div>

                {isUser && (
                  <span className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-secondary text-secondary-foreground text-xs font-semibold">
                    You
                  </span>
                )}
              </div>
            );
          })}

          {loading && (
            <div className="flex gap-3 justify-start animate-in fade-in">
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-pine text-white">
                <Bot className="h-5 w-5" />
              </span>
              <div className="rounded-2xl rounded-tl-none bg-muted/70 px-5 py-3.5 border border-border/80 flex items-center gap-2">
                <span className="h-2 w-2 rounded-full bg-pine animate-bounce" />
                <span className="h-2 w-2 rounded-full bg-pine animate-bounce [animation-delay:0.2s]" />
                <span className="h-2 w-2 rounded-full bg-pine animate-bounce [animation-delay:0.4s]" />
                <span className="text-xs text-muted-foreground ml-2">
                  {lang === "hi" ? "डेटाबेस में खोज जारी है..." : "Executing search query against SQL Server..."}
                </span>
              </div>
            </div>
          )}

          <div ref={messagesEndRef} />
        </div>

        {/* Listening notification */}
        {isListening && (
          <div className="bg-emerald-500/15 border-t border-emerald-500/30 px-5 py-2.5 flex items-center justify-between text-xs text-emerald-800 dark:text-emerald-300 animate-pulse">
            <span className="flex items-center gap-2 font-medium">
              <span className="h-2.5 w-2.5 rounded-full bg-emerald-500 animate-ping" />
              {lang === "hi" ? "सुन रहा हूँ... बोलिए (उदा. 'भुना से दिल्ली की बसें')" : "Listening in real time... Speak now"}
            </span>
            <button onClick={stopListening} className="text-xs underline font-semibold">
              Stop
            </button>
          </div>
        )}

        {/* Input Bar */}
        <footer className="p-4 border-t border-border bg-card">
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
              onClick={toggleListening}
              className={`h-11 w-11 p-0 rounded-xl shrink-0 transition-all ${
                isListening ? "animate-pulse ring-2 ring-destructive" : ""
              }`}
              title="Voice Search"
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
                    ? "पूछें: 'भुना से दिल्ली सुबह 8 बजे' या 'Hisar to Fatehabad'..."
                    : "Ask in English or Hindi: 'Buses from Bhuna to Delhi after 8:00 AM'..."
                }
                className="w-full h-11 rounded-xl border border-input bg-background px-4 text-sm text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-2 focus:ring-pine/30"
              />
            </div>

            <Button
              type="submit"
              disabled={loading || !input.trim()}
              className="h-11 w-11 p-0 rounded-xl bg-pine hover:bg-pine/90 text-white shrink-0 shadow-sm"
              title="Send"
            >
              <Send className="h-5 w-5" />
            </Button>
          </form>
        </footer>
      </div>
    </div>
  );
}

function PageBusCard({ bus }: { bus: AiBusCard }) {
  const navigate = useNavigate();
  const trackUrl = `/bus/${encodeURIComponent(bus.fleetNumber)}?from=${encodeURIComponent(bus.fromStop)}&to=${encodeURIComponent(bus.toStop)}&routeId=${bus.routeId}`;

  return (
    <div className="rounded-xl border border-border bg-card p-3 shadow-sm hover:border-pine/40 transition-all">
      <div className="flex items-start justify-between gap-1.5">
        <div>
          <span className="font-semibold text-sm text-foreground block">{bus.busName}</span>
          <span className="text-xs text-muted-foreground">{bus.operatorName} • {bus.fleetNumber}</span>
        </div>
        <span
          className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${
            bus.isLive
              ? "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border border-emerald-500/30"
              : "bg-muted text-muted-foreground"
          }`}
        >
          {bus.isLive ? "● LIVE" : "Scheduled"}
        </span>
      </div>

      <div className="mt-2 flex items-center justify-between text-xs bg-muted/40 p-2 rounded-lg">
        <div>
          <span className="text-[10px] text-muted-foreground block">Dep</span>
          <span className="font-bold">{bus.departureTime}</span>
        </div>
        <div className="text-center">
          <span className="text-[10px] text-muted-foreground">{bus.durationMinutes}m</span>
          <ArrowRight className="h-3 w-3 text-muted-foreground mx-auto" />
        </div>
        <div className="text-right">
          <span className="text-[10px] text-muted-foreground block">Arr</span>
          <span className="font-bold">{bus.arrivalTime}</span>
        </div>
      </div>

      <div className="mt-2.5 pt-2 border-t border-border/60 flex items-center gap-2">
        <Button
          size="sm"
          className="flex-1 h-8 text-xs bg-pine hover:bg-pine/90 text-white gap-1"
          onClick={() => navigate(trackUrl)}
        >
          <Radio className="h-3 w-3 animate-pulse" />
          Track Live
        </Button>
        <Button
          variant="outline"
          size="sm"
          className="h-8 text-xs"
          onClick={() => navigate(`/routes?routeId=${bus.routeId}`)}
        >
          Route
        </Button>
      </div>
    </div>
  );
}
