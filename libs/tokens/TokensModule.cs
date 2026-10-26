using Charging.Shared;
using Charging.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Charging.Tokens;

public static class TokensModule
{
    public static IServiceCollection AddTokensModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Charging") ?? throw new InvalidOperationException("Connection string 'Charging' is not configured.");
        services.AddDbContext<TokensDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", TokensDbContext.SchemaName)));
        services.AddScoped<TokensHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<TokensDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<TokensDbContext>>("tokens-db", tags: ["ready"]);

        messaging.AddOutbox<TokensDbContext>()
            .AddConsumer<TokensHandler>(Topology.TokensQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
