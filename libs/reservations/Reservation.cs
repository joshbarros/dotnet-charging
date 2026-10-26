namespace Charging.Reservations;

public enum ReservationStatus { Requested, Held, Booked, Cancelled, Rejected }

public class Reservation
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid ConnectorId { get; set; }
    public string ConnectorCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Slots { get; set; }
    public long TotalCents { get; set; }
    public ReservationStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? TokenNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SlotsReleasedAt { get; set; }
}

public class ReservationOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateReservationRequest(string? ConnectorCode, string? Reference, long Slots);

public record ReservationDto(
    Guid Id, Guid OwnerId, string ConnectorCode, string Reference, int Slots, long TotalCents, string Status,
    string? Reason, string? TokenNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SlotsReleasedAt)
{
    public static ReservationDto From(Reservation b) => new(
        b.Id, b.OwnerId, b.ConnectorCode, b.Reference, b.Slots, b.TotalCents, b.Status.ToString(),
        b.Reason, b.TokenNumber, b.CreatedAt, b.UpdatedAt, b.SlotsReleasedAt);
}
