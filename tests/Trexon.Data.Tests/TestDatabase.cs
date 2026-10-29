using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;

namespace Trexon.Data.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _provider;
    private readonly string _name = "trexon_test_" + Guid.NewGuid().ToString("N");
    private string _connection = "";
    private string? _admin;
    private string? _file;

    private TestDatabase(string provider) => _provider = provider;

    public static async Task<TestDatabase> CreateAsync(string provider)
    {
        var database = new TestDatabase(provider);
        if (provider == "sqlite")
        {
            database._file = Path.Combine(Path.GetTempPath(), database._name + ".db");
            database._connection = $"Data Source={database._file};Pooling=False";
        }
        else
        {
            var admin = Environment.GetEnvironmentVariable(provider == "postgres" ? "TREXON_TEST_POSTGRES" : "TREXON_TEST_MYSQL");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(admin), $"Set TREXON_TEST_{provider.ToUpperInvariant()} to run real database tests.");
            database._admin = admin;
            if (provider == "postgres")
            {
                await using var connection = new NpgsqlConnection(admin);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {database._name};", connection);
                await command.ExecuteNonQueryAsync();
                database._connection = new NpgsqlConnectionStringBuilder(admin) { Database = database._name, Pooling = false }.ConnectionString;
            }
            else
            {
                await using var connection = new MySqlConnection(admin);
                await connection.OpenAsync();
                await using var command = new MySqlCommand($"CREATE DATABASE {database._name};", connection);
                await command.ExecuteNonQueryAsync();
                database._connection = new MySqlConnectionStringBuilder(admin) { Database = database._name, Pooling = false }.ConnectionString;
            }
        }
        return database;
    }

    public Data Open(Action<DataConfiguration> model) => Data.Create(c =>
    {
        switch (_provider)
        {
            case "postgres": c.UsePostgres(_connection); break;
            case "mysql": c.UseMySql(_connection, "8.4.0"); break;
            default: c.UseSqlite(_connection); break;
        }
        model(c);
    });

    public async ValueTask DisposeAsync()
    {
        if (_file is not null)
        {
            File.Delete(_file); File.Delete(_file + "-wal"); File.Delete(_file + "-shm");
        }
        else if (_provider == "postgres")
        {
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE {_name} WITH (FORCE);", connection);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await using var connection = new MySqlConnection(_admin);
            await connection.OpenAsync();
            await using var command = new MySqlCommand($"DROP DATABASE {_name};", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
