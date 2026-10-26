using Charging.Api.Messaging;
using Charging.Api.Modules.Reservations;
using Charging.Api.Modules.Connectors;
using Charging.Api.Modules.Tokens;
using Microsoft.EntityFrameworkCore;

namespace Charging.Api.Data;

public class ChargingDbContext(DbContextOptions<ChargingDbContext> options) : DbContext(options)
{
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<SlotHold> Holds => Set<SlotHold>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Connector>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Connectors_Remaining", "\"SlotsLeft\" >= 0 AND \"SlotsLeft\" <= \"SlotsTotal\""));
        });

        b.Entity<SlotHold>(e =>
        {
            e.ToTable("SlotHolds");
            e.HasKey(x => x.ReservationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Connector>().WithMany().HasForeignKey(x => x.ConnectorId);
        });

        b.Entity<Reservation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(ReservationRules.MaxReferenceLength);
            e.HasIndex(x => new { x.ConnectorId, x.Reference }).IsUnique();
            e.HasOne<Connector>().WithMany().HasForeignKey(x => x.ConnectorId);
        });

        b.Entity<Token>(e =>
        {
            e.ToTable("Tokens");
            e.HasKey(x => x.ReservationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
