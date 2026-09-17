using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SiriBlobDemo
{
    public partial class MainWindow : Window
    {
        // =========================
        // Global HotKey
        // =========================

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1001;

        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_SPACE = 0x20;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(
            IntPtr hWnd,
            int id);


        // =========================
        // Animation
        // =========================

        private readonly Stopwatch stopwatch = new();

        private bool animating;

        private const double AnimationDuration = 720.0;

        private double screenWidth;
        private double screenHeight;

        private HwndSource? hwndSource;


        public MainWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Closed += OnClosed;
        }


        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Demo: 覆盖整个主屏幕
            screenWidth = SystemParameters.PrimaryScreenWidth;
            screenHeight = SystemParameters.PrimaryScreenHeight;

            Width = screenWidth;
            Height = screenHeight;

            Left = 0;
            Top = 0;

            var handle = new WindowInteropHelper(this).Handle;

            hwndSource = HwndSource.FromHwnd(handle);

            hwndSource.AddHook(WndProc);

            RegisterHotKey(
                handle,
                HOTKEY_ID,
                MOD_CONTROL,
                VK_SPACE);

            // 默认隐藏
            Blob.Visibility = Visibility.Collapsed;
        }


        private void OnClosed(object? sender, EventArgs e)
        {
            if (hwndSource != null)
            {
                hwndSource.RemoveHook(WndProc);
            }

            var handle = new WindowInteropHelper(this).Handle;

            UnregisterHotKey(handle, HOTKEY_ID);
        }


        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == WM_HOTKEY &&
                wParam.ToInt32() == HOTKEY_ID)
            {
                StartAnimation();
                handled = true;
            }

            return IntPtr.Zero;
        }


        // =========================
        // Start
        // =========================

        private void StartAnimation()
        {
            if (animating)
                return;

            animating = true;

            Blob.Visibility = Visibility.Visible;

            stopwatch.Restart();

            CompositionTarget.Rendering += OnRendering;
        }


        // =========================
        // Main Animation Loop
        // =========================

        private void OnRendering(object? sender, EventArgs e)
        {
            double t = stopwatch.Elapsed.TotalMilliseconds;

            double progress = t / AnimationDuration;

            if (progress >= 1.0)
            {
                progress = 1.0;

                UpdateBlob(progress);

                CompositionTarget.Rendering -= OnRendering;

                animating = false;

                return;
            }

            UpdateBlob(progress);
        }


        // =========================
        // Animation State
        // =========================

        private void UpdateBlob(double t)
        {
            /*
             * 整个动画：
             *
             * 0.00 - 0.28
             * 顶部粘附 + 拉伸
             *
             * 0.28 - 0.38
             * Detach
             *
             * 0.38 - 0.70
             * 高速下落 + 横向展开
             *
             * 0.70 - 1.00
             * 强力减速 + 胶囊稳定
             */

            double centerX = screenWidth / 2.0;

            double y;
            double width;
            double height;

            // =====================================
            // Phase 1
            // 顶部粘附
            // =====================================

            if (t < 0.28)
            {
                double p = t / 0.28;

                // 拉伸速度
                double stretch = EaseOutCubic(p);

                width = Lerp(70, 88, stretch);

                height = Lerp(30, 155, stretch);

                // 顶部固定
                y = height / 2.0;

                RenderAttachedBlob(
                    centerX,
                    y,
                    width,
                    height,
                    p);

                return;
            }


            // =====================================
            // Phase 2
            // Detach
            // =====================================

            if (t < 0.38)
            {
                double p = (t - 0.28) / 0.10;

                double ease = EaseInOutCubic(p);

                width = Lerp(88, 105, ease);

                height = Lerp(155, 170, ease);

                // 从顶部脱离
                y = Lerp(
                    height / 2.0,
                    230,
                    ease);

                RenderFreeBlob(
                    centerX,
                    y,
                    width,
                    height,
                    p);

                return;
            }


            // =====================================
            // Phase 3
            // 高速下落 + 横向扩张
            // =====================================

            if (t < 0.70)
            {
                double p = (t - 0.38) / 0.32;

                /*
                 * 这里故意不是普通 EaseOut。
                 *
                 * 前半段：
                 *     已经有很大的初速
                 *
                 * 中间：
                 *     小幅加速
                 *
                 * 后面：
                 *     开始明显减速
                 */

                double motion = CustomFallCurve(p);

                y = Lerp(
                    230,
                    570,
                    motion);

                /*
                 * 横向扩张不是跟 y 线性绑定。
                 *
                 * 下落过程中逐渐摊开。
                 */
                double expansion = SmoothStep(p);

                width = Lerp(
                    105,
                    430,
                    expansion);

                /*
                 * 高度降低，但保留液体感。
                 */
                height = Lerp(
                    170,
                    105,
                    expansion);

                RenderFreeBlob(
                    centerX,
                    y,
                    width,
                    height,
                    p);

                return;
            }


            // =====================================
            // Phase 4
            // 急刹 + 胶囊
            // =====================================

            {
                double p = (t - 0.70) / 0.30;

                /*
                 * 强烈减速。
                 *
                 * 不是立即停止，而是：
                 *
                 *   ↓
                 *   ↓
                 *   ↓
                 *   ↓
                 *   ╰─╮
                 *     ╰──
                 */
                double brake = BrakeCurve(p);

                y = Lerp(
                    570,
                    610,
                    brake);

                /*
                 * 横向继续扩张一点。
                 */
                double expansion = EaseOutCubic(p);

                width = Lerp(
                    430,
                    510,
                    expansion);

                /*
                 * 高度最终稳定。
                 */
                height = Lerp(
                    105,
                    92,
                    expansion);

                /*
                 * 最后一点点 overshoot。
                 */
                double spring = SmallSpring(p);

                width += spring;

                RenderFreeBlob(
                    centerX,
                    y,
                    width,
                    height,
                    1.0);
            }
        }


        // ============================================================
        // Attached Blob
        // ============================================================

        private void RenderAttachedBlob(
            double centerX,
            double centerY,
            double width,
            double height,
            double progress)
        {
            double left = centerX - width / 2;
            double right = centerX + width / 2;

            double bottom = centerY + height / 2;

            /*
             * 顶部直接贴在屏幕边缘。
             *
             * 重点：
             *
             *     ━━━━━━━━━━━━━
             *          ╲    ╱
             *           ╲  ╱
             *            ╲╱
             *
             * 它不是一个普通圆。
             */

            double top = 0;

            double shoulder = Math.Min(
                width * 0.30,
                35);

            StreamGeometry geometry = new();

            using (StreamGeometryContext ctx =
                   geometry.Open())
            {
                ctx.BeginFigure(
                    new Point(left, top),
                    true,
                    true);

                // 左侧
                ctx.BezierTo(
                    new Point(
                        left,
                        bottom * 0.45),

                    new Point(
                        left + width * 0.02,
                        bottom * 0.80),

                    new Point(
                        centerX,
                        bottom),
                    true,
                    true);

                // 右侧
                ctx.BezierTo(
                    new Point(
                        right - width * 0.02,
                        bottom * 0.80),

                    new Point(
                        right,
                        bottom * 0.45),

                    new Point(
                        right,
                        top),
                    true,
                    true);

                // 顶部回到左侧
                ctx.LineTo(
                    new Point(left, top),
                    true,
                    true);
            }

            Blob.Data = geometry;
        }


        // ============================================================
        // Free Blob
        // ============================================================

        private void RenderFreeBlob(
            double centerX,
            double centerY,
            double width,
            double height,
            double progress)
        {
            double left = centerX - width / 2;
            double right = centerX + width / 2;

            double top = centerY - height / 2;
            double bottom = centerY + height / 2;

            /*
             * 这里不是标准 RoundedRectangle。
             *
             * 用 4 组 Bézier：
             *
             *       ╭────────╮
             *     ╭            ╮
             *    │              │
             *     ╰            ╯
             *       ╰────────╯
             *
             * 让它稍微具有“不规则液体”感。
             */

            double rx = width * 0.18;
            double ry = height * 0.50;

            /*
             * 中间阶段稍微保留上下张力。
             */
            double tension =
                1.0 - Math.Abs(progress - 0.5) * 0.25;

            StreamGeometry geometry = new();

            using (StreamGeometryContext ctx =
                   geometry.Open())
            {
                ctx.BeginFigure(
                    new Point(
                        centerX,
                        top),
                    true,
                    true);

                // 左上 → 左侧
                ctx.BezierTo(
                    new Point(
                        centerX - rx,
                        top),

                    new Point(
                        left,
                        top + ry * 0.35),

                    new Point(
                        left,
                        centerY),
                    true,
                    true);

                // 左侧 → 左下
                ctx.BezierTo(
                    new Point(
                        left,
                        centerY + ry * 0.60),

                    new Point(
                        centerX - rx,
                        bottom),

                    new Point(
                        centerX,
                        bottom),
                    true,
                    true);

                // 右下
                ctx.BezierTo(
                    new Point(
                        centerX + rx,
                        bottom),

                    new Point(
                        right,
                        centerY + ry * 0.60),

                    new Point(
                        right,
                        centerY),
                    true,
                    true);

                // 右侧 → 右上
                ctx.BezierTo(
                    new Point(
                        right,
                        top + ry * 0.35),

                    new Point(
                        centerX + rx,
                        top),

                    new Point(
                        centerX,
                        top),
                    true,
                    true);
            }

            Blob.Data = geometry;
        }


        // ============================================================
        // Motion Curves
        // ============================================================

        private static double EaseOutCubic(double t)
        {
            t = Clamp(t);

            return 1.0 -
                   Math.Pow(1.0 - t, 3);
        }


        private static double EaseInOutCubic(double t)
        {
            t = Clamp(t);

            if (t < 0.5)
            {
                return 4 * t * t * t;
            }

            return 1 -
                   Math.Pow(-2 * t + 2, 3) / 2;
        }


        private static double SmoothStep(double t)
        {
            t = Clamp(t);

            return t * t * (3 - 2 * t);
        }


        /*
         * 这是整个动画最重要的运动曲线。
         *
         * 不是：
         *
         *     0 → 慢 → 快
         *
         * 而是：
         *
         *     已经有初速度
         *          ↓
         *       小加速度
         *          ↓
         *       高速
         *          ↓
         *       强减速
         *          ↓
         *        停止
         */
        private static double CustomFallCurve(double t)
        {
            t = Clamp(t);

            /*
             * 前 65%：
             * 保持较高速度。
             */
            if (t < 0.65)
            {
                double p = t / 0.65;

                return 0.65 * (
                    0.20 * p +
                    0.80 * p * p);
            }

            /*
             * 后 35%：
             * 强烈刹车。
             */
            double q = (t - 0.65) / 0.35;

            double brake =
                1 -
                Math.Pow(1 - q, 3);

            return 0.65 + 0.35 * brake;
        }


        /*
         * 最后阶段的急刹。
         */
        private static double BrakeCurve(double t)
        {
            t = Clamp(t);

            /*
             * 前面还有速度，
             * 后面快速趋近 1。
             */
            return 1 -
                   Math.Pow(1 - t, 4);
        }


        /*
         * 很小的横向回弹。
         *
         * 最终：
         *
         *     ────────────
         *          ↓
         *     ─────────────
         *          ↓
         *     ────────────
         */
        private static double SmallSpring(double t)
        {
            t = Clamp(t);

            double decay =
                Math.Pow(1 - t, 2);

            return
                Math.Sin(t * Math.PI * 2.2)
                * 12
                * decay;
        }


        // ============================================================
        // Utilities
        // ============================================================

        private static double Lerp(
            double a,
            double b,
            double t)
        {
            return a + (b - a) * t;
        }


        private static double Clamp(double value)
        {
            return Math.Max(
                0,
                Math.Min(
                    1,
                    value));
        }
    }
}