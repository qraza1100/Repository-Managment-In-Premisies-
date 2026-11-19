using Microsoft.Data.SqlClient;
using System.Reflection;
using System.Text.RegularExpressions;

namespace QualityAviationHelpdesk.Data
{
    // Brings the helpdesk database up to the newest db\migrations\*.sql script, once, at start-up.
    // Several copies of the portal can start at the same moment (a new release starts beside the live one), so the
    // whole run holds a database lock: the second copy waits and then finds nothing left to do.
    public static partial class SchemaMigrator
    {
        [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
        private static partial Regex BatchSeparator();

        public static async Task ApplyAsync(string connectionString, ILogger logger, CancellationToken cancellationToken = default)
        {
            var scripts = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(name => name.StartsWith("Schema.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await ExecuteAsync(connection, null, "EXEC sp_getapplock @Resource = N'hd-schema', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 120000;", cancellationToken);
            try
            {
                await ExecuteAsync(connection, null,
                    "IF SCHEMA_ID('hd') IS NULL EXEC('CREATE SCHEMA hd');" +
                    "IF OBJECT_ID('hd.SchemaVersions') IS NULL CREATE TABLE hd.SchemaVersions (Script nvarchar(200) NOT NULL PRIMARY KEY, AppliedAt datetime2(3) NOT NULL);",
                    cancellationToken);

                var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var read = new SqlCommand("SELECT Script FROM hd.SchemaVersions", connection))
                await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken)) applied.Add(reader.GetString(0));

                foreach (var resource in scripts)
                {
                    var script = resource["Schema.".Length..];
                    if (applied.Contains(script)) continue;

                    string sql;
                    await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!)
                    using (var text = new StreamReader(stream))
                        sql = await text.ReadToEndAsync(cancellationToken);

                    // One script is all or nothing
                    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    foreach (var batch in BatchSeparator().Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)))
                        await ExecuteAsync(connection, transaction, batch, cancellationToken);

                    await using (var record = new SqlCommand("INSERT INTO hd.SchemaVersions (Script, AppliedAt) VALUES (@script, SYSUTCDATETIME())", connection, transaction))
                    {
                        record.Parameters.AddWithValue("@script", script);
                        await record.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation("Database schema {Script} applied", script);
                }
            }
            finally
            {
                await ExecuteAsync(connection, null, "EXEC sp_releaseapplock @Resource = N'hd-schema', @LockOwner = N'Session';", CancellationToken.None);
            }
        }

        private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
