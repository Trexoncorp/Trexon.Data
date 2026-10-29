# Trexon.Data

[English](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.md) · [Русский](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.ru.md) · [Deutsch](https://github.com/Trexoncorp/Trexon.Data/blob/main/README.de.md)

Простая надстройка над EF Core для C# / .NET 8+: LINQ, PostgreSQL, MySQL и SQLite,
короткие сессии и обновление структуры БД из кода приложения. Собственный
`DbContext`, свойства `DbSet` и команды `dotnet ef` не нужны.

**Статус:** `1.0.0-dev`. Поддерживается ограниченный набор изменений схемы.

## Начало работы

```csharp
using Microsoft.EntityFrameworkCore;
using Trexon.Data;

await using var data = Data.Create(config => config
    .UseSqlite("Data Source=app.db")
    .Entity<User>());

await data.UpdateAsync(); // До обработки запросов и запуска фоновых задач.

await using var db = data.Open();
db.Add(new User { Name = "Антон" });
await db.SaveAsync();

var names = await db.Table<User>().AsNoTracking()
    .Where(user => user.Name.StartsWith("А"))
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

`Table<T>()` возвращает настоящий `DbSet<T>`: доступны стандартные LINQ, `Include`,
проекции, tracking, транзакции и массовые операции EF.
`Add`, `Remove` и изменения загруженных объектов сохраняются через `SaveAsync()`.
Освобождение сессии не сохраняет данные. `ExecuteUpdateAsync` и
`ExecuteDeleteAsync` выполняются сразу, как в обычном EF.

## Подключение

Выбери один провайдер:

```csharp
config.UsePostgres(connectionString);
config.UseMySql(connectionString, serverVersion: "8.4.0");
config.UseSqlite("Data Source=app.db");
```

Базы PostgreSQL/MySQL должны уже существовать; библиотека создаёт таблицы.
Файл SQLite создаётся при открытии. `Data Source=:memory:` сохраняется в течение
жизни `Data` и доступен всем его сессиям. Освобождай `Data` после сессий.

Все три провайдера входят в один пакет: Npgsql, Pomelo, Microsoft SQLite.
Общая версия EF Core — 9, целевая платформа библиотеки — .NET 8.
Для MySQL по умолчанию используется версия сервера `8.0.0`; лучше указывать
реальную версию. Автоопределение по сети не выполняется. MariaDB не проверялась.

## Обновление при старте

`await data.UpdateAsync()` сравнивает модель с историей в БД и проверяет физическую
структуру управляемых таблиц. Повторный запуск без изменений возвращает
`Changed == false`.

| Изменение | Поведение |
| --- | --- |
| Новая таблица с ключами, связями и индексами | Создаётся автоматически |
| Новое nullable-поле | Добавляется автоматически |
| Новое обязательное поле | Нужен default в модели или явное значение заполнения |
| Новый индекс | Создаётся; существующие строки должны удовлетворять ограничениям |
| Переименование поля | Явный `RenameColumn` |
| Удаление поля | Явный `DropColumn`; поля с индексами/ограничениями отклоняются |
| Тип, default или другое сопоставление поля | Явный `AlterColumn`; ключи, FK и nullable → required отклоняются |
| Переименование/удаление таблицы, замена ограничений или индексов | Отклоняются |

Именованные изменения используют **имена таблиц и колонок в БД**:

```csharp
config.Entity<User>(entity =>
{
    entity.ToTable("Users");
    entity.Property(user => user.Name).HasColumnName("FullName");
});
config.Change("001-rename-name", change => change
    .RenameColumn("Users", "Name", "FullName"));
```

В старой БД должна существовать колонка `Users.Name`. Новая БД сразу создаётся по
актуальной модели; старые изменения записываются в историю без выполнения.
Применённые определения `Change` нужно сохранять без изменения ID, порядка и тела.

Добавление нового обязательного свойства `Score` с заполнением существующих строк:

```csharp
config.Change("002-add-score", change => change
    .AddRequiredColumn("Users", "Score", 0));
```

Временный default после заполнения удаляется, если он не задан в самой EF-модели.
Инициализатор C#-свойства не является default в БД.
Для индексов, связей, длины строк и остальных настроек используется обычный
`EntityTypeBuilder<T>` в `Entity<T>()`.

## ASP.NET Core

```csharp
builder.Services.AddTrexonData(config => config
    .UsePostgres(builder.Configuration.GetConnectionString("Database")!)
    .Entity<User>());

var app = builder.Build();
await app.Services.GetRequiredService<Data>().UpdateAsync();
```

`Data` регистрируется как singleton, `DataSession` — как scoped.
Интеграцию регистрируют один раз. Сессию можно получать через DI в сервисах.
Один контекст нельзя использовать параллельно: для параллельных задач нужны
разные сессии.

## Границы первой версии

Управляемые таблицы должны создаваться библиотекой. Готовая таблица без истории
`__TrexonDataHistory` не подхватывается автоматически. Не смешивай обновлятор с
`EnsureCreated`, EF migrations и ручным DDL на этих таблицах.
Неуправляемые таблицы остаются без изменений.

PostgreSQL/SQLite обновляются в транзакции. В MySQL DDL не откатывается целиком:
при сбое сохраняется признак незавершённого обновления, и последующие обновления
блокируются. Для восстановления нужна известная исправная копия БД вместе с её
историей. Простой сброс признака не исправляет частично выполненный DDL.

Блокировка сериализует вызовы обновлятора, но не обычные запросы приложения.
Обновление нужно закончить до приёма трафика; работающие версии приложения нужно
координировать отдельно. Приложению требуются права на изменение схемы.

Последовательности, `HasData`, произвольные SQL-миграции данных, замена существующих
ограничений и индексов пока не поддерживаются. Изменения сопоставляются с конечной
моделью; операции, требующие промежуточной модели, отклоняются. Новая таблица может
ссылаться на существующую; добавление связи в старую таблицу считается изменением
ограничений и отклоняется.

Подробности: [схема](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/SCHEMA.md), [разработка и проверки](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/DEVELOPMENT.md).
Рабочий пример: `examples/Trexon.Data.Sample`.

Лицензия [MIT](https://github.com/Trexoncorp/Trexon.Data/blob/main/LICENSE).
