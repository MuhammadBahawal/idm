using System.Windows;

namespace MyDM.App.Utilities;

public static class WindowLayoutHelper
{
    public static void ApplyAdaptiveLayout(
        Window window,
        double widthRatio = 0.94,
        double heightRatio = 0.92)
    {
        window.SourceInitialized += (_, _) => Adjust(window, widthRatio, heightRatio);
    }

    private static void Adjust(Window window, double widthRatio, double heightRatio)
    {
        var workArea = SystemParameters.WorkArea;

        var maxWidth = Math.Max(window.MinWidth, workArea.Width * Math.Clamp(widthRatio, 0.4, 1.0));
        var maxHeight = Math.Max(window.MinHeight, workArea.Height * Math.Clamp(heightRatio, 0.4, 1.0));

        window.MaxWidth = workArea.Width;
        window.MaxHeight = workArea.Height;

        if (double.IsNaN(window.Width) || window.Width <= 0 || window.Width > maxWidth)
        {
            window.Width = maxWidth;
        }

        if (double.IsNaN(window.Height) || window.Height <= 0 || window.Height > maxHeight)
        {
            window.Height = maxHeight;
        }

        window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.Width) / 2);
        window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.Height) / 2);
    }
}

