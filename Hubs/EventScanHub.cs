using Microsoft.AspNetCore.SignalR;

namespace ImajinationAPI.Hubs
{
    /// <summary>
    /// Real-time hub for organizer ticket scanning dashboards.
    /// Clients join a group per eventId. When the scan endpoint fires,
    /// it broadcasts to the group so all connected dashboards update live.
    /// </summary>
    public class EventScanHub : Hub
    {
        /// <summary>Called by the dashboard client to subscribe to scan updates for a specific event.</summary>
        public async Task JoinEventGroup(string eventId)
        {
            if (Guid.TryParse(eventId, out _))
                await Groups.AddToGroupAsync(Context.ConnectionId, $"event-{eventId}");
        }

        /// <summary>Called by the dashboard client to unsubscribe.</summary>
        public async Task LeaveEventGroup(string eventId)
        {
            if (Guid.TryParse(eventId, out _))
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"event-{eventId}");
        }
    }

    /// <summary>Typed helper injected into TicketController to push scan events.</summary>
    public class EventScanBroadcaster
    {
        private readonly IHubContext<EventScanHub> _hub;

        public EventScanBroadcaster(IHubContext<EventScanHub> hub)
        {
            _hub = hub;
        }

        public async Task BroadcastScanAsync(string eventId, object payload)
        {
            await _hub.Clients.Group($"event-{eventId}").SendAsync("TicketScanned", payload);
        }

        public async Task BroadcastTicketSaleAsync(string eventId, object payload)
        {
            await _hub.Clients.Group($"event-{eventId}").SendAsync("TicketSold", payload);
        }
    }
}
