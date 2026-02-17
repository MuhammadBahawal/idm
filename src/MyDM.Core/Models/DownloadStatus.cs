namespace MyDM.Core.Models;

public enum DownloadStatus
{
    Queued = 0,
    Downloading = 1,
    Paused = 2,
    Complete = 3,
    Error = 4,
    Cancelled = 5,
    Merging = 6
}

public enum SegmentStatus
{
    Pending = 0,
    Active = 1,
    Done = 2,
    Error = 3
}

public enum MediaType
{
    Direct,
    Hls,
    Dash
}

public enum DownloadCategory
{
    All,
    Video,
    Music,
    Documents,
    Programs,
    Compressed,
    Others
}
