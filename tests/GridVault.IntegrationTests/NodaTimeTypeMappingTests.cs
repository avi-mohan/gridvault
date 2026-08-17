using Dapper;
using GridVault.Data;
using NodaTime;
using Npgsql;

namespace GridVault.IntegrationTests;

/// <summary>
/// Regression coverage for the Dapper/NodaTime parameter gap: Dapper resolves
/// each parameter's DbType itself and throws NotSupportedException for CLR
/// types it doesn't recognize, Instant included, unless a type handler is
/// registered (see NodaTimeDapperTypeHandlers). This sends an Instant through
/// as a parameter and back through a real timestamptz column, so it fails
/// loudly and specifically — not as a generic query failure — if that
/// registration ever regresses.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class NodaTimeTypeMappingTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public NodaTimeTypeMappingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task Instant_RoundTripsThroughATimestamptzColumn_AsTheSameInstant()
    {
        // timestamptz has microsecond precision; Instant can carry finer
        // resolution than that from the system clock, so round-trip fidelity
        // only holds at microsecond granularity — truncate before comparing,
        // the same way Postgres truncates on write. 10 ticks = 1 microsecond.
        var now = SystemClock.Instance.GetCurrentInstant();
        var written = Instant.FromUnixTimeTicks(now.ToUnixTimeTicks() / 10 * 10);

        await using var connection = await _dataSource.OpenConnectionAsync();

        var roundTripped = await connection.ExecuteScalarAsync<Instant>(
            "SELECT @Value::timestamptz",
            new { Value = written });

        Assert.Equal(written, roundTripped);
    }
}
