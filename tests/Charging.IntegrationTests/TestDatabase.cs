using Charging.Reservations;
using Charging.Connectors;
using Charging.Tokens;
using Charging.Shared.Auth;
using Charging.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Charging.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CHARGING_TEST_DB")
        ?? "Host=localhost;Port=5479;Database=charging_test;Username=charging;Password=charging";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static ReservationsDbContext Reservations() => new(Options<ReservationsDbContext>(ReservationsDbContext.SchemaName));
    public static ConnectorsDbContext Connectors() => new(Options<ConnectorsDbContext>(ConnectorsDbContext.SchemaName));
    public static TokensDbContext Tokens() => new(Options<TokensDbContext>(TokensDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var reservations = Reservations();
        await using var slots = Connectors();
        await using var tokens = Tokens();
        await reservations.Database.MigrateAsync();
        await slots.Database.MigrateAsync();
        await tokens.Database.MigrateAsync();
        await reservations.Database.ExecuteSqlRawAsync(
            "TRUNCATE reservations.\"InboxMessages\", reservations.\"OutboxMessages\", reservations.\"Reservations\", " +
            "slots.\"InboxMessages\", slots.\"OutboxMessages\", slots.\"SlotHolds\", slots.\"Connectors\", " +
            "tokens.\"InboxMessages\", tokens.\"OutboxMessages\", tokens.\"Tokens\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Connector> AddConnectorAsync(int slotsTotal, long priceCents = 5_000)
    {
        await using var db = Connectors();
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

    public static async Task<Reservation> AddReservationAsync(Connector connector, string reference, int slots, ReservationStatus status, Guid? ownerId = null)
    {
        await using var db = Reservations();
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Connector[] connectors) : IConnectorCatalog
{
    public Task<ConnectorInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(connectors.Where(s => s.Code == code).Select(s => new ConnectorInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
