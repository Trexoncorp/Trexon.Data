# Публикация GitHub и NuGet

Первый стабильный релиз — `1.0.0`. Версия NuGet-пакета берётся из тега
`vMAJOR.MINOR.PATCH[-PRERELEASE]`. Исходная версия в csproj на выпуске 1.0.0
также равна `1.0.0`. Опубликованные версии и теги не перемещаются.

## Настройка один раз

- GitHub repository: `Trexoncorp/Trexon.Data`.
- GitHub environment: `nuget`.
- Необязательная repository variable `NUGET_USER`: имя профиля nuget.org; по умолчанию `anton_aleynichenko`.
- NuGet Trusted Publishing: GitHub owner `Trexoncorp`, repository `Trexon.Data`,
  workflow `publish.yml`, environment `nuget`, область пакета строго `Trexon.Data`.
- Для первого выпуска policy должна разрешать новые пакеты и новые версии.

Публикация использует OIDC и краткоживущий API key из `NuGet/login`; постоянный
NuGet API key в GitHub secrets не требуется. Настройки соседних репозиториев
не распространяются на новый репозиторий автоматически.
[Официальная инструкция NuGet](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Проверка и выпуск

До создания тега CI актуального `main` должен пройти на Linux, Windows и macOS,
включая отдельный job с реальными PostgreSQL, MySQL и SQLite. Пример связей и
ожидаемый вывод проверяет `scripts/check-docs.py` после Release-сборки.

```sh
bash scripts/release-version.sh v1.0.0
git tag -a v1.0.0 -m "Trexon.Data 1.0.0"
git push origin v1.0.0
```

`publish.yml` проверяет тег и его принадлежность `origin/main`, повторно собирает
проект, запускает документацию и все тесты на .NET 8 / .NET 10 с тремя БД.
После этого он собирает nupkg/snupkg и сохраняет артефакт `nuget-release`.
Отдельный job в environment `nuget` обменивает GitHub OIDC token на временный
NuGet key и публикует пакет и символы.

Публикация тега сама по себе не подтверждает успех. После выполнения workflow
нужно проверить оба jobs, доступность нужной версии на nuget.org и установку
в чистый проект из публичного NuGet feed. GitHub Release с описанием и пакетами
создаётся отдельно по тому же тегу.

## Ограничения 1.0.0

Контракт включает LINQ/EF-сессии, явное сохранение, три провайдера и ограниченное
обновление схемы. Поддержка связей и границы обновления описаны в
[связях](RELATIONSHIPS.md) и [схеме](SCHEMA.md). Это часть описания релиза;
стабильная версия не означает поддержку произвольных EF migrations.

## Автоматическая публикация через Trusted Publishing

Релизный workflow публикует проверенный nupkg и символы в NuGet через OIDC.
Постоянный API key не нужен. Логин — repository variable `NUGET_USER` или
`anton_aleynichenko` по умолчанию. Публикация использует environment `nuget`.

В NuGet policy укажите owner репозитория `Trexoncorp`, имя этого репозитория,
точное имя workflow и environment `nuget`; scope ограничьте соответствующим
Package ID и публикацией новых версий существующего пакета.

Для проверки настройки без загрузки пакета запустите workflow вручную на `main`:
выполнится только обмен OIDC-токена. Публикация выполняется исключительно по тегу.
Опубликованные теги не перемещайте; перед новым тегом обновите версию и документацию.

Workflow: `publish.yml`. Тег: `vVERSION`; версия пакета берётся из тега.
