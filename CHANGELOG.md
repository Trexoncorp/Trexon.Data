# Changelog

## 1.0.0 — 2026-10-29

First stable release of Trexon.Data for C# / .NET 8+.

- PostgreSQL (Npgsql), MySQL (Pomelo) and SQLite providers in one package.
- `Data.Create`, independent `Open` sessions, standard EF LINQ through `Table<T>` and explicit `SaveAsync`.
- One-to-one, one-to-many and many-to-many relationships using the standard EF mapping API.
- Startup `UpdateAsync` with automatic new tables, nullable columns and new indexes.
- Named `RenameColumn`, `DropColumn`, `AlterColumn` and required-column backfill operations.
- Persistent schema history, repeatable startup checks, physical drift detection and updater locks.
- Transactional PostgreSQL/SQLite updates and a durable incomplete-update guard for MySQL DDL.
- ASP.NET Core dependency injection, a runnable relationship sample and English/Russian/German README files.

The updater intentionally rejects unsupported changes: existing table/constraint/index
replacement, key/FK type changes, nullable-to-required conversions and arbitrary SQL
data migrations. PostgreSQL/MySQL databases must be provisioned before startup.
See `docs/SCHEMA.md` and `docs/RELATIONSHIPS.md` for the complete boundaries.
