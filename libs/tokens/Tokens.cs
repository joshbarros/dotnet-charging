using Charging.Shared;
using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Charging.Tokens;

public enum TokenStatus { Issued, Rejected }

public class Token
{
    public Guid ReservationId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public TokenStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class TokensDbContext(DbContextOptions<TokensDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "tokens";

    public DbSet<Token> Tokens => Set<Token>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Token>(e =>
        {
            e.ToTable("Tokens");
            e.HasKey(x => x.ReservationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the token once the slots are held. A poison reference is committed as rejected.</summary>
public class TokensHandler(TokensDbContext db) : IMessageHandler
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
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Tokens.Add(new Token
            {
                ReservationId = held.ReservationId,
                OwnerId = held.OwnerId,
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
                OwnerId = held.OwnerId,
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
