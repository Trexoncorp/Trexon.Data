using System.Text.Json;

namespace Trexon.Data;

/// <summary>Explicit schema operations, expressed using database table and column names.</summary>
public sealed class SchemaChange
{
    internal string Id { get; }
    internal List<ChangeStep> Steps { get; } = [];
    internal SchemaChange(string id) => Id = id;

    /// <summary>Renames an existing column, preserving its values. Keep the target property mapped to the new name.</summary>
    public SchemaChange RenameColumn(string table, string oldName, string newName, string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        return Add("rename", table, oldName, schema, newName);
    }

    /// <summary>Explicitly drops a column and its data. Remove the property from the current EF model.</summary>
    public SchemaChange DropColumn(string table, string column, string? schema = null)
        => Add("drop", table, column, schema);

    /// <summary>Explicitly alters a column to its current EF mapping. Existing values must be convertible.</summary>
    public SchemaChange AlterColumn(string table, string column, string? schema = null)
        => Add("alter", table, column, schema);

    /// <summary>Adds a required column using a non-null constant to populate existing rows.</summary>
    /// <remarks>The fill default is removed after backfilling unless the current model also configures that default.</remarks>
    public SchemaChange AddRequiredColumn<T>(string table, string column, T defaultValue, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return Add("required", table, column, schema, value: JsonSerializer.Serialize(defaultValue));
    }

    private SchemaChange Add(string kind, string table, string column, string? schema, string? newName = null, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (newName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        Steps.Add(new(kind, table, column, schema, newName, value));
        return this;
    }
}

internal sealed record ChangeStep(string Kind, string Table, string Column, string? Schema, string? NewName, string? Value);
