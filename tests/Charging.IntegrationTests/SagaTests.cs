using Charging.Reservations;
using Charging.Tokens;
using Charging.Shared;
using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Charging.IntegrationTests;

public class SagaTests
{
    private static ReservationsHandler Handler(ReservationsDbContext db) => new(db, Options.Create(new ReservationOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var reservation = await TestDatabase.AddReservationAsync(connector, "DRV-POISON-1", 2, ReservationStatus.Held);
        var held = new SlotsHeld(reservation.Id, reservation.OwnerId, connector.Id, 2, reservation.Reference);

        await using var docs = TestDatabase.Tokens();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new TokensHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Tokens();
        var doc = await check.Tokens.SingleAsync(d => d.ReservationId == reservation.Id);
        Assert.Equal(TokenStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(reservation.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesReservationCancelledAndTokenIssued()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var owner = Guid.CreateVersion7();
        var reservation = await TestDatabase.AddReservationAsync(connector, "DRV-1", 2, ReservationStatus.Held, owner);

        // The token is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Tokens())
            await new TokensHandler(docs).IssueAsync(Guid.CreateVersion7(), new SlotsHeld(reservation.Id, owner, connector.Id, 2, reservation.Reference));

        // The owner reverses first.
        await using (var reservations = TestDatabase.Reservations())
            await new ReservationService(reservations, new FixedCatalog(connector), new TestUser(owner)).CancelAsync(reservation.Id, default);

        await using var late = TestDatabase.Reservations();
        var mark = await Handler(late).MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(reservation.Id, "CT-TEST"));

        await using var check = TestDatabase.Reservations();
        await using var checkDocs = TestDatabase.Tokens();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(ReservationStatus.Cancelled, (await check.Reservations.SingleAsync()).Status);
        Assert.Equal(TokenStatus.Issued, (await checkDocs.Tokens.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var held = await TestDatabase.AddReservationAsync(connector, "DRV-A", 1, ReservationStatus.Held);
        var rejected = await TestDatabase.AddReservationAsync(connector, "DRV-B", 1, ReservationStatus.Rejected);
        var requested = await TestDatabase.AddReservationAsync(connector, "DRV-C", 1, ReservationStatus.Requested);

        await using (var a = TestDatabase.Reservations())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(held.Id, "CT-1")));
        await using (var b = TestDatabase.Reservations())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(rejected.Id, "CT-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Reservations())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkBookedAsync(Guid.CreateVersion7(), new TokenIssued(requested.Id, "CT-3")));

        await using var check = TestDatabase.Reservations();
        var statuses = (await check.Reservations.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(ReservationStatus.Booked, statuses["DRV-A"]);
        Assert.Equal(ReservationStatus.Rejected, statuses["DRV-B"]);
        Assert.Equal(ReservationStatus.Requested, statuses["DRV-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedReservation_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var owner = Guid.CreateVersion7();
        var reservation = await TestDatabase.AddReservationAsync(connector, "DRV-1", 1, ReservationStatus.Rejected, owner);

        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(connector), new TestUser(owner)).CancelAsync(reservation.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Cancellation_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var reservation = await TestDatabase.AddReservationAsync(connector, "DRV-1", 1, ReservationStatus.Held);

        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(connector), new TestUser(Guid.NewGuid())).CancelAsync(reservation.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Reservations();
        Assert.Equal(ReservationStatus.Held, (await check.Reservations.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanCancelAnyonesReservation()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var reservation = await TestDatabase.AddReservationAsync(connector, "DRV-1", 1, ReservationStatus.Held);

        await using var db = TestDatabase.Reservations();
        var cancelled = await new ReservationService(db, new FixedCatalog(connector), new TestUser(Guid.NewGuid(), staff: true)).CancelAsync(reservation.Id, default);
        Assert.Equal(ReservationStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Reservations();
        var reservation = await new ReservationService(db, new FixedCatalog(connector), new TestUser(owner))
            .CreateAsync(new CreateReservationRequest(connector.Code, "DRV-1", 2), default);

        await using var check = TestDatabase.Reservations();
        Assert.Equal(owner, (await check.Reservations.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveSlots>(message.Payload).OwnerId);
        Assert.Equal(ReservationStatus.Requested, reservation.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var connector = await TestDatabase.AddConnectorAsync(5);
        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(connector), new TestUser(null)).CreateAsync(new CreateReservationRequest(connector.Code, "DRV-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownConnector_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateReservationRequest("NOPE", "DRV-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
