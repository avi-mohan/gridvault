using GridVault.Data;
using Testcontainers.PostgreSql;

namespace GridVault.IntegrationTests;

/// <summary>
/// One Postgres 16 container, migrated once, shared across every test in
/// the collection. Individual tests are responsible for not stepping on
/// each other's rows (distinct source/series per test is enough, since
/// nothing here truncates between tests).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("gridvault")
        .WithUsername("gridvault")
        .WithPassword("gridvault")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DatabaseMigrator.Migrate(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
