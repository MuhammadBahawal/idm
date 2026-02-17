using Microsoft.Data.Sqlite;
using MyDM.Core.Models;

namespace MyDM.Core.Data;

public class DownloadRepository
{
    private readonly MyDMDatabase _db;

    public DownloadRepository(MyDMDatabase db)
    {
        _db = db;
    }

    // ──── Downloads ────

    public void Insert(DownloadItem item)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO Downloads 
            (Id,Url,FileName,SavePath,Category,Status,TotalSize,DownloadedSize,Connections,SpeedLimit,
             Checksum,ChecksumVerified,Description,MediaType,ManifestUrl,SelectedQuality,ErrorMessage,
             RetryCount,SupportsRange,CreatedAt,CompletedAt,LastAttemptAt)
            VALUES (@id,@url,@fn,@sp,@cat,@st,@ts,@ds,@conn,@sl,@cs,@csv,@desc,@mt,@mu,@sq,@em,@rc,@sr,@ca,@coa,@la)";
        BindDownloadParams(cmd, item);
        cmd.ExecuteNonQuery();
    }

    public void Update(DownloadItem item)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE Downloads SET 
            Url=@url,FileName=@fn,SavePath=@sp,Category=@cat,Status=@st,TotalSize=@ts,
            DownloadedSize=@ds,Connections=@conn,SpeedLimit=@sl,Checksum=@cs,ChecksumVerified=@csv,
            Description=@desc,MediaType=@mt,ManifestUrl=@mu,SelectedQuality=@sq,ErrorMessage=@em,
            RetryCount=@rc,SupportsRange=@sr,CreatedAt=@ca,CompletedAt=@coa,LastAttemptAt=@la
            WHERE Id=@id";
        BindDownloadParams(cmd, item);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Downloads WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public DownloadItem? GetById(string id)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Downloads WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDownload(reader) : null;
    }

    public List<DownloadItem> GetAll()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Downloads ORDER BY CreatedAt DESC";
        using var reader = cmd.ExecuteReader();
        var items = new List<DownloadItem>();
        while (reader.Read())
            items.Add(ReadDownload(reader));
        return items;
    }

    public List<DownloadItem> GetByStatus(DownloadStatus status)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Downloads WHERE Status=@st ORDER BY CreatedAt DESC";
        cmd.Parameters.AddWithValue("@st", (int)status);
        using var reader = cmd.ExecuteReader();
        var items = new List<DownloadItem>();
        while (reader.Read())
            items.Add(ReadDownload(reader));
        return items;
    }

    public List<DownloadItem> GetByCategory(string category)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Downloads WHERE Category=@cat ORDER BY CreatedAt DESC";
        cmd.Parameters.AddWithValue("@cat", category);
        using var reader = cmd.ExecuteReader();
        var items = new List<DownloadItem>();
        while (reader.Read())
            items.Add(ReadDownload(reader));
        return items;
    }

    public void UpdateProgress(string id, long downloadedSize, DownloadStatus status)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Downloads SET DownloadedSize=@ds, Status=@st, LastAttemptAt=@la WHERE Id=@id";
        cmd.Parameters.AddWithValue("@ds", downloadedSize);
        cmd.Parameters.AddWithValue("@st", (int)status);
        cmd.Parameters.AddWithValue("@la", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // ──── Segments ────

    public void InsertSegments(List<DownloadSegment> segments)
    {
        var conn = _db.GetConnection();
        using var txn = conn.BeginTransaction();
        foreach (var seg in segments)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = @"INSERT INTO Segments (Id,DownloadId,Idx,StartByte,EndByte,DownloadedBytes,Status,TempFile)
                VALUES (@id,@did,@idx,@sb,@eb,@db,@st,@tf)";
            cmd.Parameters.AddWithValue("@id", seg.Id);
            cmd.Parameters.AddWithValue("@did", seg.DownloadId);
            cmd.Parameters.AddWithValue("@idx", seg.Index);
            cmd.Parameters.AddWithValue("@sb", seg.StartByte);
            cmd.Parameters.AddWithValue("@eb", seg.EndByte);
            cmd.Parameters.AddWithValue("@db", seg.DownloadedBytes);
            cmd.Parameters.AddWithValue("@st", (int)seg.Status);
            cmd.Parameters.AddWithValue("@tf", (object?)seg.TempFile ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    public void UpdateSegment(DownloadSegment seg)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Segments SET DownloadedBytes=@db, Status=@st WHERE Id=@id";
        cmd.Parameters.AddWithValue("@db", seg.DownloadedBytes);
        cmd.Parameters.AddWithValue("@st", (int)seg.Status);
        cmd.Parameters.AddWithValue("@id", seg.Id);
        cmd.ExecuteNonQuery();
    }

    public List<DownloadSegment> GetSegments(string downloadId)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Segments WHERE DownloadId=@did ORDER BY Idx";
        cmd.Parameters.AddWithValue("@did", downloadId);
        using var reader = cmd.ExecuteReader();
        var segs = new List<DownloadSegment>();
        while (reader.Read())
        {
            segs.Add(new DownloadSegment
            {
                Id = reader.GetString(reader.GetOrdinal("Id")),
                DownloadId = reader.GetString(reader.GetOrdinal("DownloadId")),
                Index = reader.GetInt32(reader.GetOrdinal("Idx")),
                StartByte = reader.GetInt64(reader.GetOrdinal("StartByte")),
                EndByte = reader.GetInt64(reader.GetOrdinal("EndByte")),
                DownloadedBytes = reader.GetInt64(reader.GetOrdinal("DownloadedBytes")),
                Status = (SegmentStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                TempFile = reader.IsDBNull(reader.GetOrdinal("TempFile")) ? null : reader.GetString(reader.GetOrdinal("TempFile"))
            });
        }
        return segs;
    }

    public void DeleteSegments(string downloadId)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Segments WHERE DownloadId=@did";
        cmd.Parameters.AddWithValue("@did", downloadId);
        cmd.ExecuteNonQuery();
    }

    // ──── Logs ────

    public void AddLog(string downloadId, string level, string message)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO DownloadLogs (DownloadId, Timestamp, Level, Message) VALUES (@did, @ts, @lv, @msg)";
        cmd.Parameters.AddWithValue("@did", downloadId);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@lv", level);
        cmd.Parameters.AddWithValue("@msg", message);
        cmd.ExecuteNonQuery();
    }

    public List<DownloadLog> GetLogs(string downloadId, int limit = 100)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM DownloadLogs WHERE DownloadId=@did ORDER BY Id DESC LIMIT @lim";
        cmd.Parameters.AddWithValue("@did", downloadId);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = cmd.ExecuteReader();
        var logs = new List<DownloadLog>();
        while (reader.Read())
        {
            logs.Add(new DownloadLog
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                DownloadId = reader.GetString(reader.GetOrdinal("DownloadId")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp"))),
                Level = reader.GetString(reader.GetOrdinal("Level")),
                Message = reader.GetString(reader.GetOrdinal("Message"))
            });
        }
        return logs;
    }

    // ──── Settings ────

    public string? GetSetting(string key)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key=@key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@key, @val)";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@val", value);
        cmd.ExecuteNonQuery();
    }

    // ──── Categories ────

    public List<CategoryRule> GetCategories()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Categories";
        using var reader = cmd.ExecuteReader();
        var cats = new List<CategoryRule>();
        while (reader.Read())
        {
            var extsStr = reader.IsDBNull(reader.GetOrdinal("Extensions")) ? "" : reader.GetString(reader.GetOrdinal("Extensions"));
            var mimesStr = reader.IsDBNull(reader.GetOrdinal("MimeTypes")) ? "" : reader.GetString(reader.GetOrdinal("MimeTypes"));
            cats.Add(new CategoryRule
            {
                Id = reader.GetString(reader.GetOrdinal("Id")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Extensions = string.IsNullOrEmpty(extsStr) ? Array.Empty<string>() : extsStr.Split(','),
                MimeTypes = string.IsNullOrEmpty(mimesStr) ? Array.Empty<string>() : mimesStr.Split(','),
                SaveFolder = reader.IsDBNull(reader.GetOrdinal("SaveFolder")) ? "" : reader.GetString(reader.GetOrdinal("SaveFolder"))
            });
        }
        return cats;
    }

    // ──── Helpers ────

    private static void BindDownloadParams(SqliteCommand cmd, DownloadItem item)
    {
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.Parameters.AddWithValue("@url", item.Url);
        cmd.Parameters.AddWithValue("@fn", item.FileName);
        cmd.Parameters.AddWithValue("@sp", item.SavePath);
        cmd.Parameters.AddWithValue("@cat", item.Category);
        cmd.Parameters.AddWithValue("@st", (int)item.Status);
        cmd.Parameters.AddWithValue("@ts", item.TotalSize);
        cmd.Parameters.AddWithValue("@ds", item.DownloadedSize);
        cmd.Parameters.AddWithValue("@conn", item.Connections);
        cmd.Parameters.AddWithValue("@sl", item.SpeedLimit);
        cmd.Parameters.AddWithValue("@cs", (object?)item.Checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@csv", item.ChecksumVerified ? 1 : 0);
        cmd.Parameters.AddWithValue("@desc", (object?)item.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mt", item.MediaType.ToString());
        cmd.Parameters.AddWithValue("@mu", (object?)item.ManifestUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sq", (object?)item.SelectedQuality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@em", (object?)item.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rc", item.RetryCount);
        cmd.Parameters.AddWithValue("@sr", item.SupportsRange ? 1 : 0);
        cmd.Parameters.AddWithValue("@ca", item.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@coa", (object?)item.CompletedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@la", (object?)item.LastAttemptAt?.ToString("o") ?? DBNull.Value);
    }

    private static DownloadItem ReadDownload(SqliteDataReader reader)
    {
        return new DownloadItem
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Url = reader.GetString(reader.GetOrdinal("Url")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            SavePath = reader.GetString(reader.GetOrdinal("SavePath")),
            Category = reader.GetString(reader.GetOrdinal("Category")),
            Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            TotalSize = reader.GetInt64(reader.GetOrdinal("TotalSize")),
            DownloadedSize = reader.GetInt64(reader.GetOrdinal("DownloadedSize")),
            Connections = reader.GetInt32(reader.GetOrdinal("Connections")),
            SpeedLimit = reader.GetInt64(reader.GetOrdinal("SpeedLimit")),
            Checksum = reader.IsDBNull(reader.GetOrdinal("Checksum")) ? null : reader.GetString(reader.GetOrdinal("Checksum")),
            ChecksumVerified = reader.GetInt32(reader.GetOrdinal("ChecksumVerified")) == 1,
            Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            MediaType = Enum.TryParse<MediaType>(reader.IsDBNull(reader.GetOrdinal("MediaType")) ? "Direct" : reader.GetString(reader.GetOrdinal("MediaType")), out var mt) ? mt : MediaType.Direct,
            ManifestUrl = reader.IsDBNull(reader.GetOrdinal("ManifestUrl")) ? null : reader.GetString(reader.GetOrdinal("ManifestUrl")),
            SelectedQuality = reader.IsDBNull(reader.GetOrdinal("SelectedQuality")) ? null : reader.GetString(reader.GetOrdinal("SelectedQuality")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
            SupportsRange = reader.GetInt32(reader.GetOrdinal("SupportsRange")) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            CompletedAt = reader.IsDBNull(reader.GetOrdinal("CompletedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("CompletedAt"))),
            LastAttemptAt = reader.IsDBNull(reader.GetOrdinal("LastAttemptAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastAttemptAt")))
        };
    }
}
