using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MyDM.Core.Models;
using MyDM.Core.Utilities;

namespace MyDM.App.Converters;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long size && size > 0)
            return FileHelper.FormatSize(size);
        return "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double speed && speed > 0)
            return FileHelper.FormatSpeed(speed);
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class TimeLeftConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return FileHelper.FormatTimeLeft(ts);
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is DownloadStatus status ? status switch
        {
            DownloadStatus.Queued => "⏳ Queued",
            DownloadStatus.Downloading => "⬇️ Downloading",
            DownloadStatus.Paused => "⏸️ Paused",
            DownloadStatus.Complete => "✅ Complete",
            DownloadStatus.Error => "❌ Error",
            DownloadStatus.Cancelled => "🚫 Cancelled",
            DownloadStatus.Merging => "🔄 Merging",
            _ => value.ToString() ?? ""
        } : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DownloadStatus status)
        {
            return status switch
            {
                DownloadStatus.Downloading => Application.Current.FindResource("PrimaryBrush"),
                DownloadStatus.Complete => Application.Current.FindResource("AccentBrush"),
                DownloadStatus.Error => Application.Current.FindResource("DangerBrush"),
                DownloadStatus.Paused => Application.Current.FindResource("WarningBrush"),
                _ => Application.Current.FindResource("TextMutedBrush")
            };
        }
        return Application.Current.FindResource("TextMutedBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double progress && values[1] is double actualWidth)
        {
            return progress / 100.0 * actualWidth;
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}
