using Charging.Api.Data;
using Charging.Api.Modules.Reservations;
using Charging.Api.Modules.Connectors;
using Microsoft.EntityFrameworkCore;

namespace Charging.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CHARGING_TEST_DB")
        ?? "Host=localhost;Port=5479;Database=charging_test;Username=charging;Password=charging";

    private static readonly DbContextOptions<ChargingDbContext> Options =
        new DbContextOptionsBuilder<ChargingDbContext>().UseNpgsql(ConnectionString).Options;

    public static ChargingDbContext Create() => new(Options);

    public static async Task<ChargingDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"Tokens\", \"SlotHolds\", \"Reservations\", \"Connectors\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Connector> AddConnectorAsync(ChargingDbContext db, int slotsTotal, long priceCents = 1_500)
    {
        var connector = new Connector
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Project",
            ConnectorType = "CCS2",
            City = "Lisbon",
            ServiceDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            SlotsTotal = slotsTotal,
            SlotsLeft = slotsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();
        return connector;
    }

    public static async Task<Reservation> AddReservationAsync(ChargingDbContext db, Connector connector, string reference, int slots, ReservationStatus status)
    {
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            ConnectorId = connector.Id,
            ConnectorCode = connector.Code,
            Reference = reference,
            Slots = slots,
            TotalCents = ReservationRules.TotalCents(connector.PriceCents, slots),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();
        return reservation;
    }
}
