using Charging.Api.Messaging;
using Charging.Api.Modules.Reservations;
using Charging.Api.Modules.Connectors;
using Charging.Api.Modules.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Charging.Api.Tests;

public class SagaTests
{
    private static ReservationsHandler Reservations() =>
        new(TestDatabase.Create(), Options.Create(new ReservationOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, 5);
        var reservation = await TestDatabase.AddReservationAsync(db, connector, "DRV-POISON-1", 2, ReservationStatus.Held);
        var held = new SlotsHeld(reservation.Id, connector.Id, 2, reservation.Reference);

        var handler = new TokensHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Tokens.SingleAsync(d => d.ReservationId == reservation.Id);
        Assert.Equal(TokenStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesReservationCancelledAndTokenIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, 5);
        var reservation = await TestDatabase.AddReservationAsync(db, connector, "DRV-1", 2, ReservationStatus.Held);

        // The token is issued; the final mark has not landed yet.
        var number = "CT-TEST";
        await new TokensHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new SlotsHeld(reservation.Id, connector.Id, 2, reservation.Reference));

        // The cancellation gets there first.
        await new ReservationService(TestDatabase.Create()).CancelAsync(reservation.Id, default);

        var mark = await Reservations().MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(reservation.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(ReservationStatus.Cancelled, (await check.Reservations.SingleAsync()).Status);
        Assert.Equal(TokenStatus.Issued, (await check.Tokens.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, 5);
        var held = await TestDatabase.AddReservationAsync(db, connector, "DRV-A", 1, ReservationStatus.Held);
        var rejected = await TestDatabase.AddReservationAsync(db, connector, "DRV-B", 1, ReservationStatus.Rejected);
        var requested = await TestDatabase.AddReservationAsync(db, connector, "DRV-C", 1, ReservationStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Reservations().MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(held.Id, "CT-1")));
        Assert.Equal(MarkOutcome.Skipped, await Reservations().MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(rejected.Id, "CT-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Reservations().MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(requested.Id, "CT-3")));

        var statuses = (await TestDatabase.Create().Reservations.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(ReservationStatus.Booked, statuses["DRV-A"]);
        Assert.Equal(ReservationStatus.Rejected, statuses["DRV-B"]);
        Assert.Equal(ReservationStatus.Requested, statuses["DRV-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedReservation_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(db, 5);
        var reservation = await TestDatabase.AddReservationAsync(db, connector, "DRV-1", 1, ReservationStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new ReservationService(TestDatabase.Create()).CancelAsync(reservation.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
