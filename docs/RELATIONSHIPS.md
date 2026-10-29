# Связи 1:1, 1:М и М:М

Trexon.Data передаёт настройки `Entity<T>()` обычному EF Core
`EntityTypeBuilder<T>`. Связи описываются навигационными свойствами и стандартными
`HasOne`, `HasMany`, `WithOne`, `WithMany`, `HasForeignKey`.

Полностью рабочая программа находится в
[examples/Trexon.Data.Sample/Program.cs](../examples/Trexon.Data.Sample/Program.cs).
Запуск из корня репозитория:

```sh
dotnet run --project examples/Trexon.Data.Sample --configuration Release
```

Пример использует SQLite в памяти, создаёт собственную БД при каждом запуске и не
требует сервера. Для выполнения используется SQLite-провайдер; настройки модели
передаются и другим провайдерам. Возможности конкретного сервера по-прежнему
определяются EF-провайдером.

## Модель примера

| Связь | Навигации | Структура в БД |
| --- | --- | --- |
| 1:1 | `User.Profile` ↔ `UserProfile.User` | FK `Profiles.UserId` и уникальный индекс на него |
| 1:М | `User.Posts` ↔ `Post.Author` | FK `Posts.AuthorId`; у автора несколько публикаций |
| М:М | `User.Roles` ↔ `Role.Users` | Промежуточная таблица `UserRoles` с двумя FK |

```csharp
public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public UserProfile? Profile { get; set; }
    public List<Post> Posts { get; set; } = [];
    public List<Role> Roles { get; set; } = [];
}

public sealed class UserProfile
{
    public int Id { get; set; }
    public string Bio { get; set; } = "";
    public int UserId { get; set; }
    public User User { get; set; } = null!;
}

public sealed class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
}

public sealed class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<User> Users { get; set; } = [];
}
```

Коллекции инициализируются пустыми списками. `UserProfile.UserId` и `Post.AuthorId`
имеют тип `int`: каждый профиль и каждая публикация принадлежат пользователю.
При этом пользователь может существовать без профиля: наличие зависимой строки
не обеспечивается одним FK. Поэтому `User.Profile` объявлено nullable.

## Настройка связей

Нужны `using Microsoft.EntityFrameworkCore;` и `using Trexon.Data;`.

```csharp
await using var data = Data.Create(config => config
    .UseSqlite("Data Source=:memory:")
    .Entity<User>(entity =>
    {
        entity.ToTable("Users");

        // 1:1 — one profile per user; every profile belongs to one user.
        entity.HasOne(user => user.Profile)
            .WithOne(profile => profile.User)
            .HasForeignKey<UserProfile>(profile => profile.UserId);

        // 1:N — one author can have many posts.
        entity.HasMany(user => user.Posts)
            .WithOne(post => post.Author)
            .HasForeignKey(post => post.AuthorId);

        // N:M — users share roles; EF creates the UserRoles join table.
        entity.HasMany(user => user.Roles)
            .WithMany(role => role.Users)
            .UsingEntity("UserRoles");
    })
    .Entity<UserProfile>(entity => entity.ToTable("Profiles"))
    .Entity<Post>(entity => entity.ToTable("Posts"))
    .Entity<Role>(entity => entity.ToTable("Roles")));
await data.UpdateAsync();
```

`HasForeignKey<UserProfile>` указывает зависимую сторону связи 1:1.
Для 1:М FK находится в `Post.AuthorId`. В М:М дополнительный класс не требуется:
EF управляет строками таблицы `UserRoles` через навигационные коллекции.
`UsingEntity("UserRoles")` явно задаёт имя промежуточной сущности и её таблицы.
Если в этой таблице нужны собственные поля, например дата выдачи роли, можно
описать отдельную сущность и сопоставить её стандартным API EF.

## Сохранение связанных объектов

В сессии создаём две роли и двух пользователей. Один и тот же объект `reader`
передаётся обоим пользователям: в БД появится одна роль Reader и две связи с ней.

```csharp
await using (var db = data.Open())
{
    var reader = new Role { Name = "Reader" };
    var author = new Role { Name = "Author" };

    db.Add(new User
    {
        Name = "Alice",
        IsActive = true,
        Profile = new UserProfile { Bio = "Writes about .NET" },
        Posts = [new Post { Title = "First post" }, new Post { Title = "Second post" }],
        Roles = [reader, author]
    });
    db.Add(new User
    {
        Name = "Bob",
        IsActive = false,
        Profile = new UserProfile { Bio = "Reads about databases" },
        Posts = [new Post { Title = "Database notes" }],
        Roles = [reader]
    });
    await db.SaveAsync();
}
```

`Add` отмечает связанные новые объекты для вставки, а `SaveAsync()` сохраняет граф
и заполняет сгенерированные ключи. Отдельно сохранять профиль, публикации или
строки промежуточной таблицы в этом примере не нужно.

Если роль уже существует в БД, загрузи её в текущей сессии и добавь в
`user.Roles`, затем вызови `SaveAsync()`. Новый объект с ID существующей роли
не следует передавать в граф `Add` как новую роль.

## Загрузка в другой сессии

```csharp
await using var db = data.Open();
    var users = await db.Table<User>().AsNoTracking()
        .Include(user => user.Profile)
        .Include(user => user.Posts)
        .Include(user => user.Roles)
        .AsSplitQuery()
        .OrderBy(user => user.Name)
        .ToListAsync();
```

`Include` явно загружает профиль, публикации и роли. `AsSplitQuery()` разделяет
загрузку коллекций на запросы, чтобы не перемножать строки публикаций и ролей.
`AsNoTracking()` подходит для этого чтения; для редактирования загруженных
объектов убери его и вызови `SaveAsync()` после изменений.
Автоматическая lazy loading в примере не настроена.

В программе показана и обратная навигация:

```csharp
var reader = await db.Table<Role>().AsNoTracking()
    .Include(role => role.Users)
    .SingleAsync(role => role.Name == "Reader");
```

## Результат запуска

```text
Schema changed: True
User: Alice
  1:1 profile: Writes about .NET
  1:N posts: First post, Second post
  N:M roles: Author, Reader
User: Bob
  1:1 profile: Reads about databases
  1:N posts: Database notes
  N:M roles: Reader
Reader users: Alice, Bob
Active users: Alice
Repeated update changed schema: False
```

## Связи и обновление схемы

Связи создаются вместе с новыми таблицами. Это нужно отличать от изменения
ограничений в уже существующей управляемой таблице:

- Новая таблица может содержать FK на существующую таблицу.
- Новую промежуточную таблицу М:М можно создать для существующих сущностей,
  если определения их собственных таблиц и ограничений не меняются.
- Добавить или изменить FK в существующей таблице `UpdateAsync()` пока не умеет.
  Изменения PK, FK, unique/check-ограничений отклоняются.
- Смена кардинальности, например 1:М → 1:1, может требовать изменения ограничений
  и поэтому не является автоматически поддерживаемым обновлением.
- Циклические зависимости, для которых EF выдаёт отдельные `AddForeignKeyOperation`
  после создания таблиц, выходят за разрешённый набор операций обновлятора.

Подробности об истории, блокировках и разрешённых операциях:
[обновление схемы](SCHEMA.md).
