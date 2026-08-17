using System.Data;
using Dapper;
using NodaTime;
using Npgsql;
using NpgsqlTypes;

namespace GridVault.Data;

/// <summary>
/// Npgsql.NodaTime's UseNodaTime() (enabled in GridVaultDataSource.Create)
/// covers the ADO.NET provider layer: a timestamptz column already comes
/// back from the reader as a boxed Instant, so Dapper's column deserializer
/// just casts it and query results work with no extra code. Writing a
/// parameter is a different path — Dapper resolves each parameter's
/// System.Data.DbType itself via SqlMapper.LookupDbType, a hardcoded switch
/// over CLR types that has never heard of NodaTime, and throws
/// NotSupportedException rather than guess. This handler does no conversion
/// of its own; it only stops Dapper from refusing the parameter, setting
/// NpgsqlDbType.TimestampTz and handing the Instant straight to Npgsql,
/// which is what the plugin actually converts.
/// </summary>
internal static class NodaTimeDapperTypeHandlers
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new InstantTypeHandler());
        _registered = true;
    }

    private sealed class InstantTypeHandler : SqlMapper.TypeHandler<Instant>
    {
        public override void SetValue(IDbDataParameter parameter, Instant value)
        {
            ((NpgsqlParameter)parameter).NpgsqlDbType = NpgsqlDbType.TimestampTz;
            parameter.Value = value;
        }

        public override Instant Parse(object value) => (Instant)value;
    }
}
