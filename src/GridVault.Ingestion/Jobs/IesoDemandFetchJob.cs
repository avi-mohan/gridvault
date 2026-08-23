using System.Text;
using GridVault.Data.IngestionRuns;
using GridVault.Data.Sources;
using GridVault.Domain.Ingestion;
using GridVault.Ingestion.ObjectStorage;
using GridVault.Ingestion.Sources.Ieso.Demand;
using Microsoft.Extensions.Logging;
using NodaTime;
using Quartz;

namespace GridVault.Ingestion.Jobs;

/// <summary>
/// fetch -> land -> parse -> load for the IESO hourly demand report. Runs
/// on a daily UTC cron (see Program.cs) picked from the file's observed
/// refresh time -- that schedule has nothing to do with the report's own
/// EST data convention; the two are unrelated facts (see
/// docs/decisions.md).
/// </summary>
public sealed class IesoDemandFetchJob : IJob
{
    private const string SourceName = "ieso";
    private const string ReportName = "demand";

    private static readonly Uri BaseUri = new("https://reports-public.ieso.ca/public/Demand/");

    // The file's own day boundary is fixed EST (see docs/decisions.md), so
    // picking "this year's" file by the report's local date -- not the
    // fetch instant's UTC date -- avoids a wrong-year request in the few
    // hours around New Year's where the two calendars disagree.
    private static readonly DateTimeZone ReportZone = DateTimeZoneProviders.Tzdb["Etc/GMT+5"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RawPayloadStore _rawPayloadStore;
    private readonly SourceRepository _sourceRepository;
    private readonly IngestionRunRepository _ingestionRunRepository;
    private readonly IesoDemandLoader _loader;
    private readonly ILogger<IesoDemandFetchJob> _logger;

    public IesoDemandFetchJob(
        IHttpClientFactory httpClientFactory,
        RawPayloadStore rawPayloadStore,
        SourceRepository sourceRepository,
        IngestionRunRepository ingestionRunRepository,
        IesoDemandLoader loader,
        ILogger<IesoDemandFetchJob> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rawPayloadStore = rawPayloadStore;
        _sourceRepository = sourceRepository;
        _ingestionRunRepository = ingestionRunRepository;
        _loader = loader;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var fetchedAt = SystemClock.Instance.GetCurrentInstant();
        var reportYear = fetchedAt.InZone(ReportZone).Year;
        var fileName = $"PUB_Demand_{reportYear}.csv";

        var sourceId = await _sourceRepository.GetIdByNameAsync(SourceName, cancellationToken);

        // window_start/window_end record when this run executed, not a
        // requested data range: this job always fetches "whatever the
        // current-year file currently contains" rather than a windowed
        // query, and the file's actual content range isn't known until
        // after parsing, which happens after this row is inserted.
        var runId = await _ingestionRunRepository.InsertRunningAsync(
            sourceId, null, fetchedAt, fetchedAt, fetchedAt, cancellationToken);

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var content = await httpClient.GetByteArrayAsync(new Uri(BaseUri, fileName), cancellationToken);

            var rawKey = new RawPayloadKey(SourceName, ReportName, fetchedAt, fileName);
            await _rawPayloadStore.PutAsync(rawKey, content, cancellationToken);

            var report = IesoDemandReportParser.Parse(Encoding.UTF8.GetString(content));
            var result = await _loader.LoadAsync(report, fetchedAt, runId, cancellationToken);

            await _ingestionRunRepository.CompleteAsync(
                runId,
                IngestionRunStatus.Succeeded,
                SystemClock.Instance.GetCurrentInstant(),
                result.ValuesProcessed,
                result.VintagesWritten,
                rawKey.Format(),
                errorDetail: null,
                cancellationToken);

            _logger.LogInformation(
                "IESO demand fetch run {RunId} succeeded: {ValuesProcessed} values processed, {VintagesWritten} vintages written",
                runId,
                result.ValuesProcessed,
                result.VintagesWritten);
        }
        catch (Exception ex)
        {
            await _ingestionRunRepository.CompleteAsync(
                runId,
                IngestionRunStatus.Failed,
                SystemClock.Instance.GetCurrentInstant(),
                rowsFetched: 0,
                rowsWritten: 0,
                rawStorageKey: null,
                errorDetail: ex.Message,
                cancellationToken);

            _logger.LogError(ex, "IESO demand fetch run {RunId} failed", runId);
            throw;
        }
    }
}
