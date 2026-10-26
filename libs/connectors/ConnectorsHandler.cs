using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Charging.Connectors;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Connector.SlotsLeft and of slot holds.
/// The reservation is one conditional UPDATE: it matches only while the remaining Slots still covers the request.
/// </summary>
public class ConnectorsHandler(ConnectorsDbContext db) : IMessageHandler
{
    public const string Name = "connectors";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveSlots>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseSlots>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Connectors does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveSlots command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Connectors
            .Where(s => s.Id == command.ConnectorId && s.SlotsLeft >= command.Slots)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SlotsLeft, x => x.SlotsLeft - command.Slots), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new SlotHold
            {
                ReservationId = command.ReservationId,
                ConnectorId = command.ConnectorId,
                Slots = command.Slots,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("connectors", Topology.HeldKey, command.ReservationId,
                new SlotsHeld(command.ReservationId, command.OwnerId, command.ConnectorId, command.Slots, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Connectors.Where(s => s.Id == command.ConnectorId)
                .Select(s => s.SlotsLeft).FirstOrDefaultAsync(ct);
            db.Enqueue("connectors", Topology.ShortKey, command.ReservationId,
                new SlotsShort(command.ReservationId, command.Reference, command.Slots, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held slots back exactly once. A second release for the same reservation changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseSlots command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.ReservationId == command.ReservationId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.ReservationId == command.ReservationId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Connectors
                    .Where(s => s.Id == hold.ConnectorId && s.SlotsLeft + hold.Slots <= s.SlotsTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SlotsLeft, x => x.SlotsLeft + hold.Slots), ct);
                db.Enqueue("connectors", Topology.ReleasedKey, command.ReservationId,
                    new SlotsReleased(command.ReservationId, hold.Slots, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
