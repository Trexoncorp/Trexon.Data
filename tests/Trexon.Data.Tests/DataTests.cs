using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace Trexon.Data.Tests;

public sealed class DataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string> Providers => new() { "sqlite", "postgres", "mysql" };

    private static void V1(DataConfiguration c) => c.Entity<Person>(e =>
    {
        e.ToTable("People"); e.Ignore(x => x.Note); e.Ignore(x => x.Score); e.Property(x => x.Name).HasMaxLength(40);
    });
    private static void V2(DataConfiguration c) => c.Entity<Person>(e =>
    {
        e.ToTable("People"); e.Ignore(x => x.Score); e.Property(x => x.Name).HasMaxLength(40);
    });

    [Theory, MemberData(nameof(Providers))]
    public async Task Linq_tracking_sessions_and_save_are_standard_ef(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var data = store.Open(V1);
        Assert.True((await data.UpdateAsync(Ct)).Changed);
        Assert.False((await data.UpdateAsync(Ct)).Changed);
        await using (var db = data.Open())
        {
            db.Add(new Person { Name = "Alice" }); db.Add(new Person { Name = "Bob" });
            Assert.Equal(0, await db.Table<Person>().CountAsync(Ct));
            Assert.Equal(2, await db.SaveAsync(Ct));
            var person = await db.Table<Person>().SingleAsync(x => x.Name == "Alice", Ct);
            person.Name = "Alice updated";
            await db.SaveAsync(Ct);
        }
        await using (var db = data.Open())
        {
            var names = await db.Table<Person>().AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).Take(1).ToListAsync(Ct);
            Assert.Equal(new[] { "Alice updated" }, names);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        await using (var db = data.Open()) { db.Add(new Person { Name = "Unsaved" }); }
        await using (var db = data.Open()) { Assert.Equal(2, await db.Table<Person>().CountAsync(Ct)); }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Nullable_column_and_new_table_are_added_without_losing_rows(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using (var first = store.Open(V1))
        {
            await first.UpdateAsync(Ct);
            await using var db = first.Open(); db.Add(new Person { Name = "Existing" }); await db.SaveAsync(Ct);
        }
        await using var next = store.Open(c => { V2(c); c.Entity<Audit>(); });
        Assert.True((await next.UpdateAsync(Ct)).Changed);
        Assert.False((await next.UpdateAsync(Ct)).Changed);
        await using var session = next.Open();
        var person = await session.Table<Person>().SingleAsync(Ct);
        Assert.Null(person.Note); person.Note = "New field";
        session.Add(new Audit { Message = "Created" }); await session.SaveAsync(Ct);
        Assert.Equal(1, await session.Table<Audit>().CountAsync(Ct));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Required_column_needs_a_fill_value_and_explicit_change_runs_once(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using (var first = store.Open(V2))
        {
            await first.UpdateAsync(Ct);
            await using var db = first.Open(); db.Add(new Person { Name = "Existing" }); await db.SaveAsync(Ct);
        }
        Action<DataConfiguration> target = c => c.Entity<Person>(e => { e.ToTable("People"); e.Property(x => x.Name).HasMaxLength(40); });
        await using (var invalid = store.Open(target))
            Assert.Contains("Required column", (await Assert.ThrowsAsync<SchemaUpdateException>(() => invalid.UpdateAsync(Ct))).Message);
        await using var next = store.Open(c =>
        {
            target(c); c.Change("001-score", m => m.AddRequiredColumn("People", "Score", 10));
        });
        Assert.Equal(new[] { "001-score" }, (await next.UpdateAsync(Ct)).AppliedChanges);
        Assert.False((await next.UpdateAsync(Ct)).Changed);
        await using var session = next.Open();
        Assert.Equal(10, (await session.Table<Person>().SingleAsync(Ct)).Score);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Rename_preserves_values_and_missing_rename_is_rejected(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using (var first = store.Open(V1))
        {
            await first.UpdateAsync(Ct);
            await using var db = first.Open(); db.Add(new Person { Name = "Keep me" }); await db.SaveAsync(Ct);
        }
        Action<DataConfiguration> renamed = c => c.Entity<Person>(e =>
        {
            e.ToTable("People"); e.Ignore(x => x.Note); e.Ignore(x => x.Score);
            e.Property(x => x.Name).HasColumnName("FullName").HasMaxLength(40);
        });
        await using (var invalid = store.Open(renamed))
            await Assert.ThrowsAsync<SchemaUpdateException>(() => invalid.UpdateAsync(Ct));
        await using var next = store.Open(c => { renamed(c); c.Change("001-rename", m => m.RenameColumn("People", "Name", "FullName")); });
        await next.UpdateAsync(Ct); Assert.False((await next.UpdateAsync(Ct)).Changed);
        await using var session = next.Open(); Assert.Equal("Keep me", (await session.Table<Person>().SingleAsync(Ct)).Name);
        await using var edited = store.Open(c => { renamed(c); c.Change("001-rename", m => m.RenameColumn("People", "Other", "FullName")); });
        Assert.Contains("edited", (await Assert.ThrowsAsync<SchemaUpdateException>(() => edited.UpdateAsync(Ct))).Message);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Explicit_drop_and_alter_preserve_other_columns(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using (var first = store.Open(V2))
        {
            await first.UpdateAsync(Ct); await using var db = first.Open();
            db.Add(new Person { Name = "Keep", Note = "Remove" }); await db.SaveAsync(Ct);
        }
        await using var next = store.Open(c =>
        {
            c.Entity<Person>(e => { e.ToTable("People"); e.Ignore(x => x.Note); e.Ignore(x => x.Score); e.Property(x => x.Name).HasMaxLength(100); });
            c.Change("001-cleanup", m => m.DropColumn("People", "Note").AlterColumn("People", "Name"));
        });
        await next.UpdateAsync(Ct); Assert.False((await next.UpdateAsync(Ct)).Changed);
        await using var session = next.Open(); Assert.Equal("Keep", (await session.Table<Person>().SingleAsync(Ct)).Name);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Fresh_database_uses_latest_model_and_records_historical_changes(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var data = store.Open(c =>
        {
            V1(c);
            c.Change("001-rename", m => m.RenameColumn("People", "OldName", "Name"));
            c.Change("002-drop", m => m.DropColumn("People", "Legacy"));
        });
        Assert.Equal(2, (await data.UpdateAsync(Ct)).AppliedChanges.Count);
        Assert.False((await data.UpdateAsync(Ct)).Changed);
        await using var db = data.Open(); db.Add(new Person { Name = "Latest" }); await db.SaveAsync(Ct);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Relationships_and_indexes_use_the_provider_and_linq_include(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var data = store.Open(c =>
        {
            V1(c);
            c.Entity<Purchase>(e => { e.ToTable("Purchases"); e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId); e.HasIndex(x => x.Number).IsUnique(); });
        });
        await data.UpdateAsync(Ct); Assert.False((await data.UpdateAsync(Ct)).Changed);
        await using (var db = data.Open())
        {
            db.Add(new Purchase { Number = "A1", Person = new Person { Name = "Owner" } }); await db.SaveAsync(Ct);
        }
        await using (var db = data.Open())
            Assert.Equal("Owner", (await db.Table<Purchase>().Include(x => x.Person).SingleAsync(Ct)).Person.Name);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Concurrent_startup_has_one_creator_and_one_noop(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var first = store.Open(V1); await using var second = store.Open(V1);
        var results = await Task.WhenAll(Task.Run(() => first.UpdateAsync(Ct), Ct), Task.Run(() => second.UpdateAsync(Ct), Ct));
        Assert.Single(results, r => r.Changed);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task External_index_drift_is_rejected_and_can_be_restored(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var data = store.Open(V1); await data.UpdateAsync(Ct);
        await using var db = data.Open();
        var sql = db.GetService<ISqlGenerationHelper>();
        var index = sql.DelimitIdentifier("unmanaged");
        var table = sql.DelimitIdentifier("People");
        var column = sql.DelimitIdentifier("Name");
        // Identifiers are quoted by the provider; these commands contain no user-supplied values.
        var createIndex = $"CREATE INDEX {index} ON {table} ({column});";
        await db.Database.ExecuteSqlRawAsync(createIndex, Ct);
        Assert.Contains("outside Trexon.Data", (await Assert.ThrowsAsync<SchemaUpdateException>(() => data.UpdateAsync(Ct))).Message);
        await db.Database.ExecuteSqlRawAsync(provider == "mysql" ? $"DROP INDEX {index} ON {table};" : $"DROP INDEX {index};", Ct);
        Assert.False((await data.UpdateAsync(Ct)).Changed);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Failed_ddl_rolls_back_or_leaves_a_durable_mysql_guard(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var original = store.Open(V1); await original.UpdateAsync(Ct);
        await using (var db = original.Open())
        {
            db.Add(new Person { Name = "Duplicate" }); db.Add(new Person { Name = "Duplicate" }); await db.SaveAsync(Ct);
        }
        await using var changed = store.Open(c =>
        {
            V1(c); c.Entity<Person>(e => e.HasIndex(x => x.Name).IsUnique());
        });
        await Assert.ThrowsAnyAsync<DbException>(() => changed.UpdateAsync(Ct));
        if (provider == "mysql")
            Assert.Contains("did not complete", (await Assert.ThrowsAsync<SchemaUpdateException>(() => original.UpdateAsync(Ct))).Message);
        else Assert.False((await original.UpdateAsync(Ct)).Changed);
        await using var verify = original.Open(); Assert.Equal(2, await verify.Table<Person>().CountAsync(Ct));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Cancellation_before_start_leaves_the_database_usable(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var data = store.Open(V1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => data.UpdateAsync(cancellation.Token));
        Assert.True((await data.UpdateAsync(Ct)).Changed);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Several_pending_renames_can_skip_an_intermediate_release(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using (var original = store.Open(V1))
        {
            await original.UpdateAsync(Ct); await using var db = original.Open();
            db.Add(new Person { Name = "Keep through both renames" }); await db.SaveAsync(Ct);
        }
        await using var latest = store.Open(c =>
        {
            c.Entity<Person>(e => { e.ToTable("People"); e.Ignore(x => x.Note); e.Ignore(x => x.Score); e.Property(x => x.Name).HasColumnName("FullName").HasMaxLength(40); });
            c.Change("001-first", m => m.RenameColumn("People", "Name", "DisplayName"));
            c.Change("002-second", m => m.RenameColumn("People", "DisplayName", "FullName"));
        });
        Assert.Equal(2, (await latest.UpdateAsync(Ct)).AppliedChanges.Count);
        Assert.False((await latest.UpdateAsync(Ct)).Changed);
        await using var verify = latest.Open(); Assert.Equal("Keep through both renames", (await verify.Table<Person>().SingleAsync(Ct)).Name);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Required_fill_is_temporary_and_fresh_and_upgraded_schemas_match(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using (var original = store.Open(V2))
        {
            await original.UpdateAsync(Ct); await using var db = original.Open(); db.Add(new Person { Name = "Before" }); await db.SaveAsync(Ct);
        }
        await using var latest = store.Open(c =>
        {
            c.Entity<Person>(e => { e.ToTable("People"); e.Property(x => x.Name).HasMaxLength(40); });
            c.Change("001-score", m => m.AddRequiredColumn("People", "Score", 10));
        });
        await latest.UpdateAsync(Ct);
        await using var verify = latest.Open();
        var sql = verify.GetService<ISqlGenerationHelper>();
        Assert.Equal(10, (await verify.Table<Person>().SingleAsync(Ct)).Score);
        // Without a model default, omitting the new required field must fail after the backfill.
        var missingScore = $"INSERT INTO {sql.DelimitIdentifier("People")} ({sql.DelimitIdentifier("Name")}) VALUES ('Missing score');";
        await Assert.ThrowsAnyAsync<DbException>(() => verify.Database.ExecuteSqlRawAsync(missingScore, Ct));
        Assert.False((await latest.UpdateAsync(Ct)).Changed);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Generated_columns_and_type_aliases_remain_stable_across_updates(string provider)
    {
        await using var store = await TestDatabase.CreateAsync(provider);
        await using var data = store.Open(c => c.Entity<Person>(e =>
        {
            e.ToTable("People"); e.Ignore(x => x.Note);
            e.Property(x => x.Name).HasColumnType(provider == "sqlite" ? "TEXT" : "varchar(40)");
            e.Property(x => x.Score).HasComputedColumnSql(provider == "postgres" ? "length(\"Name\")" : "length(Name)", stored: true);
        }));
        await data.UpdateAsync(Ct);
        await using (var db = data.Open()) { db.Add(new Person { Name = "Four" }); await db.SaveAsync(Ct); }
        Assert.False((await data.UpdateAsync(Ct)).Changed);
        await using var verify = data.Open(); Assert.Equal(4, (await verify.Table<Person>().SingleAsync(Ct)).Score);
    }

    [Fact]
    public async Task Dependency_injection_owns_a_single_source_and_independent_scoped_sessions()
    {
        var services = new ServiceCollection();
        services.AddTrexonData(c => { c.UseSqlite("Data Source=:memory:"); V1(c); });
        await using var provider = services.BuildServiceProvider();
        var data = provider.GetRequiredService<Data>(); await data.UpdateAsync(Ct);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataSession>();
            Assert.Same(db, scope.ServiceProvider.GetRequiredService<DataSession>());
            db.Add(new Person { Name = "Scoped" }); await db.SaveAsync(Ct);
        }
        await using var next = provider.CreateAsyncScope();
        Assert.Equal(1, await next.ServiceProvider.GetRequiredService<DataSession>().Table<Person>().CountAsync(Ct));
    }

    [Fact]
    public async Task In_memory_sqlite_survives_sessions_and_models_are_isolated()
    {
        await using var first = Data.Create(c => { c.UseSqlite("Data Source=:memory:"); V1(c); });
        await using var second = Data.Create(c => { c.UseSqlite("Data Source=:memory:"); V2(c); });
        await first.UpdateAsync(Ct); await second.UpdateAsync(Ct);
        await using (var db = first.Open()) { db.Add(new Person { Name = "First" }); await db.SaveAsync(Ct); }
        await using (var db = second.Open()) { Assert.Empty(await db.Table<Person>().ToListAsync(Ct)); db.Add(new Person { Name = "Second", Note = "Mapped" }); await db.SaveAsync(Ct); }
        await using (var db = first.Open()) Assert.Equal("First", (await db.Table<Person>().SingleAsync(Ct)).Name);
        await first.DisposeAsync(); Assert.Throws<ObjectDisposedException>(() => first.Open());
    }

    [Fact]
    public void Configuration_requires_one_provider_and_entities()
    {
        Assert.Throws<InvalidOperationException>(() => Data.Create(c => c.Entity<Person>()));
        Assert.Throws<InvalidOperationException>(() => Data.Create(c => c.UseSqlite("Data Source=:memory:")));
        Assert.Throws<InvalidOperationException>(() => Data.Create(c => c.UseSqlite("Data Source=a.db").UsePostgres("Host=localhost")));
        Assert.Throws<ArgumentException>(() => Data.Create(c => c.UseSqlite(" ")));
    }

    public sealed class Person { public int Id { get; set; } public string Name { get; set; } = ""; public string? Note { get; set; } public int Score { get; set; } }
    public sealed class Audit { public int Id { get; set; } public string Message { get; set; } = ""; }
    public sealed class Purchase { public int Id { get; set; } public string Number { get; set; } = ""; public int PersonId { get; set; } public Person Person { get; set; } = null!; }
}
