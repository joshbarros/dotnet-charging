using Charging.Api.Data;
using Charging.Api.Messaging;
using Charging.Api.Modules.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Charging.Api.Modules.Tokens;

public enum TokenStatus { Issued, Rejected }

public class Token
{
    public Guid ReservationId { get; set; }
    public string? Number { get; set; }
    public TokenStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the token once the slots are held. A poison reference is committed as rejected.</summary>
public class TokensHandler(ChargingDbContext db) : IMessageHandler
{
    public const string Name = "tokens";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Tokens does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<SlotsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, SlotsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (ReservationRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Tokens.Add(new Token
            {
                ReservationId = held.ReservationId,
                Status = TokenStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("tokens", Topology.ReleaseKey, held.ReservationId, new ReleaseSlots(held.ReservationId, "poison"));
            db.Enqueue("tokens", Topology.RejectedKey, held.ReservationId, new TokenRejected(held.ReservationId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"CT-{held.ReservationId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Tokens.Add(new Token
            {
                ReservationId = held.ReservationId,
                Number = number,
                Status = TokenStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("tokens", Topology.IssuedKey, held.ReservationId, new TokenIssued(held.ReservationId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
