using Amazon.S3;
using GridVault.Data;
using GridVault.Data.IngestionRuns;
using GridVault.Data.Observations;
using GridVault.Data.Series;
using GridVault.Data.Sources;
using GridVault.Ingestion.Jobs;
using GridVault.Ingestion.ObjectStorage;
using GridVault.Ingestion.Sources.Ieso.Demand;
using Quartz;
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

var objectStorageOptions = builder.Configuration.GetSection("ObjectStorage").Get<RawPayloadStoreOptions>()
    ?? throw new InvalidOperationException("ObjectStorage configuration is not set.");

builder.Services.AddSingleton(_ => GridVaultDataSource.Create(connectionString));
builder.Services.AddSingleton(_ => RawPayloadStore.CreateClient(objectStorageOptions));
builder.Services.AddSingleton(sp => new RawPayloadStore(sp.GetRequiredService<IAmazonS3>(), objectStorageOptions.Bucket));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SourceRepository>();
builder.Services.AddSingleton<SeriesRepository>();
builder.Services.AddSingleton<ObservationRepository>();
builder.Services.AddSingleton<IngestionRunRepository>();
builder.Services.AddSingleton<IesoDemandLoader>();

builder.Services.AddQuartz(q =>
{
    var jobKey = new JobKey(nameof(IesoDemandFetchJob));
    q.AddJob<IesoDemandFetchJob>(options => options.WithIdentity(jobKey));

    // The demand file refreshes once daily; its HTTP Last-Modified landed
    // at 12:31:18 GMT on every one of three consecutive days we checked
    // (see docs/decisions.md). 12:40 UTC gives ~9 minutes of buffer for
    // day-to-day jitter while still firing the same day the file updates.
    // This is a UTC wall-clock schedule, pinned explicitly via InTimeZone
    // so it doesn't inherit the host machine's local zone -- and it is
    // deliberately unrelated to the report's own fixed-EST data
    // convention. When to go fetch the file and what zone its rows are
    // published in are two different facts; only the second one is EST.
    q.AddTrigger(options => options
        .ForJob(jobKey)
        .WithIdentity($"{nameof(IesoDemandFetchJob)}-trigger")
        .WithCronSchedule("0 40 12 * * ?", x => x.InTimeZone(TimeZoneInfo.Utc)));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

var host = builder.Build();
host.Run();
