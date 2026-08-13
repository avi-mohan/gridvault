using GridVault.Data;
using Npgsql;

namespace GridVault.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class PartitionMaintenanceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public PartitionMaintenanceTests(PostgresFixture fixture)
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
    public async Task EnsureFuturePartitionsAsync_DoesNotThrow_WhenMigrationsHaveJustRun()
    {
        var partitionMaintenance = new PartitionMaintenance(_dataSource);

        // Script0005 pre-creates 13 months of headroom from whenever
        // migrations run, so a 3-month check should always pass right
        // after a fresh migration.
        await partitionMaintenance.EnsureFuturePartitionsAsync(minMonthsAhead: 3);
    }

    [Fact]
    public async Task EnsureFuturePartitionsAsync_ThrowsLoudly_WhenNotEnoughHeadroomExists()
    {
        var partitionMaintenance = new PartitionMaintenance(_dataSource);

        // No real deployment would ask for 100 years of headroom — this
        // proves the check actually fails when it should, not just that it
        // never throws.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => partitionMaintenance.EnsureFuturePartitionsAsync(minMonthsAhead: 1200));
    }
}
