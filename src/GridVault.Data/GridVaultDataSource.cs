using Npgsql;

namespace GridVault.Data;

public static class GridVaultDataSource
{
    /// <summary>
    /// Builds an NpgsqlDataSource with the NodaTime plugin enabled, so
    /// Instant/LocalDate map directly to timestamptz/date without manual
    /// conversion at every call site.
    /// </summary>
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNodaTime();
        return builder.Build();
    }
}
