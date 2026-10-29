using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Trexon.Data.Internal;

internal sealed record SchemaPlan(SchemaState State, List<MigrationOperation> Operations, string[] AppliedChanges);

internal static class SchemaPlanner
{
    public static SchemaPlan Plan(SchemaState? previous, SchemaState target,
        IReadOnlyList<MigrationOperation> initial, ChangeDefinition[] changes)
    {
        var old = previous ?? new SchemaState { Provider = target.Provider };
        if (old.Format != 1 || old.Provider != target.Provider)
            throw new SchemaUpdateException("The stored schema format or provider differs from this data source.");
        if (previous is not null && old.Database != target.Database)
            throw new SchemaUpdateException("Database-level model options changed. This requires a separate migration.");
        if (old.Pending) throw new SchemaUpdateException("An earlier schema update did not complete. Inspect and restore the database before retrying; automatic replay is disabled.");
        if (old.Changes.Count > changes.Length)
            throw new SchemaUpdateException("Applied changes were removed. Retain all applied Change definitions in their original order.");
        for (var i = 0; i < old.Changes.Count; i++)
            if (old.Changes[i] != new AppliedChange(changes[i].Id, SchemaState.Hash(changes[i].Steps)))
                throw new SchemaUpdateException($"Applied change '{old.Changes[i].Id}' was edited or reordered.");

        var pending = changes.Skip(old.Changes.Count).ToArray();
        var ops = new List<MigrationOperation>();
        var required = new Dictionary<string, object>();
        if (previous is not null)
        {
            foreach (var change in pending)
            foreach (var step in change.Steps)
            {
                var key = SchemaState.Key(step.Table, step.Schema);
                var table = old.Tables.SingleOrDefault(t => t.Key == key);
                var desired = target.Tables.SingleOrDefault(t => t.Key == key)
                    ?? throw Error(step, "The target table is not mapped.");
                // A new table already uses the latest model and needs no historical column operations.
                if (table is null) continue;
                var column = table.Columns.SingleOrDefault(c => c.Name == step.Column);
                switch (step.Kind)
                {
                    case "rename":
                        if (column is null || table.Columns.Any(c => c.Name == step.NewName))
                            throw Error(step, "The old column must exist and the new name must be unused.");
                        ops.Add(new RenameColumnOperation { Table = step.Table, Schema = step.Schema, Name = step.Column, NewName = step.NewName! });
                        table.Columns[table.Columns.IndexOf(column)] = column with { Name = step.NewName! };
                        RenameReferences(old, table, step.Column, step.NewName!);
                        break;
                    case "drop":
                        if (column is null || desired.Columns.Any(c => c.Name == step.Column))
                            throw Error(step, "The column must exist in the old schema and be absent from the current model.");
                        if (IsConstrained(old, table, step.Column) || table.Indexes.Any(i => i.Columns.Contains(step.Column)))
                            throw Error(step, "Dropping constrained or indexed columns is not supported by this schema updater.");
                        ops.Add(new DropColumnOperation { Table = step.Table, Schema = step.Schema, Name = step.Column });
                        table.Columns.Remove(column);
                        break;
                    case "alter":
                        if (column is null) throw Error(step, "The old column does not exist.");
                        if (IsConstrained(old, table, step.Column)) throw Error(step, "Altering key or foreign-key columns is not supported.");
                        var add = CurrentColumn(initial, step);
                        if (add.IsNullable == false && column.Nullable)
                            throw Error(step, "Making a nullable column required needs a separate data backfill; this operation is not supported.");
                        var alter = new AlterColumnOperation
                        {
                            Table = step.Table, Schema = step.Schema, Name = step.Column,
                            OldColumn = new AddColumnOperation { ColumnType = column.StoreType, IsNullable = column.Nullable, ClrType = add.ClrType }
                        };
                        CopyColumn(add, alter);
                        ops.Add(alter);
                        table.Columns[table.Columns.IndexOf(column)] = ColumnState.From(add);
                        break;
                    case "required":
                        if (column is not null) throw Error(step, "AddRequiredColumn is only valid for a new column.");
                        var current = CurrentColumn(initial, step);
                        if (current.IsNullable || current.ComputedColumnSql is not null) throw Error(step, "Map a required, non-computed column.");
                        object value;
                        try { value = JsonSerializer.Deserialize(step.Value!, current.ClrType) ?? throw new JsonException("Null default."); }
                        catch (Exception ex) when (ex is JsonException or NotSupportedException)
                        { throw Error(step, $"The fill value does not match the provider column type: {ex.Message}"); }
                        required.Add(key + "\0" + step.Column, value);
                        break;
                    default: throw Error(step, "Unknown schema operation.");
                }
            }
        }

        foreach (var removed in old.Tables.Where(t => target.Tables.All(n => n.Key != t.Key)))
            throw new SchemaUpdateException($"Removing or renaming table '{removed.Name}' is not supported. Keep its mapping or migrate it separately.");

        foreach (var operation in initial)
        {
            if (operation is not (CreateTableOperation or CreateIndexOperation or EnsureSchemaOperation or AlterDatabaseOperation))
                throw new SchemaUpdateException($"Model operation '{operation.GetType().Name}' is not supported by the startup updater.");
        }
        ops.InsertRange(0, initial.OfType<EnsureSchemaOperation>().Where(s => previous is null || old.Tables.All(t => t.Schema != s.Name)));
        if (previous is null) ops.InsertRange(0, initial.OfType<AlterDatabaseOperation>());
        foreach (var desired in target.Tables)
        {
            var table = old.Tables.SingleOrDefault(t => t.Key == desired.Key);
            if (table is null)
            {
                ops.Add(initial.OfType<CreateTableOperation>().Single(t => t.Name == desired.Name && t.Schema == desired.Schema));
                ops.AddRange(initial.OfType<CreateIndexOperation>().Where(i => i.Table == desired.Name && i.Schema == desired.Schema));
                continue;
            }
            if (table.Extra != desired.Extra || SchemaState.Hash(table.Constraints.OrderBy(c => c.Name, StringComparer.Ordinal)) != SchemaState.Hash(desired.Constraints.OrderBy(c => c.Name, StringComparer.Ordinal)))
                throw new SchemaUpdateException($"Constraints or table options changed for '{table.Name}'. Automatic constraint changes are not supported.");
            foreach (var removed in table.Columns.Where(c => desired.Columns.All(n => n.Name != c.Name)))
                throw new SchemaUpdateException($"Column '{table.Name}.{removed.Name}' was removed or renamed. Declare RenameColumn or DropColumn in a named Change.");
            foreach (var column in desired.Columns)
            {
                var existing = table.Columns.SingleOrDefault(c => c.Name == column.Name);
                if (existing is not null)
                {
                    if (existing.Signature != column.Signature)
                        throw new SchemaUpdateException($"Column '{table.Name}.{column.Name}' changed. Declare AlterColumn in a named Change.");
                    continue;
                }
                var add = initial.OfType<CreateTableOperation>().Single(t => t.Name == desired.Name && t.Schema == desired.Schema)
                    .Columns.Single(c => c.Name == column.Name);
                if (!add.IsNullable && add.DefaultValue is null && add.DefaultValueSql is null && add.ComputedColumnSql is null
                    && !required.ContainsKey(table.Key + "\0" + column.Name))
                    throw new SchemaUpdateException($"Required column '{table.Name}.{column.Name}' needs HasDefaultValue or a named AddRequiredColumn change.");
                if (add.GetAnnotations().Any(a => a.Name.Contains("ValueGenerationStrategy", StringComparison.Ordinal)
                    || a.Name == "Sqlite:Autoincrement"))
                    throw new SchemaUpdateException($"Adding generated column '{table.Name}.{column.Name}' to an existing table is not supported.");
                if (required.TryGetValue(table.Key + "\0" + column.Name, out var fill))
                {
                    if (add.DefaultValueSql is not null) throw new SchemaUpdateException("Use either a model SQL default or AddRequiredColumn, not both.");
                    if (add.DefaultValue is not null && SchemaState.Hash(add.DefaultValue) != SchemaState.Hash(fill))
                        throw new SchemaUpdateException("AddRequiredColumn must match the model default when one is configured.");
                    var filled = new AddColumnOperation { Name = add.Name, Table = add.Table, Schema = add.Schema };
                    CopyColumn(add, filled);
                    filled.DefaultValue = fill;
                    ops.Add(filled);
                    if (add.DefaultValue is null)
                    {
                        var cleanup = new AlterColumnOperation { Name = add.Name, Table = add.Table, Schema = add.Schema, OldColumn = filled };
                        CopyColumn(add, cleanup);
                        ops.Add(cleanup);
                    }
                    continue;
                }
                ops.Add(add);
            }
            foreach (var index in table.Indexes)
            {
                var current = desired.Indexes.SingleOrDefault(i => i.Name == index.Name);
                if (current is null || SchemaState.Hash(current) != SchemaState.Hash(index))
                    throw new SchemaUpdateException($"Index '{index.Name}' changed or was removed. Keep its mapping; index replacement is not supported.");
            }
            ops.AddRange(initial.OfType<CreateIndexOperation>().Where(i => i.Table == desired.Name && i.Schema == desired.Schema
                && table.Indexes.All(oldIndex => oldIndex.Name != i.Name)));
        }
        target.Changes = changes.Select(c => new AppliedChange(c.Id, SchemaState.Hash(c.Steps))).ToList();
        return new SchemaPlan(target, ops, pending.Select(c => c.Id).ToArray());
    }

