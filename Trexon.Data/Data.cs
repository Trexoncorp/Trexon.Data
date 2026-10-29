using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Trexon.Data.Internal;

namespace Trexon.Data;

/// <summary>An immutable data source. Open one short-lived session per operation or request.</summary>
public sealed class Data : IDisposable, IAsyncDisposable
{
    private readonly DbContextOptions<DataSession> _options;
    private readonly SqliteConnection? _anchor;
    private readonly Guid _modelId = Guid.NewGuid();
    private readonly Action<ModelBuilder>[] _entities;
    private readonly ChangeDefinition[] _changes;
    private bool _disposed;
    internal DataProvider Provider { get; }

    private Data(DataConfiguration configuration)
    {
        Provider = configuration.Provider;
        _entities = configuration.Entities.ToArray();
        _changes = configuration.Changes.Select(c => new ChangeDefinition(c.Id, c.Steps.ToArray())).ToArray();
        var connectionString = configuration.ConnectionString!;
        var options = new DbContextOptionsBuilder<DataSession>();
        switch (Provider)
        {
            case DataProvider.Postgres:
                options.UseNpgsql(connectionString);
                break;
            case DataProvider.MySql:
                options.UseMySql(connectionString, ServerVersion.Parse(configuration.MySqlVersion));
                break;
            case DataProvider.Sqlite:
                var sqlite = new SqliteConnectionStringBuilder(connectionString);
                if (sqlite.DataSource == ":memory:" || sqlite.Mode == SqliteOpenMode.Memory)
                {
                    if (sqlite.DataSource is ":memory:" or "") sqlite.DataSource = $"trexon-{_modelId:N}";
                    sqlite.Mode = SqliteOpenMode.Memory;
                    sqlite.Cache = SqliteCacheMode.Shared;
                    _anchor = new SqliteConnection(sqlite.ConnectionString);
                    _anchor.Open();
                    connectionString = sqlite.ConnectionString;
                }
                options.UseSqlite(connectionString);
                break;
            default: throw new InvalidOperationException("Select a database provider.");
        }
        options.ReplaceService<IModelCacheKeyFactory, DataModelCacheKeyFactory>();
        _options = options.Options;
    }

    /// <summary>Creates an independent data source. Configuration is captured before returning.</summary>
    public static Data Create(Action<DataConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var configuration = new DataConfiguration();
        configure(configuration);
        if (configuration.Provider == DataProvider.None) throw new InvalidOperationException("Select a database provider.");
        if (configuration.Entities.Count == 0) throw new InvalidOperationException("Register at least one entity.");
        return new Data(configuration);
    }

    /// <summary>Opens an independent EF context. Dispose the returned session; never share it between parallel operations.</summary>
    public DataSession Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new DataSession(_options, _modelId, _entities);
    }

    /// <summary>Updates the schema and records its history. Call before serving application requests.</summary>
    public async Task<SchemaUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        await using var session = Open();
        return await SchemaUpdater.UpdateAsync(session, Provider, _changes, cancellationToken);
    }

    /// <summary>Releases resources owned by this data source, including an in-memory SQLite anchor.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _anchor?.Dispose();
    }

    /// <summary>Asynchronously releases resources owned by this data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}

internal sealed record ChangeDefinition(string Id, ChangeStep[] Steps);

/// <summary>Describes a completed startup schema update.</summary>
public sealed record SchemaUpdateResult(bool Changed, int OperationCount, IReadOnlyList<string> AppliedChanges);

/// <summary>Reports an unsupported, ambiguous or incomplete schema update before normal data access starts.</summary>
public sealed class SchemaUpdateException : InvalidOperationException
{
    /// <summary>Creates an exception with an explanation of the schema conflict.</summary>
    public SchemaUpdateException(string message) : base(message) { }
}
