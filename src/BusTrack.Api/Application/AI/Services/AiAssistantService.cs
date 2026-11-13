using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusTrack.Api.Application.AI.DTOs;
using BusTrack.Api.Application.AI.Functions;
using BusTrack.Api.Application.AI.Infrastructure;
using BusTrack.Api.Application.AI.Interfaces;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Application.AI.Services;

public class AiAssistantService : IAiAssistantService
{
    private readonly AppDbContext _db;
    private readonly TrackingService _trackingService;
    private readonly OpenAiClient _openAiClient;
    private readonly SearchBusesFunction _searchBuses;
    private readonly PlanJourneyFunction _planJourney;
    private readonly GetLiveStatusFunction _getLiveStatus;
    private readonly GetTimetableFunction _getTimetable;
    private readonly GetRouteFunction _getRoute;
    private readonly GetAdminAnalyticsFunction _getAdminAnalytics;
    private readonly ILogger<AiAssistantService> _logger;

    private static readonly Dictionary<string, string> HindiToEnglishStops = new(StringComparer.OrdinalIgnoreCase)
    {
        { "भुना", "Bhuna" },
        { "भूना", "Bhuna" },
        { "हिसार", "Hisar" },
        { "फतेहाबाद", "Fatehabad" },
        { "फतेहाबाद बस स्टैंड", "Fatehabad Bus Stand" },
        { "दिल्ली", "Delhi" },
        { "दिल्ली आईएसबीटी", "Delhi ISBT" },
        { "कश्मीरी गेट", "Delhi ISBT" },
        { "हांसी", "Hansi" },
        { "हासी", "Hansi" },
        { "रोहतक", "Rohtak" },
        { "सिरसा", "Sirsa" },
        { "रतिया", "Ratia" },
        { "टोहाना", "Tohana" },
        { "आदमपुर", "Adampur" },
        { "मुंढाल", "Mundhal" },
        { "बरवाला", "Barwala" },
        { "उकलाना", "Uklana" },
        { "चंडीगढ़", "Chandigarh" },
        { "अंबाला", "Ambala" },
        { "जींद", "Jind" },
        { "भिवानी", "Bhiwani" }
    };

    public AiAssistantService(
        AppDbContext db,
        TrackingService trackingService,
        OpenAiClient openAiClient,
        SearchBusesFunction searchBuses,
        PlanJourneyFunction planJourney,
        GetLiveStatusFunction getLiveStatus,
        GetTimetableFunction getTimetable,
        GetRouteFunction getRoute,
        GetAdminAnalyticsFunction getAdminAnalytics,
        ILogger<AiAssistantService> logger)
    {
        _db = db;
        _trackingService = trackingService;
        _openAiClient = openAiClient;
        _searchBuses = searchBuses;
        _planJourney = planJourney;
        _getLiveStatus = getLiveStatus;
        _getTimetable = getTimetable;
        _getRoute = getRoute;
        _getAdminAnalytics = getAdminAnalytics;
        _logger = logger;
    }

    public async Task<AiChatResponse> ProcessChatAsync(
        AiChatRequest request,
        ClaimsPrincipal? user = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return new AiChatResponse(
                Reply: "कृपया अपना प्रश्न पूछें (उदा. 'भुना से दिल्ली की बसें' या 'Live status of Demo-22-R-2202')",
                Language: "hi",
                Intent: "general"
            );
        }

        var lang = DetermineLanguage(request.Message, request.Language);

        // 1. If OpenAI is configured, try OpenAI Function Calling
        if (_openAiClient.IsConfigured)
        {
            try
            {
                var openAiResponse = await TryOpenAiChatAsync(request, lang, ct);
                if (openAiResponse != null)
                {
                    return openAiResponse;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI execution encountered error. Falling back to local transit engine.");
            }
        }

        // 2. Intelligent Local NLP Engine (Zero Hallucination, 100% authentic DB data)
        return await ProcessLocallyAsync(request.Message, lang, user, ct);
    }

    private string DetermineLanguage(string message, string? requestedLang)
    {
        if (requestedLang == "hi") return "hi";
        if (requestedLang == "en") return "en";

        // Check if message contains Devanagari Unicode
        if (Regex.IsMatch(message, @"[\u0900-\u097F]"))
            return "hi";

        // Check for common Hinglish transit tokens
        var lower = message.ToLowerInvariant();
        if (lower.Contains("kahan") || lower.Contains("kab") || lower.Contains("batao") ||
            lower.Contains("chahiye") || lower.Contains("jana") || lower.Contains("milegi") ||
            lower.Contains("baje") || lower.Contains("subah") || lower.Contains("shaam"))
        {
            return "hi";
        }

        return "en";
    }

