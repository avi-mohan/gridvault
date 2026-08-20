using Dapper;
using GridVault.Domain.Series;
using Npgsql;

namespace GridVault.Data.Series;

public sealed class SeriesRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static SeriesRepository()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SeriesRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<GridVault.Domain.Series.Series> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, source_id, code, name, unit, cadence, source_timezone, hour_convention
            FROM series
            WHERE code = @Code
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SeriesRow>(new CommandDefinition(
            sql,
            new { Code = code },
            cancellationToken: cancellationToken));

        return row is null
            ? throw new InvalidOperationException($"No series with code '{code}'.")
            : MapToSeries(row);
    }

    private static GridVault.Domain.Series.Series MapToSeries(SeriesRow row) => new(
        row.Id,
        row.SourceId,
        row.Code,
        row.Name,
        row.Unit,
        row.Cadence,
        row.SourceTimezone,
        ParseHourConvention(row.HourConvention));

    private static HourConvention ParseHourConvention(string hourConvention) => hourConvention switch
    {
        "beginning" => HourConvention.Beginning,
        "ending" => HourConvention.Ending,
        _ => throw new InvalidOperationException($"Unknown hour_convention '{hourConvention}'."),
    };

    private sealed class SeriesRow
    {
        public long Id { get; set; }
        public long SourceId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public NodaTime.Period Cadence { get; set; } = NodaTime.Period.Zero;
        public string SourceTimezone { get; set; } = string.Empty;
        public string HourConvention { get; set; } = string.Empty;
    }
}
