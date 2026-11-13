import { useState } from "react";
import { Bot, Mic, Sparkles } from "lucide-react";
import { AiTravelAssistantModal } from "./AiTravelAssistantModal";

export function AiFloatingButton() {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <>
      <div className="fixed bottom-5 right-5 z-[1500] flex flex-col items-end gap-2 pointer-events-auto">
        <button
          onClick={() => setIsOpen(true)}
          className="group relative flex items-center gap-2 rounded-full bg-gradient-to-r from-pine to-emerald-600 px-4 py-3 text-white shadow-xl shadow-pine/30 hover:shadow-2xl hover:shadow-pine/40 transition-all hover:scale-105 active:scale-95 focus:outline-none focus:ring-4 focus:ring-pine/30"
          aria-label="Open AI Bus Travel Assistant"
        >
          {/* Pulsing glow ring */}
          <span className="absolute -inset-0.5 rounded-full bg-gradient-to-r from-pine to-amber-500 opacity-60 blur group-hover:opacity-100 transition duration-300 animate-pulse" />

          <div className="relative flex items-center gap-2">
            <span className="grid h-7 w-7 place-items-center rounded-full bg-white/20 text-white backdrop-blur-sm">
              <Bot className="h-4 w-4" />
            </span>
            <div className="flex flex-col text-left">
              <span className="text-xs font-bold leading-tight flex items-center gap-1">
                AI Assistant
                <Sparkles className="h-3 w-3 text-amber-300" />
              </span>
              <span className="text-[10px] text-white/80 leading-none">बस सहायक (Voice/Text)</span>
            </div>
            <span className="grid h-6 w-6 place-items-center rounded-full bg-white/10 ml-1">
              <Mic className="h-3.5 w-3.5 text-white/90" />
            </span>
          </div>
        </button>
      </div>

      <AiTravelAssistantModal isOpen={isOpen} onClose={() => setIsOpen(false)} />
    </>
  );
}
