using System;
using System.Collections.Generic;

namespace ImajinationAPI.Models
{
    public class TicketTierDto
    {
        public Guid? id { get; set; }
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public string? color { get; set; }   // hex accent e.g. "#e53e3e"
        public decimal price { get; set; }
        public int totalSlots { get; set; }
        public int slotsSold { get; set; }
        public int sortOrder { get; set; }
    }

    public class TalentLineupItemDto
    {
        public Guid id { get; set; }
        public string displayName { get; set; } = string.Empty;
        public string role { get; set; } = string.Empty;
        public string? profilePicture { get; set; }
    }

    public class CreateEventDto
    {
        public Guid organizerId { get; set; }
        public string title { get; set; } = string.Empty;
        public string artists { get; set; } = string.Empty;
        public string description { get; set; } = string.Empty;
        public DateTime time { get; set; }

        public string city { get; set; } = string.Empty;
        public string location { get; set; } = string.Empty;

        public string? posterUrl { get; set; }
        public decimal price { get; set; }
        public int slots { get; set; }
        public int? maxTicketsPerCustomer { get; set; }

        public string? eventType { get; set; }
        public string? genres { get; set; }

        public string? tierName { get; set; }
        public decimal? tierPrice { get; set; }
        public int? tierSlots { get; set; }
        public string? bundles { get; set; }
        public string? discounts { get; set; }
        public string? sponsors { get; set; }
        public string? saleName { get; set; }
        public string? saleType { get; set; }
        public decimal? saleValue { get; set; }
        public DateTime? saleStartsAt { get; set; }
        public DateTime? saleEndsAt { get; set; }
        public int? saleQuantityLimit { get; set; }
        public string? status { get; set; }
        public List<TalentLineupItemDto>? artistLineup { get; set; }
        public List<TalentLineupItemDto>? sessionistLineup { get; set; }
        public List<TicketTierDto>? tiers { get; set; }
    }

    public class EventDto
    {
        public Guid id { get; set; }
        public string title { get; set; } = string.Empty;
        public DateTime time { get; set; }

        public string city { get; set; } = string.Empty;
        public string location { get; set; } = string.Empty;

        public decimal price { get; set; }
        public int slots { get; set; }
        public int ticketsSold { get; set; }
        public int maxTicketsPerCustomer { get; set; }
        public int attendedTickets { get; set; }
        public string status { get; set; } = string.Empty;
        public string? posterUrl { get; set; }
        public string? eventType { get; set; }
        public string? genres { get; set; }
        public string? saleName { get; set; }
        public string? saleType { get; set; }
        public decimal? saleValue { get; set; }
        public DateTime? saleStartsAt { get; set; }
        public DateTime? saleEndsAt { get; set; }
        public List<TalentLineupItemDto> artistLineup { get; set; } = new();
        public List<TalentLineupItemDto> sessionistLineup { get; set; } = new();
        public List<TicketTierDto> tiers { get; set; } = new();
        public string? description { get; set; }
        public int? saleQuantityLimit { get; set; }
        public int? saleQuantityUsed { get; set; }
    }
}
