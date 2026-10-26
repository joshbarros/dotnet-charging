using System.Collections.Concurrent;
using Charging.Api.Messaging;
using Charging.Api.Modules.Connectors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Charging.Api.Tests;

public class ConnectorsConcurrencyTests
{
    [Fact]
    public async Task LastSlot_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, slotsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Reservation, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var reservationId = Guid.CreateVersion7();
            var handler = new ConnectorsHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveSlots(reservationId, connector.Id, 1, $"DRV-{n}"));
            outcomes.Add((reservationId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, slotsTotal: 4);
        var reservationId = Guid.CreateVersion7();
        var handler = new ConnectorsHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSlots(reservationId, connector.Id, 3, "DRV-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same slots back.
        var first = await new ConnectorsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(reservationId, "poison"));
        var second = await new ConnectorsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(reservationId, "cancellation"));

        var remaining = await TestDatabase.Create().Connectors.Where(s => s.Id == connector.Id).Select(s => s.SlotsLeft).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, slotsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSlots(Guid.CreateVersion7(), connector.Id, 2, "DRV-1");

        var first = await new ConnectorsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new ConnectorsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Connectors.Where(s => s.Id == connector.Id).Select(s => s.SlotsLeft).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
