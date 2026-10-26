using Charging.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Charging.Api.Modules.Tokens;

public record TokenDto(Guid ReservationId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/tokens")]
public class TokensController(ChargingDbContext db) : ControllerBase
{
    [HttpGet("{reservationId:guid}")]
    public async Task<ActionResult<TokenDto>> Get(Guid reservationId, CancellationToken ct)
    {
        var doc = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(d => d.ReservationId == reservationId, ct);
        return doc is null
            ? NotFound()
            : new TokenDto(doc.ReservationId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
