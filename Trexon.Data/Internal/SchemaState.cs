using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Trexon.Data.Internal;

internal sealed class SchemaState
{
    public int Format { get; set; } = 1;
    public string Provider { get; set; } = "";
    public string Database { get; set; } = "";
    public bool Pending { get; set; }
    public List<TableState> Tables { get; set; } = [];
    public List<AppliedChange> Changes { get; set; } = [];

    public static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string Key(string table, string? schema) => $"{schema ?? ""}\0{table}";

    public static SchemaState From(IReadOnlyList<MigrationOperation> operations, DataProvider provider)
    {
        var state = new SchemaState { Provider = provider.ToString(), Database = Hash(operations.OfType<AlterDatabaseOperation>().Select(Annotations)) };
        foreach (var table in operations.OfType<CreateTableOperation>())
        {
            var item = new TableState
            {
                Name = table.Name, Schema = table.Schema,
                Extra = Hash(new { table.Comment, Annotations = Annotations(table) }),
                Columns = table.Columns.Select(ColumnState.From).ToList()
            };
            if (table.PrimaryKey is { } pk)
                item.Constraints.Add(new("pk", pk.Name, pk.Columns, Extra: Hash(Annotations(pk))));
            foreach (var unique in table.UniqueConstraints)
                item.Constraints.Add(new("unique", unique.Name, unique.Columns, Extra: Hash(Annotations(unique))));
            foreach (var fk in table.ForeignKeys)
                item.Constraints.Add(new("fk", fk.Name, fk.Columns, fk.PrincipalTable, fk.PrincipalSchema,
                    fk.PrincipalColumns ?? [], Extra: Hash(new { fk.OnDelete, Annotations = Annotations(fk) })));
            foreach (var check in table.CheckConstraints)
                item.Constraints.Add(new("check", check.Name, [], Sql: check.Sql, Extra: Hash(Annotations(check))));
            foreach (var index in operations.OfType<CreateIndexOperation>().Where(i => i.Table == table.Name && i.Schema == table.Schema))
                item.Indexes.Add(new("index", index.Name, index.Columns,
                    Sql: index.Filter, Extra: Hash(new { index.IsUnique, index.IsDescending, Annotations = Annotations(index) })));
            state.Tables.Add(item);
        }
        return state;
    }

    public static string[] Annotations(MigrationOperation operation) => operation.GetAnnotations()
        .OrderBy(a => a.Name, StringComparer.Ordinal)
        .Select(a => a.Name + "=" + Convert.ToString(a.Value, CultureInfo.InvariantCulture)).ToArray();
}

internal sealed class TableState
{
    public string Name { get; set; } = "";
    public string? Schema { get; set; }
    public string Extra { get; set; } = "";
    public string Physical { get; set; } = "";
    public List<ColumnState> Columns { get; set; } = [];
    public List<ArtifactState> Constraints { get; set; } = [];
    public List<ArtifactState> Indexes { get; set; } = [];
    public string Key => SchemaState.Key(Name, Schema);
}

internal sealed record ColumnState(string Name, string? StoreType, bool Nullable, string Signature)
{
    public static ColumnState From(ColumnOperation column) => new(column.Name, column.ColumnType, column.IsNullable,
        SchemaState.Hash(new
        {
            ClrType = column.ClrType.FullName, column.ColumnType, column.IsNullable,
            column.IsUnicode, column.IsFixedLength, column.MaxLength, column.Precision, column.Scale,
            column.DefaultValue, column.DefaultValueSql, column.ComputedColumnSql, column.IsStored,
            column.Collation, column.Comment, Annotations = SchemaState.Annotations(column)
        }));
}

internal sealed record ArtifactState(string Kind, string Name, string[] Columns,
    string? PrincipalTable = null, string? PrincipalSchema = null, string[]? PrincipalColumns = null,
    string? Sql = null, string? Extra = null);
internal sealed record AppliedChange(string Id, string Hash);
