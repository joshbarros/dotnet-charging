using Charging.Reservations;
using Charging.Connectors;
using Xunit;

namespace Charging.UnitTests;

public class DtoTests
{
    [Fact]
    public void ReservationDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            ConnectorId = Guid.NewGuid(),
            ConnectorCode = "S-1",
            Reference = "DRV-1",
            Slots = 3,
            TotalCents = 277_500,
            Status = ReservationStatus.Booked,
            TokenNumber = "CT-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = ReservationDto.From(reservation);

        Assert.Equal(reservation.Id, dto.Id);
        Assert.Equal(reservation.OwnerId, dto.OwnerId);
        Assert.Equal("Booked", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("CT-1", dto.TokenNumber);
    }

    [Fact]
    public void ConnectorDto_MapsTheScarceNumber()
    {
        var connector = new Connector { Id = Guid.NewGuid(), Code = "S-1", Name = "v", ConnectorType = "o", City = "d", SlotsTotal = 10, SlotsLeft = 4 };
        var dto = ConnectorDto.From(connector);
        Assert.Equal(10, dto.SlotsTotal);
        Assert.Equal(4, dto.SlotsLeft);
    }
}
