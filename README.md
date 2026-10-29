# Trexon.Data

[English](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.md) · [Русский](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.ru.md) · [Deutsch](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.de.md)

A small EF Core layer for C# / .NET 8+: LINQ, PostgreSQL, MySQL and SQLite,
short-lived sessions, and schema updates from application code. No custom
`DbContext`, `DbSet` properties or `dotnet ef` commands are required.

**Status:** `1.0.0-dev`. The schema updater intentionally supports a limited set
of changes. This is not a replacement for every EF migration scenario.

## Start with one model

```csharp
using Microsoft.EntityFrameworkCore;
using Trexon.Data;

await using var data = Data.Create(config => config
    .UseSqlite("Data Source=app.db")
    .Entity<User>());

await data.UpdateAsync(); // Before starting requests or background workers.

await using var db = data.Open();
db.Add(new User { Name = "Alice" });
await db.SaveAsync();

var names = await db.Table<User>().AsNoTracking()
    .Where(user => user.Name.StartsWith("A"))
    .OrderBy(user => user.Name)
    .Select(user => user.Name)
    .Take(20)
    .ToListAsync();

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
```

`Table<T>()` returns a real `DbSet<T>`. Standard EF LINQ, `Include`, projections,
tracking, bulk operations, transactions and cancellation remain available.
`Add`, `Remove` and tracked edits are saved by `SaveAsync`; disposing does not save.
EF's `ExecuteUpdateAsync` and `ExecuteDeleteAsync` execute immediately.

## Choose a database

Select exactly one provider:

```csharp
config.UsePostgres(connectionString);
config.UseMySql(connectionString, serverVersion: "8.4.0");
config.UseSqlite("Data Source=app.db");
```

PostgreSQL and MySQL databases must already exist; Trexon.Data manages their
mapped tables. SQLite files are created when opened. `Data Source=:memory:`
is kept alive by the `Data` instance and shared by its sessions.
Dispose `Data` after all sessions finish.

One package includes Npgsql, Pomelo and the Microsoft SQLite provider. EF Core 9
is the common provider baseline; the library targets .NET 8. MySQL defaults to
server version `8.0.0`; provide the real version rather than relying on network
autodetection. MariaDB has not been validated.

## Update the schema at startup

Call `await data.UpdateAsync(cancellationToken)` once before normal data access.
It compares the model with stored history and checks the managed physical schema.
A repeated call returns `Changed == false` when nothing changed.

| Model change | Behavior |
| --- | --- |
| New table, its keys, relationships and indexes | Created automatically |
| New nullable column | Added automatically |
| New required column | Requires a model default or an explicit fill value |
| New index on an existing table | Added automatically; existing data must satisfy it |
| Column rename | Requires `RenameColumn` |
| Column removal | Requires `DropColumn`; constrained/indexed columns are rejected |
| Column type/default/mapping change | Requires `AlterColumn`; key/FK changes and nullable-to-required are rejected |
| Table removal/rename, existing constraint/index replacement | Rejected |

Describe ambiguous changes using stable IDs and **physical database names**:

```csharp
config.Entity<User>(entity =>
{
    entity.ToTable("Users");
    entity.Property(user => user.Name).HasColumnName("FullName");
});
config.Change("001-rename-name", change => change
    .RenameColumn("Users", "Name", "FullName"));
```

The original database must have `Users.Name`. A fresh database is created directly
from the latest model and records historical changes without replaying them.
Keep applied changes in their original order and do not edit their definitions.

For a new required `Score` property:

```csharp
config.Change("002-add-score", change => change
    .AddRequiredColumn("Users", "Score", 0));
```

Existing rows receive the supplied constant. The temporary fill default is
removed unless the model also defines one. A C# property initializer is not a database default. For complex mappings, use the standard
`EntityTypeBuilder<T>` callback in `Entity<T>()`.

## ASP.NET Core

```csharp
builder.Services.AddTrexonData(config => config
    .UsePostgres(builder.Configuration.GetConnectionString("Database")!)
    .Entity<User>());

var app = builder.Build();
await app.Services.GetRequiredService<Data>().UpdateAsync();
```

Inject `DataSession` into scoped services. `Data` is a singleton; sessions are
scoped. A session is not thread-safe: use independent sessions for parallel work.
Register this DI integration once per service collection.

## Boundaries

The updater owns the mapped tables and `__TrexonDataHistory`; it rejects existing
mapped tables without its history. Do not combine it with `EnsureCreated`, EF
migrations or manual DDL on managed tables. Unmapped tables are left alone.
Schema creation assumes a trusted model and database permissions to execute DDL.

PostgreSQL/SQLite updates run in a transaction. MySQL DDL cannot be rolled back as
a batch: a durable pending marker blocks further updates after an interrupted or
failed update. Restore a known-good database **with its matching history** before
retrying. Do not clear the marker blindly. Concurrent updater calls are serialized
by a database lock; normal application queries do not acquire that lock. Complete
updates before accepting traffic, and coordinate updates across running versions.

Sequences, model seeding (`HasData`), table/constraint/index replacement and
arbitrary SQL data migrations are outside this version. Changes operate against
the final model: operations requiring an intermediate mapping are rejected. A new
table with a relationship to an existing table is supported; adding a relationship
to an already managed table is a constraint change and is rejected.
See [schema behavior](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/SCHEMA.md) for details and limitations.

## Build and verify

```sh
dotnet build Trexon.Data.slnx --configuration Release -warnaserror
dotnet test --solution Trexon.Data.slnx --configuration Release --no-build
dotnet run --project examples/Trexon.Data.Sample --configuration Release --no-build
dotnet pack Trexon.Data/Trexon.Data.csproj --configuration Release --no-build --output artifacts -warnaserror
```

Tests run on .NET 8 and .NET 10. SQLite tests always run; PostgreSQL/MySQL tests
require the explicit test connection environment variables described in
[development](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/DEVELOPMENT.md). CI runs all three databases.

Licensed under [MIT](https://github.com/Trexoncorp/Trexon.Data/blob/main/LICENSE).
