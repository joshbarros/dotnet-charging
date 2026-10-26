using Charging.Api.Data;
using Charging.Api.Messaging;
using Charging.Api.Modules.Connectors;
using Charging.Api.Modules.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Charging.Api.Modules.Reservations;

public enum ReservationStatus { Requested, Held, Booked, Cancelled, Rejected }

public class Reservation
{
    public Guid Id { get; set; }
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

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateReservationRequest(string? ConnectorCode, string? Reference, long Slots);

public record ReservationDto(
    Guid Id, string ConnectorCode, string Reference, int Slots, long TotalCents, string Status,
    string? Reason, string? TokenNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SlotsReleasedAt)
{
    public static ReservationDto From(Reservation b) => new(
        b.Id, b.ConnectorCode, b.Reference, b.Slots, b.TotalCents, b.Status.ToString(),
        b.Reason, b.TokenNumber, b.CreatedAt, b.UpdatedAt, b.SlotsReleasedAt);
}

/// <summary>HTTP side of the saga: writes the reservation and its first outbox row in one local transaction.</summary>
public class ReservationService(ChargingDbContext db)
{
    public async Task<Reservation> CreateAsync(CreateReservationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectorCode))
            throw new DomainException(400, "Connector code is required.");
        var error = ReservationRules.Validate(request.Reference, request.Slots);
        if (error is not null)
            throw new DomainException(400, error);

        var connector = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.ConnectorCode.Trim(), ct)
            ?? throw new DomainException(404, $"Connector '{request.ConnectorCode}' does not exist.");

        var slots = (int)request.Slots;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            ConnectorId = connector.Id,
            ConnectorCode = connector.Code,
            Reference = reference,
            Slots = slots,
            TotalCents = ReservationRules.TotalCents(connector.PriceCents, slots),
            Status = ReservationStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Reservations.Add(reservation);
        db.Enqueue("reservations", Topology.ReserveKey, reservation.Id, new ReserveSlots(reservation.Id, connector.Id, slots, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {connector.Code}.");
        }
        await tx.CommitAsync(ct);
        return reservation;
    }

    /// <summary>Moves Held or Booked to Cancelled and asks Connectors to give the slots back. The token is not touched.</summary>
    public async Task<Reservation> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Reservations
            .Where(b => b.Id == id && (b.Status == ReservationStatus.Held || b.Status == ReservationStatus.Booked))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Cancelled)
                .SetProperty(b => b.Reason, "Cancelled by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Reservation not found.");
            throw new DomainException(409, $"A reservation in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("reservations", Topology.ReleaseKey, id, new ReleaseSlots(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Reservations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a reservation forward. Each step runs only from the status it expects.</summary>
public class ReservationsHandler(ChargingDbContext db, IOptions<ReservationOptions> options) : IMessageHandler
{
    public const string Name = "reservations";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<SlotsHeld>(d.Body).ReservationId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<SlotsShort>(d.Body), ct),
        Topology.IssuedKey => MarkBookedAsync(d.MessageId, Json.Parse<TokenIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<TokenRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<SlotsReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Reservations does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid reservationId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == reservationId && b.Status == ReservationStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, SlotsShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && b.Status == ReservationStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedSlots} slots wanted, {m.SlotsLeft} left.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a cancellation got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkBookedAsync(Guid messageId, TokenIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && b.Status == ReservationStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Booked)
                .SetProperty(b => b.TokenNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            reservationIdForOrderCheck: m.ReservationId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, TokenRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && (b.Status == ReservationStatus.Held || b.Status == ReservationStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, SlotsReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && b.SlotsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.SlotsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? reservationIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && reservationIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Reservations.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == ReservationStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