    private static SchemaUpdateException Error(ChangeStep step, string message)
        => new($"{step.Kind}: '{step.Table}.{step.Column}'. {message}");

    private static AddColumnOperation CurrentColumn(IReadOnlyList<MigrationOperation> initial, ChangeStep step)
        => initial.OfType<CreateTableOperation>().SingleOrDefault(t => t.Name == step.Table && t.Schema == step.Schema)?
            .Columns.SingleOrDefault(c => c.Name == step.Column) ?? throw Error(step, "The target column is not mapped.");

    private static bool IsConstrained(SchemaState state, TableState table, string name)
        => table.Constraints.Any(c => c.Columns.Contains(name) || c.Kind == "check")
            || state.Tables.Any(t => t.Constraints.Any(c => c.Kind == "fk" && c.PrincipalTable == table.Name
                && c.PrincipalSchema == table.Schema && (c.PrincipalColumns ?? []).Contains(name)));

    private static void RenameReferences(SchemaState state, TableState table, string oldName, string newName)
    {
        static string[] Replace(string[] names, string oldName, string newName) => names.Select(n => n == oldName ? newName : n).ToArray();
        table.Indexes = table.Indexes.Select(i => i with { Columns = Replace(i.Columns, oldName, newName) }).ToList();
        foreach (var t in state.Tables)
            t.Constraints = t.Constraints.Select(c => c with
            {
                Columns = t == table ? Replace(c.Columns, oldName, newName) : c.Columns,
                PrincipalColumns = c.PrincipalTable == table.Name && c.PrincipalSchema == table.Schema
                    ? Replace(c.PrincipalColumns ?? [], oldName, newName) : c.PrincipalColumns
            }).ToList();
    }

    private static void CopyColumn(ColumnOperation from, ColumnOperation to)
    {
        to.ClrType = from.ClrType; to.ColumnType = from.ColumnType; to.IsUnicode = from.IsUnicode;
        to.IsFixedLength = from.IsFixedLength; to.MaxLength = from.MaxLength; to.Precision = from.Precision;
        to.Scale = from.Scale; to.IsNullable = from.IsNullable; to.DefaultValue = from.DefaultValue;
        to.DefaultValueSql = from.DefaultValueSql; to.ComputedColumnSql = from.ComputedColumnSql;
        to.IsStored = from.IsStored; to.Comment = from.Comment; to.Collation = from.Collation;
        foreach (var annotation in from.GetAnnotations()) to.AddAnnotation(annotation.Name, annotation.Value);
    }
}
