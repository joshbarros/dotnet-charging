using Charging.Api.Data;
using Charging.Api.Messaging;
using Charging.Api.Middleware;
using Charging.Api.Modules.Reservations;
using Charging.Api.Modules.Connectors;
using Charging.Api.Modules.Tokens;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Charging")
    ?? throw new InvalidOperationException("Connection string 'Charging' is not configured.");
builder.Services.AddDbContext<ChargingDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<RabbitOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<ReservationOptions>(builder.Configuration.GetSection("Reservations"));
builder.Services.AddSingleton<RabbitConnection>();
builder.Services.AddSingleton<KafkaLog>();

builder.Services.AddScoped<ReservationService>();
builder.Services.AddScoped<ConnectorsHandler>();
builder.Services.AddScoped<TokensHandler>();
builder.Services.AddScoped<ReservationsHandler>();

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService(sp => new ModuleConsumer<ConnectorsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.ConnectorsQueue, [Topology.ReserveKey, Topology.ReleaseKey], Prefetch: 1),
    sp.GetRequiredService<ILogger<ModuleConsumer<ConnectorsHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<TokensHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.TokensQueue, [Topology.HeldKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<TokensHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<ReservationsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.ReservationsQueue,
        [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<ReservationsHandler>>>()));

var app = builder.Build();

app.UseMiddleware<ExceptionHandler>();
app.MapControllers();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/api/health", () => Results.Text("Healthy"));

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ChargingDbContext>().Database.MigrateAsync();
}

app.Run();
