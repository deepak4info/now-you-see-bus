using Microsoft.AspNetCore.SignalR;

namespace BusTrack.Api.Hubs;

public class TrackingHub : Hub
{
    public Task JoinBus(string fleetNumber) =>
        Groups.AddToGroupAsync(Context.ConnectionId, "bus-" + fleetNumber.Trim());

    public Task JoinLive() =>
        Groups.AddToGroupAsync(Context.ConnectionId, "live");
}
