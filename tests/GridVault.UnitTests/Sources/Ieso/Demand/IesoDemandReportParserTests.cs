using GridVault.Ingestion.Sources.Ieso.Demand;
using NodaTime;

namespace GridVault.UnitTests.Sources.Ieso.Demand;

public class IesoDemandReportParserTests
{
    // Real excerpt from PUB_Demand_2026_v229.csv (fixtures/ieso/demand/),
    // fetched from reports-public.ieso.ca. 2026-08-17 HE1 here (18711,
    // 16190) is a genuine revision of what PUB_Demand_2026_v228.csv
    // reported the day before (19140, 16615) — the same hour, restated.
    private const string RealExcerpt = """
        \\Hourly Demand Report,,,
        \\Created at 2026-08-18 07:30:09,,,
        \\For 2026,,,
        Date,Hour,Market Demand,Ontario Demand
        2026-08-17,1,18711,16190
        2026-08-17,2,18494,15978
        2026-08-18,24,19024,16645
        """;

    [Fact]
    public void Parse_RealExcerpt_ExtractsHeaderFields()
    {
        var report = IesoDemandReportParser.Parse(RealExcerpt);

        Assert.Equal("2026-08-18 07:30:09", report.CreatedAtRaw);
        Assert.Equal(2026, report.ForYear);
        Assert.Equal(3, report.Rows.Count);
    }

    [Fact]
    public void Parse_RealExcerpt_ParsesRowsInOrder()
    {
        var report = IesoDemandReportParser.Parse(RealExcerpt);

        var first = report.Rows[0];
        Assert.Equal(new LocalDate(2026, 8, 17), first.Date);
        Assert.Equal(1, first.HourEnding);
        Assert.Equal(18711m, first.MarketDemand);
        Assert.Equal(16190m, first.OntarioDemand);

        var last = report.Rows[^1];
        Assert.Equal(new LocalDate(2026, 8, 18), last.Date);
        Assert.Equal(24, last.HourEnding);
    }

    [Fact]
    public void Parse_BlankValueCell_IsNullNotZero()
    {
        // Real files fetched so far never contain a blank cell (IESO
        // appears not to publish a row until its value is known), so this
        // case is constructed rather than observed — but the schema's
        // NotPublished status exists for exactly this shape, so the parser
        // must not choke on it or coerce it to zero.
        const string withBlank = """
            \\Hourly Demand Report,,,
            \\Created at 2026-08-18 07:30:09,,,
            \\For 2026,,,
            Date,Hour,Market Demand,Ontario Demand
            2026-08-18,24,,
            """;

        var report = IesoDemandReportParser.Parse(withBlank);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.MarketDemand);
        Assert.Null(row.OntarioDemand);
    }

    [Fact]
    public void Parse_UnexpectedTitleLine_Throws()
    {
        const string wrongTitle = "\\\\Some Other Report,,,\n\\\\Created at 2026-08-18 07:30:09,,,\n\\\\For 2026,,,\nDate,Hour,Market Demand,Ontario Demand\n";

        Assert.Throws<FormatException>(() => IesoDemandReportParser.Parse(wrongTitle));
    }
}
