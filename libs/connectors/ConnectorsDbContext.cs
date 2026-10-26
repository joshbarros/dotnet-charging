using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Charging.Connectors;

public class ConnectorsDbContext(DbContextOptions<ConnectorsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "slots";

    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<SlotHold> Holds => Set<SlotHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
