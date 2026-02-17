using System.Windows;
using MyDM.Core.Data;
using MyDM.Core.Models;
using MyDM.Core.Utilities;

namespace MyDM.App.Views;

public partial class DownloadDetailsWindow : Window
{
    private readonly DownloadItem _item;
    private readonly DownloadRepository _repository;

    public DownloadDetailsWindow(DownloadItem item, DownloadRepository repository)
    {
        InitializeComponent();
        _item = item;
        _repository = repository;
        LoadDetails();
    }

    private void LoadDetails()
    {
        UrlText.Text = _item.Url;
        FileNameText.Text = _item.FileName;
        SavePathText.Text = _item.SavePath;
        SizeText.Text = $"Size: {FileHelper.FormatSize(_item.TotalSize)}";
        StatusText.Text = $"Status: {_item.Status}";
        CategoryText.Text = $"Category: {_item.Category}";
        ConnectionsText.Text = $"Connections: {_item.Connections}";
        RetriesText.Text = $"Retries: {_item.RetryCount}";
        CreatedText.Text = $"Created: {_item.CreatedAt.ToLocalTime():g}";
        ErrorText.Text = _item.ErrorMessage != null ? $"Error: {_item.ErrorMessage}" : "";

        // Load segments
        var segments = _repository.GetSegments(_item.Id);
        SegmentsList.ItemsSource = segments.Select(s => new
        {
            s.Index,
            RangeText = $"{FileHelper.FormatSize(s.StartByte)} - {FileHelper.FormatSize(s.EndByte)}",
            DownloadedText = FileHelper.FormatSize(s.DownloadedBytes),
            StatusText = s.Status.ToString(),
            ProgressText = $"{s.ProgressPercent:F1}%"
        }).ToList();

        // Load logs
        var logs = _repository.GetLogs(_item.Id);
        LogsList.ItemsSource = logs.Select(l =>
            $"[{l.Timestamp.ToLocalTime():HH:mm:ss}] [{l.Level}] {l.Message}").ToList();
    }
}
