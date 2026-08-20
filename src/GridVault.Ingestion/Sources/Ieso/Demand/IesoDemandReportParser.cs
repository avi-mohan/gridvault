using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace GridVault.Ingestion.Sources.Ieso.Demand;

public sealed record ParsedDemandRow(LocalDate Date, int HourEnding, decimal? MarketDemand, decimal? OntarioDemand);

/// <summary>
/// The result of parsing one PUB_Demand_{year}.csv payload. CreatedAtRaw is
/// kept as unparsed text — its timezone isn't confirmed yet (see
/// docs/decisions.md), so turning it into an Instant happens at the load
/// step once that's settled, not here.
/// </summary>
public sealed record ParsedDemandReport(string CreatedAtRaw, int ForYear, IReadOnlyList<ParsedDemandRow> Rows);

/// <summary>
/// Parses the IESO hourly demand report's file shape, confirmed against
/// real files at reports-public.ieso.ca/public/Demand/: three "\\"-prefixed
/// comment lines (each padded to 4 comma-separated fields, e.g.
/// "\\Created at 2026-08-19 07:30:09,,,"), then a
/// Date,Hour,Market Demand,Ontario Demand CSV body with plain "YYYY-MM-DD"
/// dates and no blank cells observed in practice (a blank value cell is
/// still handled as NotPublished, in case one occurs, but real files parsed
/// so far are always fully populated up to the latest published hour).
///
/// Does not resolve hour-ending local dates to UTC instants — that's
/// IesoDemandLoader's job, once the report's timezone is confirmed.
/// </summary>
public static class IesoDemandReportParser
{
    private const string TitlePrefix = "\\\\Hourly Demand Report";
    private const string CreatedAtPrefix = "\\\\Created at ";
    private const string ForYearPrefix = "\\\\For ";
    private const string ExpectedColumnHeader = "Date,Hour,Market Demand,Ontario Demand";

    public static ParsedDemandReport Parse(string content)
    {
        using var reader = new StringReader(content);

        var titleLine = ReadRequiredLine(reader);
        if (FirstField(titleLine) != TitlePrefix)
        {
            throw new FormatException($"Expected '{TitlePrefix}' header, got '{titleLine}'.");
        }

        var createdAtLine = ReadRequiredLine(reader);
        var createdAtRaw = ExtractPrefixedValue(createdAtLine, CreatedAtPrefix);

        var forYearLine = ReadRequiredLine(reader);
        var forYearText = ExtractPrefixedValue(forYearLine, ForYearPrefix);
        if (!int.TryParse(forYearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var forYear))
        {
            throw new FormatException($"Could not parse year from '{forYearLine}'.");
        }

        var columnHeaderLine = ReadRequiredLine(reader);
        if (columnHeaderLine != ExpectedColumnHeader)
        {
            throw new FormatException($"Expected column header '{ExpectedColumnHeader}', got '{columnHeaderLine}'.");
        }

        var rows = new List<ParsedDemandRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            rows.Add(ParseRow(line));
        }

        return new ParsedDemandReport(createdAtRaw, forYear, rows);
    }

    private static ParsedDemandRow ParseRow(string line)
    {
        var fields = line.Split(',');
        if (fields.Length != 4)
        {
            throw new FormatException($"Expected 4 comma-separated fields, got {fields.Length} in '{line}'.");
        }

        var date = LocalDatePattern.Iso.Parse(fields[0]).Value;
        var hourEnding = int.Parse(fields[1], CultureInfo.InvariantCulture);
        var marketDemand = ParseOptionalDecimal(fields[2]);
        var ontarioDemand = ParseOptionalDecimal(fields[3]);

        return new ParsedDemandRow(date, hourEnding, marketDemand, ontarioDemand);
    }

    private static decimal? ParseOptionalDecimal(string field) =>
        string.IsNullOrWhiteSpace(field) ? null : decimal.Parse(field, CultureInfo.InvariantCulture);

    private static string ReadRequiredLine(TextReader reader) =>
        reader.ReadLine() ?? throw new FormatException("Unexpected end of file while reading header.");

    private static string FirstField(string line) => line.Split(',')[0];

    private static string ExtractPrefixedValue(string line, string prefix)
    {
        var value = FirstField(line);
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"Expected line starting with '{prefix}', got '{line}'.");
        }

        return value[prefix.Length..].Trim();
    }
}
