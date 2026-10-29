# Разработка и проверки

Структура повторяет Trexon.Logging и Trexon.Configuration: библиотека в одноимённой
папке, `examples/`, `tests/`, `.slnx`, SDK 10 и целевой .NET 8. Тесты запускаются на
.NET 8 и .NET 10 через xUnit v3 / Microsoft.Testing.Platform.

## Локальная проверка

```sh
dotnet restore Trexon.Data.slnx
dotnet build Trexon.Data.slnx --configuration Release --no-restore -warnaserror
dotnet test --solution Trexon.Data.slnx --configuration Release --no-build --no-restore
dotnet run --project examples/Trexon.Data.Sample --configuration Release --no-build
dotnet pack Trexon.Data/Trexon.Data.csproj --configuration Release --no-build --output artifacts -warnaserror
```

Без переменных подключения запускаются SQLite-проверки; тесты PostgreSQL/MySQL
отмечаются пропущенными, а не успешно выполненными. Для всех трёх нужны отдельные
тестовые серверы и переменные:

```sh
export TREXON_TEST_POSTGRES='Host=localhost;Port=5432;Username=postgres;Password=local-test-only;Database=postgres'
export TREXON_TEST_MYSQL='Server=localhost;Port=3306;User ID=root;Password=local-test-only'
dotnet test --solution Trexon.Data.slnx --configuration Release --no-build --no-restore
```

Используйте только изолированные тестовые серверы. Учётной записи нужны права на
создание/удаление БД. Каждый тест создаёт собственную БД с префиксом
`trexon_test_<guid>` и удаляет именно её после завершения. SQLite использует
временные файлы. Тестовые пароли в примерах не являются рабочими секретами.

CI поднимает PostgreSQL 17 и MySQL 8.4, запускает оба набора runtime-тестов и
упаковывает NuGet. Кроссплатформенная сборка, SQLite и пример дополнительно
проверяются на Linux, Windows и macOS. Публикация NuGet не настроена: локальная
версия пакета — `1.0.0-dev`.

## Рабочий пример связей

`examples/Trexon.Data.Sample` показывает 1:1 (профиль), 1:М (публикации),
М:М (роли), сохранение графа одним `SaveAsync`, загрузку через `Include` в новой
сессии и обратную навигацию `Role.Users`. Он использует SQLite в памяти.
Полные модели и ожидаемый вывод приведены в [руководстве по связям](RELATIONSHIPS.md).

## Устройство

- `DataConfiguration`: провайдер, регистрации моделей, именованные изменения.
- `Data`: снимок конфигурации, options EF, открытие независимых сессий; владеет
  SQLite anchor для in-memory БД.
- `DataSession`: обычный DbContext с короткими `Table<T>` / `SaveAsync`.
- `SchemaState`: сериализуемое описание управляемой модели.
- `SchemaPlanner`: проверка изменений и построение разрешённых EF-операций.
- `SchemaUpdater`: блокировка, история, физическая проверка и выполнение SQL.

Модель EF кешируется отдельно для каждого Data и режима designTime. Две независимые
конфигурации не должны получать чужое сопоставление модели. Callbacks модели должны
быть детерминированными: не меняйте захваченные переменные после `Data.Create`.

SQL схемы генерирует соответствующий EF-провайдер. Значения истории и параметры
каталогов передаются через DbParameter; идентификаторы экранируются провайдером.
Соединения с серверами открываются только при операции, а не для определения
версии MySQL. Новые возможности схемы сопровождайте тестами сохранности строк,
повторов, отката и блокировок на каждом поддерживаемом движке.
