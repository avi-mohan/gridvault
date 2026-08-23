using Dapper;
using Npgsql;

namespace GridVault.Data.Sources;

public sealed class SourceRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SourceRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<long> GetIdByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT id FROM source WHERE name = @Name";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var id = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            sql,
            new { Name = name },
            cancellationToken: cancellationToken));

        return id ?? throw new InvalidOperationException($"No source named '{name}'.");
    }
}
