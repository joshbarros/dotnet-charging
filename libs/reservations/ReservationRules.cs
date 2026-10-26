namespace Charging.Reservations;

public static class ReservationRules
{
    public const int MaxSlotsPerReservation = 8;
    public const int MaxReferenceLength = 40;
    public const long SessionFeeCents = 150;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long slots)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Driver reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Driver reference must be at most {MaxReferenceLength} characters.";
        if (slots < 1)
            return "A reservation needs at least 1 slot.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (slots > MaxSlotsPerReservation)
            return $"A reservation takes at most {MaxSlotsPerReservation} slots.";
        return null;
    }

    /// <summary>Slots times the connector's price per slot, plus a flat session fee, in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long priceCents, int slots) => checked(slots * priceCents + SessionFeeCents);
}
