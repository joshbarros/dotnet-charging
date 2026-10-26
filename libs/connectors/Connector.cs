namespace Charging.Connectors;

public class Connector
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ConnectorType { get; set; } = "";
    public string City { get; set; } = "";
    public DateTimeOffset ServiceDate { get; set; }
    public long PriceCents { get; set; }
    public int SlotsTotal { get; set; }
    public int SlotsLeft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class SlotHold
{
    public Guid ReservationId { get; set; }
    public Guid ConnectorId { get; set; }
    public int Slots { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
