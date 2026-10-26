namespace Charging.Shared.Contracts;

public record ConnectorInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Reservations may ask Connectors, without referencing it. Connectors implements it over its own tables;
/// Reservations never reads another module's schema.
/// </summary>
public interface IConnectorCatalog
{
    Task<ConnectorInfo?> FindAsync(string code, CancellationToken ct);
}
