using Charging.Shared;
using Charging.Shared.Auth;
using Charging.Shared.Contracts;
using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Charging.Reservations;

/// <summary>HTTP side of the saga: writes the reservation and its first outbox row in one local transaction.</summary>
public class ReservationService(ReservationsDbContext db, IConnectorCatalog connectors, ICurrentUser user)
{
    public async Task<Reservation> CreateAsync(CreateReservationRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.ConnectorCode))
            throw new DomainException(400, "Connector code is required.");
        var error = ReservationRules.Validate(request.Reference, request.Slots);
        if (error is not null)
            throw new DomainException(400, error);

        var connector = await connectors.FindAsync(request.ConnectorCode.Trim(), ct)
            ?? throw new DomainException(404, $"Connector '{request.ConnectorCode}' does not exist.");

        var slots = (int)request.Slots;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("reservations", Topology.ReserveKey, reservation.Id, new ReserveSlots(reservation.Id, ownerId, connector.Id, slots, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Reservation> GetAsync(Guid id, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (reservation is null || !OwnerAccess.CanAccess(user, reservation.OwnerId))
            throw new DomainException(404, "Reservation not found.");
        return reservation;
    }

    public async Task<(List<Reservation> Items, int Total)> ListAsync(string? connectorCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Reservations.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see reservations.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(connectorCode))
            query = query.Where(b => b.ConnectorCode == connectorCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Booked to Cancelled and asks Connectors to give the slots back. The token is not touched.</summary>
    public async Task<Reservation> CancelAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Reservations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A reservation in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("reservations", Topology.ReleaseKey, id, new ReleaseSlots(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Reservations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
