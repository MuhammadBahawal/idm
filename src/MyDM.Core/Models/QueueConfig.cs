namespace MyDM.Core.Models;

public class QueueConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Default";
    public int MaxConcurrent { get; set; } = 3;
    public long SpeedLimit { get; set; }
    public TimeOnly? ScheduleStart { get; set; }
    public TimeOnly? ScheduleStop { get; set; }
    public DayOfWeek[]? DaysOfWeek { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> DownloadIds { get; set; } = new();
}
