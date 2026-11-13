using System.Security.Claims;
using BusTrack.Api.Application.AI.DTOs;

namespace BusTrack.Api.Application.AI.Interfaces;

public interface IAiAssistantService
{
    Task<AiChatResponse> ProcessChatAsync(AiChatRequest request, ClaimsPrincipal? user = null, CancellationToken ct = default);
    Task<List<string>> GetSmartSuggestionsAsync(string lang = "hi", CancellationToken ct = default);
    Task<AiAdminAnalyticsDto> GetAdminAnalyticsAsync(CancellationToken ct = default);
}
