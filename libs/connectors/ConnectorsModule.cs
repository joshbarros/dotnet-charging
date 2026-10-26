using Charging.Shared;
using Charging.Shared.Contracts;
using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Charging.Connectors;

public static class ConnectorsModule
{
    public static IServiceCollection AddConnectorsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Charging") ?? throw new InvalidOperationException("Connection string 'Charging' is not configured.");
        services.AddDbContext<ConnectorsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ConnectorsDbContext.SchemaName)));
        services.AddScoped<ConnectorsHandler>();
        services.AddScoped<IConnectorCatalog, ConnectorCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ConnectorsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ConnectorsDbContext>>("slots-db", tags: ["ready"]);

        messaging.AddOutbox<ConnectorsDbContext>()
            .AddConsumer<ConnectorsHandler>(Topology.ConnectorsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
