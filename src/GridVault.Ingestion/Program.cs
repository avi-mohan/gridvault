using GridVault.Data;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(dispose: true);

var connectionString = builder.Configuration.GetConnectionString("GridVault")
    ?? throw new InvalidOperationException("ConnectionStrings:GridVault is not configured.");

DatabaseMigrator.Migrate(connectionString);

// No ingestion jobs registered yet — Milestone 1 is data model and local
// environment only. Quartz.NET scheduling and fetch/land/parse/load jobs
// land in Milestone 2.
var host = builder.Build();
host.Run();
