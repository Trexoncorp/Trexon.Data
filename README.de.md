# Trexon.Data

[English](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.md) · [Русский](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.ru.md) · [Deutsch](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.de.md)

Eine kleine EF-Core-Schicht für C# / .NET 8+: LINQ, PostgreSQL, MySQL und SQLite,
kurzlebige Sitzungen und Schemaänderungen beim Programmstart. Ein eigener
`DbContext` und `dotnet ef` sind nicht erforderlich. Status: `1.0.0-dev`.

```csharp
using Microsoft.EntityFrameworkCore;
using Trexon.Data;

await using var data = Data.Create(config => config
    .UseSqlite("Data Source=app.db")
    .Entity<User>());

await data.UpdateAsync();
await using var db = data.Open();
db.Add(new User { Name = "Alice" });
await db.SaveAsync();
var users = await db.Table<User>().AsNoTracking()
    .Where(user => user.Name.StartsWith("A"))
    .OrderBy(user => user.Name).Take(20).ToListAsync();

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
```

Alternativen: `UsePostgres(connectionString)` oder
`UseMySql(connectionString, serverVersion: "8.4.0")`. Die Serverdatenbank muss
bereits existieren. SQLite-Dateien werden beim Öffnen angelegt. Bei
`Data Source=:memory:` hält `Data` die Datenbank für seine Sitzungen offen.
Alle drei Provider sind im Paket enthalten; gemeinsame EF-Core-Version: 9.
MariaDB wurde nicht getestet.

`Table<T>()` liefert `DbSet<T>`; LINQ, Include und Tracking bleiben erhalten.
Änderungen werden mit `SaveAsync()` gespeichert, nicht beim Dispose.
`ExecuteUpdateAsync` und `ExecuteDeleteAsync` wirken sofort.
Eine Sitzung darf nicht für parallele Vorgänge geteilt werden.

`UpdateAsync()` muss vor der Verarbeitung normaler Anfragen abgeschlossen sein.
Neue Tabellen, nullable Spalten und neue Indizes werden automatisch angelegt.
Neue Pflichtspalten benötigen einen Modellstandardwert oder eine benannte Änderung:

```csharp
config.Change("001-score", change => change
    .AddRequiredColumn("Users", "Score", 0));
```

Weitere explizite Operationen: `RenameColumn`, `DropColumn`, `AlterColumn`.
Sie verwenden physische Tabellen- und Spaltennamen. Angewendete Änderungen müssen
mit derselben Definition und Reihenfolge erhalten bleiben. Eine neue Datenbank
wird direkt aus dem aktuellen Modell erzeugt. Der temporäre Standardwert für das
Auffüllen wird entfernt, sofern das Modell keinen solchen Wert vorgibt.

Tabellenumbenennungen/-löschungen, Änderungen bestehender Constraints/Indizes,
Änderungen an Schlüssel-/FK-Spalten, nullable → required, `HasData`, Sequenzen und
beliebige SQL-Datenmigrationen werden nicht unterstützt. Indexierte oder durch
Constraints gebundene Spalten können nicht mit `DropColumn` entfernt werden.
Vorhandene Tabellen ohne Trexon-Historie werden nicht automatisch übernommen.
Keine Kombination mit `EnsureCreated`, EF-Migrationen oder manuellem DDL.

PostgreSQL/SQLite verwenden eine Transaktion. MySQL-DDL kann teilweise angewendet
bleiben; eine dauerhaft gespeicherte Markierung blockiert danach weitere Updates.
Eine bekannte vollständige Datenbankkopie einschließlich passender Historie muss
wiederhergestellt werden. Die Markierung nicht einfach löschen.
Eine Datenbanksperre serialisiert Schema-Updates, nicht normale Abfragen.

## Beziehungen: 1:1, 1:N und N:M

Die Navigationseigenschaften stammen aus dem
[vollständigen ausführbaren Beispiel](https://github.com/Trexoncorp/Trexon.Data/blob/main/examples/Trexon.Data.Sample/Program.cs).
Die Konfiguration verwendet die Standard-API von EF Core:

```csharp
config.Entity<User>(entity =>
{
    entity.HasOne(user => user.Profile)
        .WithOne(profile => profile.User)
        .HasForeignKey<UserProfile>(profile => profile.UserId);

    entity.HasMany(user => user.Posts)
        .WithOne(post => post.Author)
        .HasForeignKey(post => post.AuthorId);

    entity.HasMany(user => user.Roles)
        .WithMany(role => role.Users)
        .UsingEntity("UserRoles");
});
```

Das Beispiel speichert zwei Benutzer mit Profilen, Beiträgen und gemeinsamen
Rollen. Eine neue Sitzung lädt alle Beziehungen mit `Include`; auch die
Gegenrichtung `Role.Users` wird gezeigt. EF verwaltet die Tabelle `UserRoles`.
Beziehungen werden mit neuen Tabellen erstellt. Neue oder geänderte Fremdschlüssel
in bestehenden Tabellen werden vom Updater abgelehnt. Eine neue N:M-Zwischentabelle
ist möglich, solange bestehende Constraints unverändert bleiben.
[Ausführliche Anleitung (Russisch)](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/RELATIONSHIPS.md).

## ASP.NET Core

`services.AddTrexonData(config => ...)` registriert `Data` als
Singleton und `DataSession` pro Scope. `UpdateAsync()` wird explizit beim Start
aufgerufen. Die Integration einmal registrieren.

[Ausführliche englische Anleitung](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.md), [Schema](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/SCHEMA.md),
[Entwicklung](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/DEVELOPMENT.md), [MIT-Lizenz](https://github.com/Trexoncorp/Trexon.Data/blob/main/LICENSE).
