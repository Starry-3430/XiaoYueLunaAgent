using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Luna.Controls;

public static class SmoothScroll
{
    // 响应速度，越大越快，10~20 比较合适
    private const double Response = 12.0;

    // 小于这个距离就吸附到目标
    private const double SnapEpsilon = 0.1;

    private sealed class State
    {
        public double TargetV;
        public double TargetH;
        public bool VActive;
        public bool HActive;
        public bool Rendering;
        public long LastTimestamp;
        public EventHandler RenderingHandler;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    public static void Vertical(ScrollViewer sv, double delta)
    {
        if (sv.ScrollableHeight <= 0) return;

        var st = States.GetOrCreateValue(sv);

        if (!st.VActive)
        {
            st.TargetV = sv.VerticalOffset;
            st.VActive = true;
        }

        st.TargetV = Clamp(st.TargetV - delta, 0, sv.ScrollableHeight);
        EnsureRendering(sv, st);
    }

    public static void Horizontal(ScrollViewer sv, double delta)
    {
        if (sv.ScrollableWidth <= 0) return;

        var st = States.GetOrCreateValue(sv);

        if (!st.HActive)
        {
            st.TargetH = sv.HorizontalOffset;
            st.HActive = true;
        }

        st.TargetH = Clamp(st.TargetH - delta, 0, sv.ScrollableWidth);
        EnsureRendering(sv, st);
    }

    private static void EnsureRendering(ScrollViewer sv, State st)
    {
        if (st.Rendering) return;

        st.Rendering = true;
        st.LastTimestamp = Stopwatch.GetTimestamp();

        EventHandler handler = (_, _) => OnRendering(sv, st);
        st.RenderingHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private static void OnRendering(ScrollViewer sv, State st)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - st.LastTimestamp) / (double)Stopwatch.Frequency;
        st.LastTimestamp = now;

        if (dt <= 0) return;

        // 防止卡顿后 dt 太大导致跳变
        if (dt > 0.1) dt = 0.1;

        var factor = 1.0 - Math.Exp(-Response * dt);

        if (st.VActive)
        {
            st.TargetV = Clamp(st.TargetV, 0, sv.ScrollableHeight);

            var current = sv.VerticalOffset;
            var next = current + (st.TargetV - current) * factor;

            if (Math.Abs(st.TargetV - next) < SnapEpsilon)
            {
                next = st.TargetV;
                st.VActive = false;
            }

            sv.ScrollToVerticalOffset(next);
        }

        if (st.HActive)
        {
            st.TargetH = Clamp(st.TargetH, 0, sv.ScrollableWidth);

            var current = sv.HorizontalOffset;
            var next = current + (st.TargetH - current) * factor;

            if (Math.Abs(st.TargetH - next) < SnapEpsilon)
            {
                next = st.TargetH;
                st.HActive = false;
            }

            sv.ScrollToHorizontalOffset(next);
        }

        if (!st.VActive && !st.HActive)
        {
            st.Rendering = false;

            if (st.RenderingHandler != null)
            {
                CompositionTarget.Rendering -= st.RenderingHandler;
                st.RenderingHandler = null;
            }
        }
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}