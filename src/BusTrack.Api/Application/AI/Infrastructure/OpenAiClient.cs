using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BusTrack.Api.Application.AI.Infrastructure;

public record OpenAiToolCall(string Id, string FunctionName, JsonElement Arguments);

public record OpenAiCompletionResult(
    string? MessageContent,
    List<OpenAiToolCall> ToolCalls,
    bool HasToolCalls
);

public class OpenAiClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<OpenAiClient> _logger;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;

    public OpenAiClient(HttpClient http, IConfiguration config, ILogger<OpenAiClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _apiKey = config["OpenAi:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _model = config["OpenAi:Model"] ?? "gpt-4o-mini";
        _baseUrl = (config["OpenAi:BaseUrl"] ?? "https://api.openai.com/v1").TrimEnd('/');
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<OpenAiCompletionResult?> CallChatAsync(
        JsonArray messages,
        JsonArray tools,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        try
        {
            var requestBody = new JsonObject
            {
                ["model"] = _model,
                ["messages"] = messages,
                ["tools"] = tools,
                ["tool_choice"] = "auto",
                ["temperature"] = 0.2
            };

            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!.Trim());

            var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI API call returned status {Status}: {Error}", res.StatusCode, err);
                return null;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var firstChoice = choices[0];
            var msg = firstChoice.GetProperty("message");

            string? content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var toolCalls = new List<OpenAiToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcArray.EnumerateArray())
                {
                    var id = tc.GetProperty("id").GetString() ?? "";
                    var fn = tc.GetProperty("function");
                    var name = fn.GetProperty("name").GetString() ?? "";
                    var argsRaw = fn.GetProperty("arguments").GetString() ?? "{}";

                    using var argsDoc = JsonDocument.Parse(argsRaw);
                    toolCalls.Add(new OpenAiToolCall(id, name, argsDoc.RootElement.Clone()));
                }
            }

            return new OpenAiCompletionResult(content, toolCalls, toolCalls.Count > 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during OpenAI API call");
            return null;
        }
    }

    public static JsonArray GetStandardTools()
    {
        var tools = new JsonArray();

        // 1. search_buses
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "search_buses",
                ["description"] = "Search scheduled and live buses between two bus stands or cities. Use when passenger asks for buses between source and destination.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["from"] = new JsonObject { ["type"] = "string", ["description"] = "Origin bus stand/city name (e.g. Bhuna, Hisar, Delhi, Fatehabad)" },
                        ["to"] = new JsonObject { ["type"] = "string", ["description"] = "Destination bus stand/city name" },
                        ["departure_after"] = new JsonObject { ["type"] = "string", ["description"] = "Optional earliest departure time in HH:mm (24h) format, e.g. '08:00'" },
                        ["departure_before"] = new JsonObject { ["type"] = "string", ["description"] = "Optional latest departure time in HH:mm format (e.g. for last bus or morning bus)" },
                        ["date"] = new JsonObject { ["type"] = "string", ["description"] = "Travel date in YYYY-MM-DD format (defaults to today)" }
                    },
                    ["required"] = new JsonArray { "from", "to" }
                }
            }
        });

        // 2. plan_journey (Multi-route connecting buses)
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "plan_journey",
                ["description"] = "Plan journeys between cities including connecting buses and transfers (e.g. Bhuna -> Hisar -> Delhi). Use when no direct bus exists or when asked for connecting routes.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["from"] = new JsonObject { ["type"] = "string", ["description"] = "Origin city/bus stand" },
                        ["to"] = new JsonObject { ["type"] = "string", ["description"] = "Final destination city/bus stand" },
                        ["date"] = new JsonObject { ["type"] = "string", ["description"] = "Travel date YYYY-MM-DD" }
                    },
                    ["required"] = new JsonArray { "from", "to" }
                }
            }
        });

        // 3. get_live_status
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "get_live_status",
                ["description"] = "Get real-time live GPS tracking, speed, delay, and ETA for a specific bus number or fleet number (e.g. 'Demo-22-R-2202', '102').",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["bus_number"] = new JsonObject { ["type"] = "string", ["description"] = "Fleet number or registration number" }
                    },
                    ["required"] = new JsonArray { "bus_number" }
                }
            }
        });

        // 4. get_stop_timetable
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "get_stop_timetable",
                ["description"] = "Get complete live departures board, routes, and timings for a specific bus stand or stop (e.g. 'Fatehabad Bus Stand', 'FTB', 'Hisar Bus Stand').",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["stop_code_or_name"] = new JsonObject { ["type"] = "string", ["description"] = "Bus stand name or short code" }
                    },
                    ["required"] = new JsonArray { "stop_code_or_name" }
                }
            }
        });

        // 5. get_admin_analytics
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "get_admin_analytics",
                ["description"] = "Get admin operations overview, missing schedules, inactive buses, and delay metrics.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["category"] = new JsonObject { ["type"] = "string", ["description"] = "Optional category: 'missing_schedules', 'inactive_buses', 'summary'" }
                    }
                }
            }
        });

        return tools;
    }
}
