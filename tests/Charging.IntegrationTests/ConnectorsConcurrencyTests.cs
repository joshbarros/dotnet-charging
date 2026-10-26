using System.Collections.Concurrent;
using Charging.Connectors;
using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Charging.IntegrationTests;

public class ConnectorsConcurrencyTests
{
    [Fact]
    public async Task LastSlot_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(slotsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Reservation, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var reservationId = Guid.CreateVersion7();
            await using var db = TestDatabase.Connectors();
            var outcome = await new ConnectorsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSlots(reservationId, Guid.NewGuid(), connector.Id, 1, $"DRV-{n}"));
            outcomes.Add((reservationId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Connectors();
        var remaining = await check.Connectors.Where(s => s.Id == connector.Id).Select(s => s.SlotsLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Reservation;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.ReservationId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheSlotsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(slotsTotal: 4);
        var reservationId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Connectors())
            await new ConnectorsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveSlots(reservationId, Guid.NewGuid(), connector.Id, 3, "DRV-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same slots back.
        await using var first = TestDatabase.Connectors();
        await using var second = TestDatabase.Connectors();
        var one = await new ConnectorsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(reservationId, "poison"));
        var two = await new ConnectorsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(reservationId, "cancellation"));

        await using var check = TestDatabase.Connectors();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Connectors.Where(s => s.Id == connector.Id).Select(s => s.SlotsLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(slotsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSlots(Guid.CreateVersion7(), Guid.NewGuid(), connector.Id, 2, "DRV-1");

        await using var a = TestDatabase.Connectors();
        await using var b = TestDatabase.Connectors();
        var first = await new ConnectorsHandler(a).ReserveAsync(messageId, command);
        var second = await new ConnectorsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Connectors();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Connectors.Where(s => s.Id == connector.Id).Select(s => s.SlotsLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(slotsTotal: 2);
        await using var db = TestDatabase.Connectors();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Connectors.Where(s => s.Id == connector.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SlotsLeft, x => x.SlotsLeft - 3)));
    }
}
