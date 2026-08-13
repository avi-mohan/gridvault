using System.Reflection;
using DbUp;

namespace GridVault.Data;

/// <summary>
/// Applies the SQL scripts embedded from Migrations/ in order, tracked via
/// DbUp's journal table so re-running is a no-op. Forward-only: there are no
/// down-migrations, matching the "never mutate history" ethos of the schema
/// itself.
/// </summary>
public static class DatabaseMigrator
{
    public static void Migrate(string connectionString)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed while running script '{result.ErrorScript?.Name}'. See inner exception for detail.",
                result.Error);
        }
    }
}
