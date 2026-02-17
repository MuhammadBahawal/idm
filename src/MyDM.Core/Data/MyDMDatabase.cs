using Microsoft.Data.Sqlite;
using MyDM.Core.Models;

namespace MyDM.Core.Data;

public class MyDMDatabase : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public MyDMDatabase(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    public SqliteConnection GetConnection()
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            // Enable WAL mode for better concurrent access
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        return _connection;
    }

    public void Initialize()
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        SeedCategories(conn);
    }

    private void SeedCategories(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM Categories";
        var count = (long)check.ExecuteScalar()!;
        if (count > 0) return;

        var categories = new[]
        {
            ("Video", ".mp4,.mkv,.avi,.mov,.wmv,.flv,.webm,.m4v,.3gp,.ts", "video/", "Videos"),
            ("Music", ".mp3,.flac,.wav,.aac,.ogg,.wma,.m4a,.opus", "audio/", "Music"),
            ("Documents", ".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.rtf,.odt,.csv", "application/pdf,application/msword,text/", "Documents"),
            ("Programs", ".exe,.msi,.dmg,.deb,.rpm,.apk,.appx,.bat,.sh", "application/x-msdownload,application/x-executable", "Programs"),
            ("Compressed", ".zip,.rar,.7z,.tar,.gz,.bz2,.xz,.iso,.cab", "application/zip,application/x-rar,application/x-7z", "Compressed"),
            ("Others", "", "", "Others")
        };

        foreach (var (name, exts, mimes, folder) in categories)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO Categories (Id, Name, Extensions, MimeTypes, SaveFolder) VALUES (@id, @name, @exts, @mimes, @folder)";
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@exts", exts);
            insert.Parameters.AddWithValue("@mimes", mimes);
            insert.Parameters.AddWithValue("@folder", folder);
            insert.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    private const string Schema = @"
CREATE TABLE IF NOT EXISTS Downloads (
    Id              TEXT PRIMARY KEY,
    Url             TEXT NOT NULL,
    FileName        TEXT NOT NULL,
    SavePath        TEXT NOT NULL,
    Category        TEXT NOT NULL DEFAULT 'Others',
    Status          INTEGER NOT NULL DEFAULT 0,
    TotalSize       INTEGER DEFAULT 0,
    DownloadedSize  INTEGER DEFAULT 0,
    Connections     INTEGER DEFAULT 8,
    SpeedLimit      INTEGER DEFAULT 0,
    Checksum        TEXT,
    ChecksumVerified INTEGER DEFAULT 0,
    Description     TEXT,
    MediaType       TEXT DEFAULT 'Direct',
    ManifestUrl     TEXT,
    SelectedQuality TEXT,
    ErrorMessage    TEXT,
    RetryCount      INTEGER DEFAULT 0,
    SupportsRange   INTEGER DEFAULT 1,
    CreatedAt       TEXT NOT NULL,
    CompletedAt     TEXT,
    LastAttemptAt   TEXT
);

CREATE TABLE IF NOT EXISTS Segments (
    Id              TEXT PRIMARY KEY,
    DownloadId      TEXT NOT NULL REFERENCES Downloads(Id) ON DELETE CASCADE,
    Idx             INTEGER NOT NULL,
    StartByte       INTEGER NOT NULL,
    EndByte         INTEGER NOT NULL,
    DownloadedBytes INTEGER DEFAULT 0,
    Status          INTEGER DEFAULT 0,
    TempFile        TEXT
);

CREATE TABLE IF NOT EXISTS Queues (
    Id              TEXT PRIMARY KEY,
    Name            TEXT NOT NULL,
    MaxConcurrent   INTEGER DEFAULT 3,
    SpeedLimit      INTEGER DEFAULT 0,
    ScheduleStart   TEXT,
    ScheduleStop    TEXT,
    DaysOfWeek      TEXT,
    IsActive        INTEGER DEFAULT 1
);

CREATE TABLE IF NOT EXISTS QueueDownloads (
    QueueId     TEXT REFERENCES Queues(Id),
    DownloadId  TEXT REFERENCES Downloads(Id),
    Position    INTEGER,
    PRIMARY KEY (QueueId, DownloadId)
);

CREATE TABLE IF NOT EXISTS Categories (
    Id          TEXT PRIMARY KEY,
    Name        TEXT NOT NULL,
    Extensions  TEXT,
    MimeTypes   TEXT,
    SaveFolder  TEXT
);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

CREATE TABLE IF NOT EXISTS DownloadLogs (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    DownloadId  TEXT REFERENCES Downloads(Id) ON DELETE CASCADE,
    Timestamp   TEXT NOT NULL,
    Level       TEXT NOT NULL,
    Message     TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_segments_download ON Segments(DownloadId);
CREATE INDEX IF NOT EXISTS idx_logs_download ON DownloadLogs(DownloadId);
CREATE INDEX IF NOT EXISTS idx_queue_downloads ON QueueDownloads(QueueId);
";
}
