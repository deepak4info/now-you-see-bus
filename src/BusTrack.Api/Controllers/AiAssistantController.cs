using BusTrack.Api.Application.AI.DTOs;
using BusTrack.Api.Application.AI.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BusTrack.Api.Controllers;

[ApiController]
[Route("api/ai")]
public class AiAssistantController : ControllerBase
{
    private readonly IAiAssistantService _aiAssistantService;
    private readonly ILogger<AiAssistantController> _logger;

    public AiAssistantController(IAiAssistantService aiAssistantService, ILogger<AiAssistantController> logger)
    {
        _aiAssistantService = aiAssistantService;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<ActionResult<AiChatResponse>> Chat([FromBody] AiChatRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Message is required" });
        }

        try
        {
            var response = await _aiAssistantService.ProcessChatAsync(request, User, ct);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI chat query: {Message}", request.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new AiChatResponse(
                Reply: "क्षमा करें, आपके अनुरोध को प्रोसेस करते समय एक त्रुटि आई। कृपया पुनः प्रयास करें।",
                Language: request.Language ?? "hi",
                Intent: "error",
                Success: false,
                ErrorMessage: ex.Message
            ));
        }
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<string>>> GetSuggestions([FromQuery] string lang = "hi", CancellationToken ct = default)
    {
        var suggestions = await _aiAssistantService.GetSmartSuggestionsAsync(lang, ct);
        return Ok(suggestions);
    }

    [HttpGet("admin-analytics")]
    public async Task<ActionResult<AiAdminAnalyticsDto>> GetAdminAnalytics(CancellationToken ct)
    {
        var analytics = await _aiAssistantService.GetAdminAnalyticsAsync(ct);
        return Ok(analytics);
    }
}
