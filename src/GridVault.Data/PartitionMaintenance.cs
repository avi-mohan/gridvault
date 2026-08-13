using System.Text.RegularExpressions;
using Dapper;
using NodaTime;
using Npgsql;

namespace GridVault.Data;

/// <summary>
/// Guards against the "ran out of pre-created observation partitions"
/// failure mode. There is deliberately no DEFAULT partition (see
/// docs/decisions.md), so once headroom runs out, ingestion inserts start
/// failing outright. This is a stub for Milestone 1: a method that fails
/// loudly when called. Milestone 3 wires it into a scheduled check and
/// alerting; pg_partman-style automatic partition creation is still out of
/// scope.
/// </summary>
public sealed class PartitionMaintenance
{
    private static readonly Regex UpperBoundPattern = new(@"TO \('([^']+)'\)", RegexOptions.Compiled);

    private readonly NpgsqlDataSource _dataSource;

    public PartitionMaintenance(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Instant> GetLatestObservationPartitionUpperBoundAsync(CancellationToken cancellationToken = default)
    {
        const string boundsSql = """
            SELECT pg_get_expr(c.relpartbound, c.oid) AS bound
            FROM pg_class c
            JOIN pg_inherits i ON i.inhrelid = c.oid
            JOIN pg_class p ON p.oid = i.inhparent
            WHERE p.relname = 'observation'
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var boundExpressions = await connection.QueryAsync<string>(
            new CommandDefinition(boundsSql, cancellationToken: cancellationToken));

        Instant? latest = null;

        foreach (var expression in boundExpressions)
        {
            // Feed the upper-bound literal Postgres itself produced back
            // through a ::timestamptz cast, rather than parsing its text
            // output on the client — Postgres is the authority on what its
            // own output means.
            var literal = ExtractUpperBoundLiteral(expression);

            var upperBound = await connection.ExecuteScalarAsync<Instant>(
                new CommandDefinition(
                    "SELECT @Literal::timestamptz",
                    new { Literal = literal },
                    cancellationToken: cancellationToken));

            if (latest is null || upperBound > latest)
            {
                latest = upperBound;
            }
        }

        return latest ?? throw new InvalidOperationException("observation has no partitions.");
    }

    public async Task EnsureFuturePartitionsAsync(int minMonthsAhead, CancellationToken cancellationToken = default)
    {
        var latestUpperBound = await GetLatestObservationPartitionUpperBoundAsync(cancellationToken);

        // Approximated as 30-day months: this is a headroom guard, not a
        // billing calculation, and pre-created partitions already cover
        // whole calendar months with margin.
        var threshold = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(minMonthsAhead * 30);

        if (latestUpperBound < threshold)
        {
            throw new InvalidOperationException(
                $"observation partitions only extend to {latestUpperBound}, which is less than {minMonthsAhead} months from now. Create more partitions.");
        }
    }

    private static string ExtractUpperBoundLiteral(string partitionBoundExpression)
    {
        var match = UpperBoundPattern.Match(partitionBoundExpression);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not parse partition bound '{partitionBoundExpression}'.");
        }

        return match.Groups[1].Value;
    }
}
