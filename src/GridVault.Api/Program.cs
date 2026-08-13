using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, loggerConfiguration) => loggerConfiguration
    .WriteTo.Console(new CompactJsonFormatter()));

var app = builder.Build();

// Read-side endpoints (as-of query, etc.) land in Milestone 2. This is a
// scaffold health check only.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

