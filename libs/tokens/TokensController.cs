using Charging.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Charging.Tokens;

public record TokenDto(Guid ReservationId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/tokens")]
[Authorize]
public class TokensController(TokensDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{reservationId:guid}")]
    public async Task<ActionResult<TokenDto>> Get(Guid reservationId, CancellationToken ct)
    {
        var doc = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(d => d.ReservationId == reservationId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new TokenDto(doc.ReservationId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
