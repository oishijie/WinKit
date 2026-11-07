using System.Windows;
using System.Windows.Media;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 标注对象接口 —— 借鉴 SnowAir ShapeBase 的「能力协商」思想，
    /// 但精简为 WPF 截图标注所需的核心能力。
    ///
    /// 生命周期：CreatePreview（按下建临时元素）→ Update（拖拽实时更新）
    ///          → Commit（松手定格，返回 false 表示太小/无效，元素被丢弃）。
    /// 提交后通过 <see cref="Visual"/> 拿到最终元素，进入「可选中/可拖动」状态。
    /// </summary>
    public interface IAnnotation
    {
        /// <summary>按下鼠标时创建一个临时预览元素（拖拽过程中可见）。</summary>
        FrameworkElement CreatePreview();

        /// <summary>拖拽中实时更新预览元素。</summary>
        void Update(FrameworkElement el, Point start, Point current);

        /// <summary>松手定格。返回 false 表示该标注太小/无效，应丢弃。</summary>
        bool Commit(FrameworkElement el);

        /// <summary>提交后的最终元素（挂在 OverlayCanvas 上）。</summary>
        FrameworkElement Visual { get; }

        /// <summary>点选命中检测（在 OverlayCanvas 坐标系中）。</summary>
        bool HitTest(Point p);

        /// <summary>设置选中态：选中时描边加粗或加虚线外框，取消时恢复。</summary>
        void SetSelected(bool selected);

        /// <summary>整体平移（拖动选中的标注）。</summary>
        void Translate(double dx, double dy);

        /// <summary>几何 + 属性快照，供命令化撤销恢复。</summary>
        object Snapshot();

        /// <summary>从快照恢复几何 + 属性。</summary>
        void Restore(object state);

        /// <summary>标注颜色。</summary>
        Color Color { get; set; }

        /// <summary>线宽（画笔/箭头/矩形描边；文字字号另算）。</summary>
        double Thickness { get; set; }

        // ── 缩放 / 旋转能力（能力协商：返回 false 表示该形状不支持）──

        /// <summary>是否支持 8 控点缩放（矩形/椭圆 true，箭头/画笔/马赛克 false）。</summary>
        bool SupportsResize { get; }

        /// <summary>是否支持旋转（矩形/椭圆/箭头 true，文字/马赛克/画笔 false）。</summary>
        bool SupportsRotation { get; }

        /// <summary>轴对齐包围盒（OverlayCanvas 坐标系），用于控点定位与命中。</summary>
        Rect Bounds { get; }

        /// <summary>按控点缩放。handle 0-7（四角 + 四边中点，与 SnowAir 同语义）。</summary>
        void Resize(int handle, Point p);

        /// <summary>绕中心旋转 delta 弧度（正 = 顺时针）。</summary>
        void Rotate(double deltaRad);

        /// <summary>当前旋转角（弧度）。</summary>
        double Angle { get; set; }
    }
}
