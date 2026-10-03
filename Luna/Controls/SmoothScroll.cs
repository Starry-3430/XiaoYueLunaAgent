using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Luna.Controls;

/// <summary>
/// 鼠标滚轮平滑滚动：让 ScrollViewer 像浏览器一样带领画动画地滚动，
/// 而不是每个滚轮刻度硬跳固定像素。连续滚动时目标位移会累加，动画用缓出曲线收尾。
/// </summary>
public static class SmoothScroll
{
    private const double DurationMs = 220; // 单次滚动动画时长
    private const double IdleMs = 320;     // 超过该时间没有滚轮事件即视为一次新滚动（需大于动画时长）

    private sealed class State
    {
        public double TargetV;
        public double TargetH;
        public bool VActive;
        public bool HActive;
        public DispatcherTimer? Idle;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    private static readonly DependencyProperty AnimatedVProperty =
        DependencyProperty.RegisterAttached("AnimatedV", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnAnimatedVChanged));

    private static readonly DependencyProperty AnimatedHProperty =
        DependencyProperty.RegisterAttached("AnimatedH", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnAnimatedHChanged));

    private static void OnAnimatedVChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue);

    private static void OnAnimatedHChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ScrollViewer)d).ScrollToHorizontalOffset((double)e.NewValue);

    /// <summary>按滚轮增量平滑滚动垂直方向（delta 为 MouseWheelEventArgs.Delta）。</summary>
    public static void Vertical(ScrollViewer sv, double delta)
    {
        if (sv.ScrollableHeight <= 0) return;

        var st = States.GetOrCreateValue(sv);
        var baseOffset = st.VActive ? st.TargetV : sv.VerticalOffset;
        var target = Clamp(baseOffset - delta, 0, sv.ScrollableHeight);

        st.TargetV = target;
        st.VActive = true;
        ArmIdle(sv, st);
        Animate(sv, AnimatedVProperty, sv.VerticalOffset, target);
    }

    /// <summary>按滚轮增量平滑滚动水平方向（delta 为 MouseWheelEventArgs.Delta）。</summary>
    public static void Horizontal(ScrollViewer sv, double delta)
    {
        if (sv.ScrollableWidth <= 0) return;

        var st = States.GetOrCreateValue(sv);
        var baseOffset = st.HActive ? st.TargetH : sv.HorizontalOffset;
        var target = Clamp(baseOffset - delta, 0, sv.ScrollableWidth);

        st.TargetH = target;
        st.HActive = true;
        ArmIdle(sv, st);
        Animate(sv, AnimatedHProperty, sv.HorizontalOffset, target);
    }

    private static void Animate(ScrollViewer sv, DependencyProperty prop, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(DurationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        sv.BeginAnimation(prop, animation);
    }

    private static void ArmIdle(ScrollViewer sv, State st)
    {
        if (st.Idle is null)
        {
            st.Idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleMs) };
            st.Idle.Tick += (_, _) =>
            {
                st.Idle!.Stop();
                ResetAxis(sv, st, AnimatedVProperty, vertical: true);
                ResetAxis(sv, st, AnimatedHProperty, vertical: false);
            };
        }

        st.Idle.Stop();
        st.Idle.Start();
    }

    /// <summary>动画结束后把当前偏移固化为基值，避免下次从旧目标开始。</summary>
    private static void ResetAxis(ScrollViewer sv, State st, DependencyProperty prop, bool vertical)
    {
        var active = vertical ? st.VActive : st.HActive;
        if (!active) return;

        var current = vertical ? sv.VerticalOffset : sv.HorizontalOffset;
        sv.SetValue(prop, current);
        sv.BeginAnimation(prop, null);

        if (vertical)
        {
            st.VActive = false;
            st.TargetV = current;
        }
        else
        {
            st.HActive = false;
            st.TargetH = current;
        }
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
