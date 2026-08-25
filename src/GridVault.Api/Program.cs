using GridVault.Api.Series;
using GridVault.Data;
using GridVault.Data.Observations;
using GridVault.Data.Series;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, loggerConfiguration) => loggerConfiguration
    .WriteTo.Console(new CompactJsonFormatter()));

// Resolved lazily from IConfiguration (rather than read eagerly off
// builder.Configuration here) so WebApplicationFactory-based tests can
// override it via ConfigureAppConfiguration before this singleton is first
// requested.
builder.Services.AddSingleton(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("GridVault")
        ?? throw new InvalidOperationException("ConnectionStrings:GridVault is not configured.");
    return GridVaultDataSource.Create(connectionString);
});
builder.Services.AddSingleton<SeriesRepository>();
builder.Services.AddSingleton<ObservationRepository>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapObservationsEndpoint();

app.Run();

// Exposed so GridVault.IntegrationTests can drive this host via
// WebApplicationFactory<Program> against a Testcontainers-backed Postgres.
public partial class Program;
