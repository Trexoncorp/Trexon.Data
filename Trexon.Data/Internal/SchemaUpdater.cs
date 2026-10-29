using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Trexon.Data.Internal;

internal static class SchemaUpdater
{
    private const string History = "__TrexonDataHistory";
    private const long PostgresLock = 0x545245584F4E;

    public static async Task<SchemaUpdateResult> UpdateAsync(DataSession db, DataProvider provider,
        ChangeDefinition[] changes, CancellationToken cancellationToken)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var initial = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var target = SchemaState.From(initial, provider);
        if (target.Tables.Any(t => t.Name == History)) throw new SchemaUpdateException($"Table '{History}' is reserved.");
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        var locked = false;
        var mysqlLock = "Trexon.Data." + SchemaState.Hash(connection.Database)[..40];
        IDbContextTransaction? transaction = null;
        var sqlite = provider == DataProvider.Sqlite;
        try
        {
            if (sqlite)
            {
                // SQLite rebuild operations require foreign keys disabled before starting the transaction.
                await ExecuteAsync(db, "PRAGMA foreign_keys = OFF;", cancellationToken);
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            }
            else
            {
                var lockSql = provider == DataProvider.Postgres
                    ? $"SELECT pg_advisory_lock({PostgresLock});"
                    : $"SELECT GET_LOCK('{mysqlLock}', 30);";
                var result = await ScalarAsync(db, lockSql, cancellationToken);
                if (provider == DataProvider.MySql && Convert.ToInt32(result) != 1)
                    throw new TimeoutException("Could not acquire the Trexon.Data schema lock within 30 seconds.");
                locked = true;
                if (provider == DataProvider.Postgres) transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            }
            var helper = db.GetService<ISqlGenerationHelper>();
            var historyTable = helper.DelimitIdentifier(History);
            var id = helper.DelimitIdentifier("Id");
            var state = helper.DelimitIdentifier("State");
            var applied = helper.DelimitIdentifier("AppliedUtc");
            var textType = provider == DataProvider.MySql ? "longtext" : "text";
            await ExecuteAsync(db, $"CREATE TABLE IF NOT EXISTS {historyTable} ({id} varchar(150) PRIMARY KEY, {state} {textType} NOT NULL, {applied} varchar(40) NOT NULL);", cancellationToken);
            var json = await ScalarAsync(db, $"SELECT {state} FROM {historyTable} WHERE {id} = 'current';", cancellationToken);
            SchemaState? previous = null;
            if (json is string value)
            {
                try { previous = JsonSerializer.Deserialize<SchemaState>(value) ?? throw new JsonException("Empty state."); }
                catch (JsonException ex) { throw new SchemaUpdateException($"The schema history is unreadable: {ex.Message}"); }
            }
            if (previous?.Pending == true)
                throw new SchemaUpdateException("An earlier MySQL schema update did not complete. Restore the database from a known schema and its matching history before retrying.");
            await VerifyTablesAsync(db, provider, previous, target, cancellationToken);
            // Planning mutates its input while applying explicit renames, so retain the original for the failure marker.
            var dirty = previous is null ? new SchemaState { Provider = provider.ToString(), Pending = true }
                : JsonSerializer.Deserialize<SchemaState>(JsonSerializer.Serialize(previous))!;
            dirty.Pending = true;
            var plan = SchemaPlanner.Plan(previous, target, initial, changes);
            var commands = db.GetService<IMigrationsSqlGenerator>().Generate(plan.Operations, model);
            var changed = previous is null || plan.Operations.Count != 0 || plan.AppliedChanges.Length != 0;
            if (changed)
            {
                // MySQL DDL implicitly commits. Persist a guard before the first DDL so a partial update is never replayed silently.
                if (provider == DataProvider.MySql)
                    await WriteCurrentAsync(db, historyTable, id, state, applied, dirty, cancellationToken);
                foreach (var command in commands)
                    await ExecuteAsync(db, command.CommandText, cancellationToken);
                if (sqlite)
                {
                    using var check = Command(db, "PRAGMA foreign_key_check;");
                    await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken)) throw new SchemaUpdateException("The schema update failed SQLite foreign_key_check.");
                }
                foreach (var table in plan.State.Tables)
                    table.Physical = await PhysicalFingerprintAsync(db, provider, table, cancellationToken);
                if (provider == DataProvider.MySql) transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await WriteCurrentAsync(db, historyTable, id, state, applied, plan.State, cancellationToken);
                await InsertHistoryAsync(db, historyTable, id, state, applied,
                    "update-" + Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(plan.State), cancellationToken);
            }
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return new(changed, plan.Operations.Count, Array.AsReadOnly(plan.AppliedChanges));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
            if (locked && connection.State == ConnectionState.Open)
            {
                var sql = provider == DataProvider.Postgres
                    ? $"SELECT pg_advisory_unlock({PostgresLock});"
                    : $"SELECT RELEASE_LOCK('{mysqlLock}');";
                await ExecuteAsync(db, sql, CancellationToken.None);
            }
            if (sqlite && connection.State == ConnectionState.Open)
                await ExecuteAsync(db, "PRAGMA foreign_keys = ON;", CancellationToken.None);
        }
    }

    private static async Task WriteCurrentAsync(DataSession db, string table, string id, string state, string applied,
        SchemaState snapshot, CancellationToken cancellationToken)
    {
        var suffix = db.Database.ProviderName == "Pomelo.EntityFrameworkCore.MySql"
            ? $" ON DUPLICATE KEY UPDATE {state} = @state, {applied} = @utc"
            : $" ON CONFLICT ({id}) DO UPDATE SET {state} = @state, {applied} = @utc";
        using var command = Command(db, $"INSERT INTO {table} ({id}, {state}, {applied}) VALUES (@id, @state, @utc){suffix};");
        Add(command, "@id", "current"); Add(command, "@state", JsonSerializer.Serialize(snapshot));
        Add(command, "@utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertHistoryAsync(DataSession db, string table, string id, string state, string applied,
        string key, string json, CancellationToken cancellationToken)
    {
        using var command = Command(db, $"INSERT INTO {table} ({id}, {state}, {applied}) VALUES (@id, @state, @utc);");
        Add(command, "@id", key); Add(command, "@state", json); Add(command, "@utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifyTablesAsync(DataSession db, DataProvider provider, SchemaState? previous,
        SchemaState target, CancellationToken cancellationToken)
    {
        // Compare physical column names and types with the last managed snapshot. Never adopt an unrelated existing table.
        foreach (var table in (previous?.Tables ?? target.Tables))
        {
            var columns = await ReadColumnsAsync(db, provider, table, cancellationToken);
            if (previous is null)
            {
                if (columns.Count != 0) throw new SchemaUpdateException($"Table '{table.Name}' already exists without Trexon.Data history. Existing databases must be migrated separately.");
                continue;
            }
            if (columns.Count != table.Columns.Count || table.Columns.Any(c => !columns.ContainsKey(c.Name)))
                throw new SchemaUpdateException($"Physical schema of '{table.Name}' differs from its history. Restore the expected schema before updating.");
            if (table.Physical != await PhysicalFingerprintAsync(db, provider, table, cancellationToken))
                throw new SchemaUpdateException($"Physical schema of '{table.Name}' changed outside Trexon.Data. Restore its expected definition before updating.");

        }
        if (previous is not null)
        {
            foreach (var table in target.Tables.Where(t => previous.Tables.All(p => p.Key != t.Key)))
                if ((await ReadColumnsAsync(db, provider, table, cancellationToken)).Count != 0)
                    throw new SchemaUpdateException($"New mapped table '{table.Name}' already exists outside Trexon.Data history.");
        }
    }

    private static async Task<Dictionary<string, string>> ReadColumnsAsync(DataSession db, DataProvider provider,
        TableState table, CancellationToken cancellationToken)
    {
        var sql = provider switch
        {
            DataProvider.Sqlite => "SELECT name, type FROM pragma_table_xinfo(@table);",
            DataProvider.Postgres => "SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod) FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class t ON t.oid = a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace WHERE n.nspname = COALESCE(@schema, current_schema()) AND t.relname = @table AND a.attnum > 0 AND NOT a.attisdropped;",
            _ => "SELECT COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;"
        };
        using var command = Command(db, sql);
        Add(command, "@table", table.Name);
        if (provider == DataProvider.Postgres) Add(command, "@schema", table.Schema);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static async Task<string> PhysicalFingerprintAsync(DataSession db, DataProvider provider,
        TableState table, CancellationToken cancellationToken)
    {
        string[] queries = provider switch
        {
            DataProvider.Sqlite => ["SELECT type, name, sql FROM sqlite_master WHERE tbl_name = @table AND type IN ('table', 'index', 'trigger') ORDER BY type, name;"],
            DataProvider.MySql => [$"SHOW CREATE TABLE {db.GetService<ISqlGenerationHelper>().DelimitIdentifier(table.Name)};"],
            _ =>
            [
                "SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod), a.attnotnull, a.attidentity, a.attgenerated, pg_get_expr(d.adbin, d.adrelid), a.attcollation, col_description(t.oid, a.attnum) FROM pg_attribute a JOIN pg_class t ON t.oid = a.attrelid JOIN pg_namespace n ON n.oid = t.relnamespace LEFT JOIN pg_attrdef d ON d.adrelid = t.oid AND d.adnum = a.attnum WHERE n.nspname = COALESCE(@schema, current_schema()) AND t.relname = @table AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum;",
                "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = COALESCE(@schema, current_schema()) AND tablename = @table ORDER BY indexname;",
                "SELECT c.conname, pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace WHERE n.nspname = COALESCE(@schema, current_schema()) AND t.relname = @table ORDER BY c.conname;",
                "SELECT g.tgname, pg_get_triggerdef(g.oid) FROM pg_trigger g JOIN pg_class t ON t.oid = g.tgrelid JOIN pg_namespace n ON n.oid = t.relnamespace WHERE n.nspname = COALESCE(@schema, current_schema()) AND t.relname = @table AND NOT g.tgisinternal ORDER BY g.tgname;"
            ]
        };
        var rows = new List<string?[]>();
        foreach (var sql in queries)
        {
            using var command = Command(db, sql);
            if (provider != DataProvider.MySql) Add(command, "@table", table.Name);
            if (provider == DataProvider.Postgres) Add(command, "@schema", table.Schema);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    if (provider == DataProvider.MySql && row[i] is { } value)
                        row[i] = Regex.Replace(value, @" AUTO_INCREMENT=\d+", "", RegexOptions.CultureInvariant);
                }
                rows.Add(row);
            }
        }
        return SchemaState.Hash(rows);
    }

    private static DbCommand Command(DataSession db, string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (value is null) parameter.DbType = DbType.String;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(DataSession db, string sql, CancellationToken cancellationToken)
    {
        using var command = Command(db, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(DataSession db, string sql, CancellationToken cancellationToken)
    {
        using var command = Command(db, sql);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
