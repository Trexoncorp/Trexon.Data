using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Trexon.Data;

/// <summary>Configures a data source, its EF model and named schema changes.</summary>
public sealed class DataConfiguration
{
    internal string? ConnectionString;
    internal DataProvider Provider;
    internal string MySqlVersion = "8.0.0";
    internal readonly List<Action<ModelBuilder>> Entities = [];
    internal readonly List<SchemaChange> Changes = [];

    /// <summary>Selects PostgreSQL. The database must already exist.</summary>
    public DataConfiguration UsePostgres(string connectionString) => Use(DataProvider.Postgres, connectionString);

    /// <summary>Selects MySQL. Supply the actual server version when it differs from MySQL 8.0.</summary>
    public DataConfiguration UseMySql(string connectionString, string serverVersion = "8.0.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);
        _ = ServerVersion.Parse(serverVersion);
        Use(DataProvider.MySql, connectionString);
        MySqlVersion = serverVersion;
        return this;
    }

    /// <summary>Selects SQLite. File databases are created when first opened.</summary>
    public DataConfiguration UseSqlite(string connectionString) => Use(DataProvider.Sqlite, connectionString);

    /// <summary>Registers an entity with optional standard EF Core mapping.</summary>
    public DataConfiguration Entity<T>(Action<EntityTypeBuilder<T>>? configure = null) where T : class
    {
        Entities.Add(model =>
        {
            var entity = model.Entity<T>();
            configure?.Invoke(entity);
        });
        return this;
    }

    /// <summary>Registers a uniquely named schema change. Applied changes must retain their order and definition.</summary>
    public DataConfiguration Change(string id, Action<SchemaChange> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(configure);
        if (Changes.Any(c => c.Id == id)) throw new ArgumentException($"Duplicate change '{id}'.", nameof(id));
        var change = new SchemaChange(id);
        configure(change);
        if (change.Steps.Count == 0) throw new ArgumentException("A change must contain at least one operation.", nameof(configure));
        Changes.Add(change);
        return this;
    }

    private DataConfiguration Use(DataProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (Provider != DataProvider.None) throw new InvalidOperationException("Select exactly one database provider.");
        Provider = provider;
        ConnectionString = connectionString;
        return this;
    }
}

internal enum DataProvider { None, Sqlite, Postgres, MySql }
