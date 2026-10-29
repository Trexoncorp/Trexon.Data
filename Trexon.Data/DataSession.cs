using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Trexon.Data;

/// <summary>A short-lived EF Core context with LINQ, change tracking and explicit saving.</summary>
public sealed class DataSession : DbContext
{
    internal Guid ModelId { get; }
    private readonly Action<ModelBuilder>[] _entities;

    internal DataSession(DbContextOptions<DataSession> options, Guid modelId, Action<ModelBuilder>[] entities) : base(options)
        => (ModelId, _entities) = (modelId, entities);

    /// <summary>Returns the standard EF set. Queries track entities by default; use AsNoTracking for read-only queries.</summary>
    public DbSet<T> Table<T>() where T : class => Set<T>();

    /// <summary>Saves all tracked changes using EF Core's normal transaction behavior.</summary>
    public Task<int> SaveAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var configure in _entities) configure(modelBuilder);
    }
}

internal sealed class DataModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => context is DataSession session ? (typeof(DataSession), session.ModelId, designTime) : (object)(context.GetType(), designTime);
}
