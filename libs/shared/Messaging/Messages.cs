using System.Text.Json;

namespace Charging.Shared.Messaging;

public record ReserveSlots(Guid ReservationId, Guid OwnerId, Guid ConnectorId, int Slots, string Reference);
public record SlotsHeld(Guid ReservationId, Guid OwnerId, Guid ConnectorId, int Slots, string Reference);
public record SlotsShort(Guid ReservationId, string Reference, int RequestedSlots, int SlotsLeft);
public record ReleaseSlots(Guid ReservationId, string Reason);
public record SlotsReleased(Guid ReservationId, int Slots, string Reason);
public record TokenIssued(Guid ReservationId, string Number);
public record TokenRejected(Guid ReservationId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
