using System.Windows;
using System.Windows.Media.Animation;

namespace Luna.Controls;

/// <summary>
/// S 曲线（smoothstep）缓动函数：3t² − 2t³，起止平缓、中间加速。
/// 用于胶囊展开/收起等需要柔和过渡的动画。
/// </summary>
public sealed class SmoothStepEase : EasingFunctionBase
{
    protected override double EaseInCore(double normalizedTime)
    {
        normalizedTime = Math.Clamp(normalizedTime, 0, 1);
        return normalizedTime * normalizedTime * (3 - 2 * normalizedTime);
    }

    protected override Freezable CreateInstanceCore() => new SmoothStepEase();
}
