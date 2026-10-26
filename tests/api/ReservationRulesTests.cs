using Charging.Api.Modules.Reservations;
using Xunit;

namespace Charging.Api.Tests;

public class ReservationRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("DRV-1", 0)]
    [InlineData("DRV-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long slots) =>
        Assert.NotNull(ReservationRules.Validate(reference, slots));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(ReservationRules.Validate("DRV-1", ReservationRules.MaxSlotsPerReservation));
        Assert.NotNull(ReservationRules.Validate("DRV-1", ReservationRules.MaxSlotsPerReservation + 1));
        Assert.NotNull(ReservationRules.Validate(new string('x', ReservationRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one slot
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long slots)
    {
        Assert.NotNull(ReservationRules.Validate("DRV-1", slots));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyTotal_IsSlotsTimesPricePlusTheSessionFee()
    {
        Assert.Null(ReservationRules.Validate("DRV-1", 4));
        Assert.Equal(4 * 900L + 150L, ReservationRules.TotalCents(900, 4)); // two hours at $9.00 per half hour + $1.50
        Assert.Equal(151L, ReservationRules.TotalCents(1, 1));
        Assert.Equal(8 * 900L + 150L, ReservationRules.TotalCents(900, ReservationRules.MaxSlotsPerReservation));
    }

    [Fact]
    public void TotalArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => ReservationRules.TotalCents(long.MaxValue, 1)); // adding the fee overflows
        Assert.Throws<OverflowException>(() => ReservationRules.TotalCents(long.MaxValue / 2, 3)); // the multiplication overflows
    }

    [Theory]
    [InlineData("POISON")]
    [InlineData("DRV-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void PoisonSubstring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(ReservationRules.IsPoison(reference));

    [Theory]
    [InlineData("DRV-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(ReservationRules.IsPoison(reference));
}
