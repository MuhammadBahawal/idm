using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace MyDM.App.Utilities;

public static class ProgressBarAnimationHelper
{
    public static readonly DependencyProperty TargetValueProperty =
        DependencyProperty.RegisterAttached(
            "TargetValue",
            typeof(double),
            typeof(ProgressBarAnimationHelper),
            new PropertyMetadata(0d, OnTargetValueChanged));

    public static void SetTargetValue(DependencyObject element, double value)
    {
        element.SetValue(TargetValueProperty, value);
    }

    public static double GetTargetValue(DependencyObject element)
    {
        return (double)element.GetValue(TargetValueProperty);
    }

    private static void OnTargetValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not WpfProgressBar progressBar)
        {
            return;
        }

        var maximum = progressBar.Maximum <= 0 ? 100d : progressBar.Maximum;
        var nextValue = Math.Clamp((double)eventArgs.NewValue, 0d, maximum);

        if (!progressBar.IsLoaded)
        {
            progressBar.Value = nextValue;
            return;
        }

        var currentValue = progressBar.Value;
        var delta = Math.Abs(nextValue - currentValue);
        if (delta < 0.1d)
        {
            progressBar.Value = nextValue;
            return;
        }

        // Keep motion smooth while still staying responsive to frequent updates.
        var durationMs = Math.Clamp(delta * 7d, 90d, 300d);
        var animation = new DoubleAnimation
        {
            To = nextValue,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        progressBar.BeginAnimation(RangeBase.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
