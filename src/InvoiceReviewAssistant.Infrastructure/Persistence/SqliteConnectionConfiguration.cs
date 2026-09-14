using Microsoft.Data.Sqlite;

namespace InvoiceReviewAssistant.Infrastructure.Persistence;

/// <summary>Applies the database-wide invariants required by the single-process SQLite topology.</summary>
public static class SqliteConnectionConfiguration
{
    public static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
