using Charging.Api.Data;
using Charging.Api.Modules.Reservations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Charging.Api.Modules.Connectors;

public record CreateConnectorRequest(string? Code, string? Name, string? ConnectorType, string? City, DateTimeOffset? ServiceDate, long SlotsTotal, long PriceCents);

public record ConnectorDto(Guid Id, string Code, string Name, string ConnectorType, string City, DateTimeOffset ServiceDate, long PriceCents, int SlotsTotal, int SlotsLeft)
{
    public static ConnectorDto From(Connector s) => new(s.Id, s.Code, s.Name, s.ConnectorType, s.City, s.ServiceDate, s.PriceCents, s.SlotsTotal, s.SlotsLeft);
}

[ApiController]
[Route("api/connectors")]
public class ConnectorsController(ChargingDbContext db) : ControllerBase
{
    private const int MaxSlotsTotal = 96;
    private const long MaxPriceCents = 100_000;

    [HttpGet]
    public async Task<IEnumerable<ConnectorDto>> List(CancellationToken ct) =>
        (await db.Connectors.AsNoTracking().OrderBy(s => s.ServiceDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(ConnectorDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<ConnectorDto>> Get(string code, CancellationToken ct)
    {
        var connector = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return connector is null ? NotFound() : ConnectorDto.From(connector);
    }

    /// <summary>Opens a connector. This is the only place SlotsLeft is initialised; after that only the Connectors handler writes it.</summary>
    [HttpPost]
    public async Task<ActionResult<ConnectorDto>> Create(CreateConnectorRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectorType) || string.IsNullOrWhiteSpace(request.City))
            throw new DomainException(400, "Name, connectorType and city are required.");
        if (request.SlotsTotal < 1 || request.SlotsTotal > MaxSlotsTotal)
            throw new DomainException(400, $"Slots on offer must be between 1 and {MaxSlotsTotal} slots.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per slot.");
        if (await db.Connectors.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Connector '{code}' already exists.");

        var connector = new Connector
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            ConnectorType = request.ConnectorType.Trim(),
            City = request.City.Trim(),
            ServiceDate = request.ServiceDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            SlotsTotal = (int)request.SlotsTotal,
            SlotsLeft = (int)request.SlotsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync(ct);
        return Created($"/api/connectors/{connector.Code}", ConnectorDto.From(connector));
    }
}