    private async Task<AiChatResponse?> TryOpenAiChatAsync(
        AiChatRequest request,
        string lang,
        CancellationToken ct)
    {
        var nowIst = IndiaClock.Now();
        var today = DateOnly.FromDateTime(nowIst);
        var timeStr = nowIst.ToString("HH:mm");

        var systemPrompt = $@"You are the official AI Bus Travel Assistant for 'WhereIsMyBus' (now-you-see-bus) Haryana & North India state transit network.
Today is {today:yyyy-MM-dd} (IST). Current local time is {timeStr} IST.
User language preference is {lang}.

CRITICAL RULES:
1. NEVER invent, fabricate or guess bus timings, stops, fares, or GPS coordinates.
2. ALWAYS use function calling to fetch real database data.
3. If user writes in Hindi or Hinglish, reply warmly in polite Hindi.
4. Keep the text concise and user-friendly, emphasizing departure time, route, and live tracking.";

        var messagesArray = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            }
        };

        if (request.History != null)
        {
            foreach (var h in request.History.TakeLast(6))
            {
                messagesArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = h.Role,
                    ["content"] = h.Content
                });
            }
        }

        messagesArray.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["role"] = "user",
            ["content"] = request.Message
        });

        var tools = OpenAiClient.GetStandardTools();
        var result = await _openAiClient.CallChatAsync(messagesArray, tools, ct);

        if (result == null) return null;

        if (result.HasToolCalls)
        {
            var firstCall = result.ToolCalls[0];
            var fnName = firstCall.FunctionName;
            var args = firstCall.Arguments;

            switch (fnName)
            {
                case "search_buses":
                {
                    var from = args.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                    var to = args.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
                    var depAfterStr = args.TryGetProperty("departure_after", out var da) ? da.GetString() : null;
                    var depBeforeStr = args.TryGetProperty("departure_before", out var db) ? db.GetString() : null;
                    TimeSpan? depAfter = TimeSpan.TryParse(depAfterStr, out var d1) ? d1 : null;
                    TimeSpan? depBefore = TimeSpan.TryParse(depBeforeStr, out var d2) ? d2 : null;

                    var resolvedFrom = NormalizeStopName(from);
                    var resolvedTo = NormalizeStopName(to);

                    var buses = await _searchBuses.ExecuteAsync(resolvedFrom, resolvedTo, today, depAfter, depBefore, false, ct);
                    return BuildBusSearchResponse(resolvedFrom, resolvedTo, buses, depAfter, lang);
                }

                case "plan_journey":
                {
                    var from = args.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                    var to = args.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
                    var resolvedFrom = NormalizeStopName(from);
                    var resolvedTo = NormalizeStopName(to);

                    var plan = await _planJourney.ExecuteAsync(resolvedFrom, resolvedTo, today, ct);
                    return BuildJourneyPlanResponse(resolvedFrom, resolvedTo, plan, lang);
                }

                case "get_live_status":
                {
                    var busNumber = args.TryGetProperty("bus_number", out var bn) ? bn.GetString() ?? "" : "";
                    var status = await _getLiveStatus.ExecuteAsync(busNumber, ct);
                    return BuildLiveStatusResponse(busNumber, status, lang);
                }

                case "get_stop_timetable":
                {
                    var stopName = args.TryGetProperty("stop_code_or_name", out var sn) ? sn.GetString() ?? "" : "";
                    var resolvedStop = NormalizeStopName(stopName);
                    var timetable = await _getTimetable.ExecuteAsync(resolvedStop, today, ct);
                    return BuildTimetableResponse(resolvedStop, timetable, lang);
                }

                case "get_admin_analytics":
                {
                    var analytics = await _getAdminAnalytics.ExecuteAsync(ct);
                    return BuildAdminAnalyticsResponse(analytics, lang);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(result.MessageContent))
        {
            return new AiChatResponse(
                Reply: result.MessageContent,
                Language: lang,
                Intent: "general"
            );
        }

        return null;
    }

    private async Task<AiChatResponse> ProcessLocallyAsync(
        string message,
        string lang,
        ClaimsPrincipal? user,
        CancellationToken ct)
    {
        var raw = message.Trim();
        var lower = raw.ToLowerInvariant();
        var nowIst = IndiaClock.Now();
        var today = DateOnly.FromDateTime(nowIst);

        // 1. Admin analytics check
        if (lower.Contains("missing schedule") || lower.Contains("inactive bus") ||
            lower.Contains("analytics") || lower.Contains("admin summary") ||
            (lower.Contains("admin") && (lower.Contains("show") || lower.Contains("dikhaye") || lower.Contains("status"))))
        {
            var analytics = await _getAdminAnalytics.ExecuteAsync(ct);
            return BuildAdminAnalyticsResponse(analytics, lang);
        }

        // 2. Stop Timetable / Bus Stand Inquiry
        if (lower.Contains("timetable") || lower.Contains("time table") || lower.Contains("अड्डा") ||
            lower.Contains("टाइमटेबल") || lower.Contains("समय सारणी") || lower.Contains("departures") ||
            lower.Contains("board") || lower.Contains("stand timings"))
        {
            var detectedStop = ExtractSingleStop(raw);
            if (!string.IsNullOrWhiteSpace(detectedStop))
            {
                var timetable = await _getTimetable.ExecuteAsync(detectedStop, today, ct);
                return BuildTimetableResponse(detectedStop, timetable, lang);
            }
        }

        // 3. Live bus status check
        var fleetMatch = Regex.Match(raw, @"\b(Demo-\w+-\w+-\w+|HR-?\d{1,2}[A-Z]?-?\d{1,4}|\d{3})\b", RegexOptions.IgnoreCase);
        if (fleetMatch.Success || lower.Contains("live status") || lower.Contains("kahan pahunchi") || lower.Contains("kahan hai") || lower.Contains("where is bus"))
        {
            var busIdentifier = fleetMatch.Success ? fleetMatch.Value : ExtractBusNumber(raw);
            if (!string.IsNullOrWhiteSpace(busIdentifier))
            {
                var status = await _getLiveStatus.ExecuteAsync(busIdentifier, ct);
                return BuildLiveStatusResponse(busIdentifier, status, lang);
            }
        }

        // 4. Multi-Route / Connecting Journey inquiry
        bool isConnectingQuery = lower.Contains("connecting") || lower.Contains("via") ||
                                 lower.Contains("badal kar") || lower.Contains("transfer") ||
                                 lower.Contains("connection");

        // 5. Origin & Destination Extraction
        var (from, to) = ExtractOriginDestination(raw);

        if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
        {
            var resolvedFrom = NormalizeStopName(from);
            var resolvedTo = NormalizeStopName(to);

            // Time filter extraction (e.g. "सुबह 8 बजे के बाद" -> 08:00)
            var (depAfter, depBefore) = ExtractTimeConstraints(raw);

            if (isConnectingQuery)
            {
                var plan = await _planJourney.ExecuteAsync(resolvedFrom, resolvedTo, today, ct);
                return BuildJourneyPlanResponse(resolvedFrom, resolvedTo, plan, lang);
            }

            var buses = await _searchBuses.ExecuteAsync(resolvedFrom, resolvedTo, today, depAfter, depBefore, false, ct);
            if (buses.Count > 0)
            {
                return BuildBusSearchResponse(resolvedFrom, resolvedTo, buses, depAfter, lang);
            }

            // If direct buses are empty, try connecting journey planner!
            var fallbackPlan = await _planJourney.ExecuteAsync(resolvedFrom, resolvedTo, today, ct);
            if (fallbackPlan.AllOptions.Count > 0)
            {
                return BuildJourneyPlanResponse(resolvedFrom, resolvedTo, fallbackPlan, lang);
            }

            return BuildNoBusesResponse(resolvedFrom, resolvedTo, depAfter, lang);
        }

        // 6. Help / General transit guidance
        return BuildGeneralHelpResponse(lang);
    }

    private string NormalizeStopName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        var trimmed = input.Trim();

        if (HindiToEnglishStops.TryGetValue(trimmed, out var mapped))
            return mapped;

        foreach (var kvp in HindiToEnglishStops)
        {
            if (trimmed.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return trimmed;
    }

    private (string? From, string? To) ExtractOriginDestination(string message)
    {
        var text = message;

        // Hindi pattern 1: "मुझे [A] से [B] जाना है" or "[A] से [B] की बस"
        var hiMatch = Regex.Match(text, @"(?:मुझे\s+)?([^\s]+)\s+से\s+([^\s]+)(?:\s+जाना|\s+की|\s+तक|\s+बस)", RegexOptions.IgnoreCase);
        if (hiMatch.Success)
        {
            return (CleanStopName(hiMatch.Groups[1].Value), CleanStopName(hiMatch.Groups[2].Value));
        }

        // Hindi pattern 2: "[A] se [B] ki bus" or "[A] se [B]"
        var hinglishMatch = Regex.Match(text, @"\b([a-zA-Z]+)\s+se\s+([a-zA-Z]+)\b", RegexOptions.IgnoreCase);
        if (hinglishMatch.Success)
        {
            return (CleanStopName(hinglishMatch.Groups[1].Value), CleanStopName(hinglishMatch.Groups[2].Value));
        }

        // English pattern: "from [A] to [B]"
        var enFromToMatch = Regex.Match(text, @"\bfrom\s+([a-zA-Z\s]+?)\s+to\s+([a-zA-Z\s]+?)(?:\s+after|\s+before|\s+on|\s*$)", RegexOptions.IgnoreCase);
        if (enFromToMatch.Success)
        {
            return (CleanStopName(enFromToMatch.Groups[1].Value), CleanStopName(enFromToMatch.Groups[2].Value));
        }

        // English pattern: "[A] to [B]"
        var enToMatch = Regex.Match(text, @"\b([a-zA-Z]+)\s+to\s+([a-zA-Z]+)\b", RegexOptions.IgnoreCase);
        if (enToMatch.Success)
        {
            return (CleanStopName(enToMatch.Groups[1].Value), CleanStopName(enToMatch.Groups[2].Value));
        }

        // Check dictionary containment ordered by position of appearance in sentence
        var matches = HindiToEnglishStops
            .Where(kvp => text.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => new
            {
                Stop = kvp.Value,
                Index = text.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(x => x.Index)
            .Select(x => x.Stop)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count >= 2)
        {
            return (matches[0], matches[1]);
        }

        return (null, null);
    }

    private string? ExtractSingleStop(string text)
    {
        // Check longest matching keys first so "फतेहाबाद बस स्टैंड" matches before "फतेहाबाद"
        foreach (var kvp in HindiToEnglishStops.OrderByDescending(k => k.Key.Length))
        {
            if (text.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        var match = Regex.Match(text, @"\b(Fatehabad\s+Bus\s+Stand|Fatehabad|Bhuna|Hisar|Delhi\s+ISBT|Delhi|Hansi|Rohtak|Sirsa|Ratia|Tohana)\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    private string? ExtractBusNumber(string text)
    {
        var m = Regex.Match(text, @"\b(Demo-\w+-\w+-\w+|HR-?\d{1,2}[A-Z]?-?\d{1,4}|\d{3})\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }

    private (TimeSpan? DepAfter, TimeSpan? DepBefore) ExtractTimeConstraints(string text)
    {
        TimeSpan? depAfter = null;
        TimeSpan? depBefore = null;

        // Hindi "सुबह 8 बजे के बाद"
        var hiAfter = Regex.Match(text, @"(?:सुबह|शाम|दोपहर|रात)?\s*(\d{1,2})\s*बजे\s*के\s*बाद", RegexOptions.IgnoreCase);
        if (hiAfter.Success && int.TryParse(hiAfter.Groups[1].Value, out var h))
        {
            if (text.Contains("शाम") || text.Contains("रात"))
            {
                if (h < 12) h += 12;
            }
            depAfter = TimeSpan.FromHours(h);
        }

        // English "after 8 am" / "after 08:00" / "after 8"
        var enAfter = Regex.Match(text, @"\bafter\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?", RegexOptions.IgnoreCase);
        if (enAfter.Success && int.TryParse(enAfter.Groups[1].Value, out var eh))
        {
            int em = enAfter.Groups[2].Success && int.TryParse(enAfter.Groups[2].Value, out var mVal) ? mVal : 0;
            var meridiem = enAfter.Groups[3].Value.ToLowerInvariant();
            if (meridiem == "pm" && eh < 12) eh += 12;
            if (meridiem == "am" && eh == 12) eh = 0;
            depAfter = new TimeSpan(eh, em, 0);
        }

        // "last bus"
        if (text.Contains("last bus", StringComparison.OrdinalIgnoreCase) || text.Contains("अंतिम बस", StringComparison.OrdinalIgnoreCase) || text.Contains("आखिरी बस", StringComparison.OrdinalIgnoreCase))
        {
            // Set departure after 17:00 (5 PM) or we pick the last bus from results
            depAfter = TimeSpan.FromHours(14);
        }

        return (depAfter, depBefore);
    }

    private string CleanStopName(string raw)
    {
        return raw.Trim().TrimEnd(',', '.', '!', '?', ';');
    }

    private AiChatResponse BuildBusSearchResponse(
        string from,
        string to,
        List<AiBusCardDto> buses,
        TimeSpan? depAfter,
        string lang)
    {
        var count = buses.Count;
        var timeHint = depAfter.HasValue ? $" (डिपार्चर {depAfter.Value:hh\\:mm} के बाद)" : "";
        var timeHintEn = depAfter.HasValue ? $" after {depAfter.Value:hh\\:mm}" : "";

        string reply;
        if (lang == "hi")
        {
            reply = $"नमस्ते! 😊 **{from}** से **{to}** जाने के लिए{timeHint} हमें **{count}** बसें मिली हैं:\n\n" +
                    string.Join("\n", buses.Take(3).Select((b, idx) =>
                        $"{idx + 1}. 🚌 **{b.DepartureTime}** — {b.BusName} ({b.OperatorName}) • किराया: ₹{b.EstimatedFare} • {b.DelayStatus}")) +
                    (count > 3 ? $"\n\n*(और {count - 3} बसें नीचे कार्ड्स में उपलब्ध हैं)*" : "") +
                    "\n\nआप नीचे दिए गए कार्ड पर **'Track Live'** दबाकर बस की वास्तविक लोकेशन देख सकते हैं।";
        }
        else
        {
            reply = $"Hello! We found **{count}** buses running from **{from}** to **{to}**{timeHintEn}:\n\n" +
                    string.Join("\n", buses.Take(3).Select((b, idx) =>
                        $"{idx + 1}. 🚌 **{b.DepartureTime}** — {b.BusName} ({b.OperatorName}) • Fare: ₹{b.EstimatedFare} • {b.DelayStatus}")) +
                    (count > 3 ? $"\n\n*(Plus {count - 3} more departures shown below)*" : "") +
                    "\n\nTap **'Track Live'** on any card below to view real-time GPS location and stops.";
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "bus_search",
            FunctionCalled: "search_buses",
            Buses: buses,
            QuickReplies: new List<string>
            {
                $"{from} से {to} की पहली बस",
                $"{from} bus stand timetable",
                $"{to} bus stand timetable"
            }
        );
    }

    private AiChatResponse BuildJourneyPlanResponse(
        string from,
        string to,
        MultiRoutePlanDto plan,
        string lang)
    {
        var totalOptions = plan.AllOptions.Count;
        string reply;

        if (totalOptions == 0)
        {
            return BuildNoBusesResponse(from, to, null, lang);
        }

        var firstOpt = plan.AllOptions[0];
        if (lang == "hi")
        {
            if (firstOpt.IsDirect)
            {
                reply = $"**{from}** से **{to}** के लिए सीधी बस उपलब्ध है!\n\n" +
                        $"• प्रस्थान: **{firstOpt.DepartureTime}**\n• आगमन: **{firstOpt.ArrivalTime}**\n• कुल समय: **{firstOpt.TotalJourneyMinutes} मिनट**\n• अनुमानित किराया: **₹{firstOpt.EstimatedTotalFare}**";
            }
            else
            {
                var transfers = string.Join(" ➔ ", firstOpt.TransferStops);
                reply = $"**{from}** से **{to}** के लिए कनेक्टिंग बस रूट मिला है (वाया {transfers}):\n\n" +
                        $"• प्रस्थान: **{firstOpt.DepartureTime}**\n• कुल समय: **{firstOpt.TotalJourneyMinutes} मिनट** (लेओवर: {firstOpt.TotalWaitMinutes} मिनट)\n• अनुमानित किराया: **₹{firstOpt.EstimatedTotalFare}**\n\n" +
                        "नीचे मल्टी-रूट कार्ड में दोनों बसों के समय और स्टॉप देखें।";
            }
        }
        else
        {
            if (firstOpt.IsDirect)
            {
                reply = $"Direct bus available from **{from}** to **{to}**!\n\n" +
                        $"• Departure: **{firstOpt.DepartureTime}**\n• Arrival: **{firstOpt.ArrivalTime}**\n• Duration: **{firstOpt.TotalJourneyMinutes} mins**\n• Estimated Fare: **₹{firstOpt.EstimatedTotalFare}**";
            }
            else
            {
                var transfers = string.Join(" ➔ ", firstOpt.TransferStops);
                reply = $"Connecting bus journey found from **{from}** to **{to}** via **{transfers}**:\n\n" +
                        $"• Departure: **{firstOpt.DepartureTime}**\n• Total Time: **{firstOpt.TotalJourneyMinutes} mins** (Layover: {firstOpt.TotalWaitMinutes} mins)\n• Estimated Fare: **₹{firstOpt.EstimatedTotalFare}**\n\n" +
                        "See the connecting legs details below.";
            }
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "journey_plan",
            FunctionCalled: "plan_journey",
            JourneyPlan: plan,
            QuickReplies: new List<string>
            {
                $"{from} bus stand live board",
                $"Live status of Demo-22-R-2202"
            }
        );
    }

    private AiChatResponse BuildLiveStatusResponse(string busNumber, AiLiveStatusDto? status, string lang)
    {
        if (status == null)
        {
            var notFoundReply = lang == "hi"
                ? $"बस **{busNumber}** का लाइव GPS डेटा अभी उपलब्ध नहीं है। कृपया बस नंबर जांचें (उदा. 'Demo-22-R-2202' या '102')।"
                : $"Live GPS tracking is currently not available for bus **{busNumber}**. Please verify the bus fleet or registration number.";

            return new AiChatResponse(
                Reply: notFoundReply,
                Language: lang,
                Intent: "live_status",
                QuickReplies: new List<string> { "Demo-22-R-2202 live status", "Fatehabad stand timetable" }
            );
        }

        string reply;
        var speedText = status.SpeedKmph.HasValue ? $"{status.SpeedKmph.Value:F0} km/h" : "Stationary";
        var delayText = status.DelayMinutes.GetValueOrDefault() > 0 ? $"{status.DelayMinutes} min delay" : "On Time";

        if (lang == "hi")
        {
            reply = $"📍 **बस {status.FleetNumber} ({status.BusName}) की लाइव स्थिति:**\n\n" +
                    $"• **रूट:** {status.RouteName}\n" +
                    $"• **वर्तमान स्थान:** {status.CurrentLocation ?? "रूट पर"}\n" +
                    $"• **अगला स्टॉप:** {status.NextStop ?? "गंतव्य"} (ETA: {status.EtaMinutes ?? 0} मिनट)\n" +
                    $"• **गति:** {speedText}\n" +
                    $"• **स्थिति:** {delayText}\n\n" +
                    "नीचे दिए गए कार्ड पर **'Open Live Map'** दबाकर पूरे रूट पर बस की गति देखें।";
        }
        else
        {
            reply = $"📍 **Live Status for Bus {status.FleetNumber} ({status.BusName}):**\n\n" +
                    $"• **Route:** {status.RouteName}\n" +
                    $"• **Current Location:** {status.CurrentLocation ?? "In transit"}\n" +
                    $"• **Next Stop:** {status.NextStop ?? "Destination"} (ETA: {status.EtaMinutes ?? 0} min)\n" +
                    $"• **Speed:** {speedText}\n" +
                    $"• **Status:** {delayText}\n\n" +
                    "Tap **'Open Live Map'** below to view real-time location on the map.";
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "live_status",
            FunctionCalled: "get_live_status",
            LiveStatus: status,
            QuickReplies: new List<string> { $"{status.OriginStop} to {status.DestinationStop}", "Stop arrival reminders" }
        );
    }

    private AiChatResponse BuildTimetableResponse(string stopName, StopTimetableDto? timetable, string lang)
    {
        if (timetable == null)
        {
            var notFound = lang == "hi"
                ? $"बस स्टैंड **{stopName}** की समय-सारणी नहीं मिल सकी। कृपया स्टैंड का नाम जांचें (उदा. 'Fatehabad Bus Stand', 'Hisar', 'Bhuna')।"
                : $"Timetable for stop **{stopName}** could not be found. Please check the stop name (e.g. 'Fatehabad Bus Stand', 'Hisar', 'Bhuna').";

            return new AiChatResponse(
                Reply: notFound,
                Language: lang,
                Intent: "stop_timetable",
                QuickReplies: new List<string> { "Fatehabad Bus Stand timetable", "Hisar Bus Stand timetable" }
            );
        }

        var upcomingCount = timetable.UpcomingBuses.Count;
        string reply;

        if (lang == "hi")
        {
            reply = $"🚏 **{timetable.Stop.Name} ({timetable.Stop.Code})** का लाइव डिपार्चर बोर्ड:\n\n" +
                    $"• कुल आगामी बसें: **{upcomingCount}**\n" +
                    $"• सक्रिय रूट: **{timetable.Routes.Count}**\n\n" +
                    string.Join("\n", timetable.UpcomingBuses.Take(4).Select((a, i) =>
                        $"{i + 1}. ⏰ **{a.ScheduledTime}** ➔ **{a.DestinationStop}** ({a.OperatorName} • {a.Status})")) +
                    "\n\nपूरा बोर्ड देखने या QR पोस्टर डाउनलोड करने के लिए नीचे दिए गए कार्ड का उपयोग करें।";
        }
        else
        {
            reply = $"🚏 **Live Departure Board for {timetable.Stop.Name} ({timetable.Stop.Code}):**\n\n" +
                    $"• Upcoming departures: **{upcomingCount}**\n" +
                    $"• Active routes served: **{timetable.Routes.Count}**\n\n" +
                    string.Join("\n", timetable.UpcomingBuses.Take(4).Select((a, i) =>
                        $"{i + 1}. ⏰ **{a.ScheduledTime}** ➔ **{a.DestinationStop}** ({a.OperatorName} • {a.Status})")) +
                    "\n\nView the full timetable or printable QR poster below.";
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "stop_timetable",
            FunctionCalled: "get_stop_timetable",
            Timetable: timetable,
            QuickReplies: new List<string>
            {
                $"{timetable.Stop.Name} QR poster",
                "Hisar to Fatehabad next bus"
            }
        );
    }

    private AiChatResponse BuildAdminAnalyticsResponse(AiAdminAnalyticsDto analytics, string lang)
    {
        string reply;
        if (lang == "hi")
        {
            reply = $"📊 **एडमिन एनालिटिक्स और डेटा सत्यापन रिपोर्ट:**\n\n" +
                    $"• **सक्रिय बसें:** {analytics.ActiveBuses} / {analytics.TotalBuses}\n" +
                    $"• **सक्रिय रूट:** {analytics.ActiveRoutes} / {analytics.TotalRoutes}\n" +
                    $"• **आज के कुल ट्रिप:** {analytics.TotalTripsToday} (वर्तमान में चल रहे: {analytics.ActiveTripsNow})\n" +
                    $"• **विलंबित बसें (>10m):** {analytics.DelayedBusesCount}\n" +
                    (analytics.RoutesWithoutSchedules.Count > 0
                        ? $"\n⚠️ **शेड्यूल रहित रूट ({analytics.RoutesWithoutSchedules.Count}):**\n" + string.Join("\n", analytics.RoutesWithoutSchedules.Take(3).Select(r => $"  - {r}"))
                        : "\n✅ सभी सक्रिय रूटों पर शेड्यूल उपलब्ध हैं।");
        }
        else
        {
            reply = $"📊 **Admin Operations & Data Verification Summary:**\n\n" +
                    $"• **Active Fleet:** {analytics.ActiveBuses} of {analytics.TotalBuses} buses active\n" +
                    $"• **Active Routes:** {analytics.ActiveRoutes} of {analytics.TotalRoutes} routes with {analytics.TotalSchedules} schedules\n" +
                    $"• **Today's Trips:** {analytics.TotalTripsToday} scheduled ({analytics.ActiveTripsNow} currently on road)\n" +
                    $"• **Delayed Buses (>10m):** {analytics.DelayedBusesCount}\n" +
                    (analytics.RoutesWithoutSchedules.Count > 0
                        ? $"\n⚠️ **Routes without Active Schedules ({analytics.RoutesWithoutSchedules.Count}):**\n" + string.Join("\n", analytics.RoutesWithoutSchedules.Take(3).Select(r => $"  - {r}"))
                        : "\n✅ All active routes have scheduled services.");
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "admin_analytics",
            FunctionCalled: "get_admin_analytics",
            AdminSummary: analytics,
            QuickReplies: new List<string>
            {
                "Missing schedules",
                "Inactive buses list",
                "Trip delays"
            }
        );
    }

    private AiChatResponse BuildNoBusesResponse(string from, string to, TimeSpan? depAfter, string lang)
    {
        string reply;
        if (lang == "hi")
        {
            reply = $"क्षमा करें, **{from}** से **{to}** के बीच कोई सीधी बस नहीं मिली।\n\n" +
                    "सुझाव:\n" +
                    "• समय बदल कर देखें (उदा. 'सुबह 8 बजे के बाद')\n" +
                    "• कनेक्टिंग यात्रा खोजें (उदा. 'Bhuna to Delhi connecting via Hisar')\n" +
                    "• बस स्टैंड नंबर देखने के लिए 'Bus stand helpline' पूछें।";
        }
        else
        {
            reply = $"Sorry, no direct buses were found running from **{from}** to **{to}**.\n\n" +
                    "Suggestions:\n" +
                    "• Try adjusting the time window\n" +
                    "• Ask for connecting journeys (e.g. 'Bhuna to Delhi via Hisar')\n" +
                    "• Check station enquiry desk numbers.";
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "bus_search",
            QuickReplies: new List<string>
            {
                $"{from} bus stand timetable",
                $"Connecting buses from {from} to {to}",
                "Bus stand helpline numbers"
            }
        );
    }

    private AiChatResponse BuildGeneralHelpResponse(string lang)
    {
        string reply;
        if (lang == "hi")
        {
            reply = "नमस्ते! 🙏 मैं आपका **AI बस सहायक** हूँ।\n\n" +
                    "आप मुझसे प्राकृतिक हिंदी या अंग्रेजी में पूछ सकते हैं:\n" +
                    "• *'मुझे भुना से दिल्ली जाना है, सुबह 8 बजे के बाद कौन सी बस मिलेगी?'*\n" +
                    "• *'हिसार से फतेहाबाद की अगली बस कब है?'*\n" +
                    "• *'Demo-22-R-2202 का लाइव स्टेटस दिखाओ'*\n" +
                    "• *'फतेहाबाद बस स्टैंड का टाइमटेबल'*\n" +
                    "• *'भुना से दिल्ली कनेक्टिंग बस रूट'*\n\n" +
                    "आप माइक बटन दबाकर बोल भी सकते हैं! 🎙️";
        }
        else
        {
            reply = "Hello! 👋 I am your **AI Bus Travel Assistant**.\n\n" +
                    "You can ask me anything in English or Hindi:\n" +
                    "• *'Buses from Bhuna to Delhi departing after 8 AM'*\n" +
                    "• *'Hisar to Fatehabad next bus'*\n" +
                    "• *'Live status of bus Demo-22-R-2202'*\n" +
                    "• *'Fatehabad bus stand live timetable'*\n" +
                    "• *'Connecting bus routes from Bhuna to Delhi via Hisar'*\n\n" +
                    "You can also tap the microphone icon to speak! 🎙️";
        }

        return new AiChatResponse(
            Reply: reply,
            Language: lang,
            Intent: "general",
            QuickReplies: new List<string>
            {
                "भुना से दिल्ली (सुबह 8 बजे के बाद)",
                "Hisar to Fatehabad next bus",
                "Fatehabad bus stand timetable",
                "Live status Demo-22-R-2202"
            }
        );
    }

    public Task<List<string>> GetSmartSuggestionsAsync(string lang = "hi", CancellationToken ct = default)
    {
        List<string> suggestions;
        if (lang == "hi")
        {
            suggestions = new List<string>
            {
                "भुना से दिल्ली (सुबह 8 बजे के बाद)",
                "हिसार से फतेहाबाद की अगली बस",
                "भुना से हिसार बसें",
                "फतेहाबाद बस स्टैंड लाइव टाइमटेबल",
                "Demo-22-R-2202 का लाइव स्टेटस",
                "भुना से दिल्ली कनेक्टिंग यात्रा"
            };
        }
        else
        {
            suggestions = new List<string>
            {
                "Buses from Bhuna to Delhi after 8:00 AM",
                "Hisar to Fatehabad next bus",
                "Live timetable for Fatehabad Bus Stand",
                "Live status of bus Demo-22-R-2202",
                "Connecting buses: Bhuna to Delhi via Hisar",
                "Bus stand enquiry numbers"
            };
        }
        return Task.FromResult(suggestions);
    }

    public async Task<AiAdminAnalyticsDto> GetAdminAnalyticsAsync(CancellationToken ct = default)
    {
        return await _getAdminAnalytics.ExecuteAsync(ct);
    }
}
