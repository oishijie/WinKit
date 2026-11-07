using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 标注控制器 —— 承载三件事：
    ///   1. 工具 → 标注对象的工厂映射（新增形状只需注册一项，取代旧的三处 switch）；
    ///   2. 当前选中标注的追踪与命中/拖动分发；
    ///   3. 命令化撤销栈（新增 / 移除 / 移动各自可单独撤销）。
    /// </summary>
    public sealed class AnnotationController
    {
        public enum ToolKind { Rect, Arrow, Pen, Mosaic, Ellipse, Eraser }

        private readonly Canvas _canvas;
        private readonly Stack<IUndoCommand> _undoStack = new();
        private readonly Stack<IUndoCommand> _redoStack = new();

        private readonly Color _defaultColor;
        private readonly double _defaultThickness;

        private Color _color;
        private double _thickness;

        private IAnnotation? _selected;
        private Point _dragLast;
        private Vector _dragTotal;
        private bool _dragging;
        private object? _resizeSnapshot;
        private double _rotateStartAngle;

        public AnnotationController(Canvas canvas, Color defaultColor, double defaultThickness)
        {
            _canvas = canvas;
            _defaultColor = defaultColor;
            _defaultThickness = defaultThickness;
            _color = defaultColor;
            _thickness = defaultThickness;
        }

        /// <summary>当前调色板颜色（新画的标注用它）。</summary>
        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                if (_selected != null) _selected.Color = value;
            }
        }

        /// <summary>当前线宽（新画的标注用它）。</summary>
        public double Thickness
        {
            get => _thickness;
            set
            {
                _thickness = value;
                if (_selected != null) _selected.Thickness = value;
            }
        }

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>当前是否有选中的标注。</summary>
        public bool HasSelection => _selected != null;

        /// <summary>按工具类型创建对应的标注对象（工厂）。马赛克需底图上下文，由宿主单独创建。</summary>
        public IAnnotation CreateAnnotation(ToolKind tool)
        {
            return tool switch
            {
                ToolKind.Rect => new RectAnnotation(_color, _thickness),
                ToolKind.Arrow => new ArrowAnnotation(_color, _thickness),
                ToolKind.Pen => new PenAnnotation(_color, _thickness),
                ToolKind.Ellipse => new EllipseAnnotation(_color, _thickness),
                _ => new RectAnnotation(_color, _thickness),
            };
        }

        /// <summary>把提交后的标注元素挂到画布，并记一条「新增」撤销命令。</summary>
        public void AddCommitted(IAnnotation annotation)
        {
            _canvas.Children.Add(annotation.Visual);
            PushUndo(new AddCommand(_canvas, annotation));
        }

        /// <summary>
        /// 尝试点选命中：返回 true 表示点中了某个标注并已选中它。
        /// 命中优先级：后画的在上层，从后往前找。
        /// </summary>
        public bool TrySelect(Point p)
        {
            Deselect();

            for (int i = _canvas.Children.Count - 1; i >= 0; i--)
            {
                if (_canvas.Children[i] is not FrameworkElement el) continue;
                if (el.Tag is IAnnotation anno && anno.HitTest(p))
                {
                    Select(anno);
                    return true;
                }
            }
            return false;
        }

        /// <summary>开始拖动选中的标注。</summary>
        public bool BeginDrag(Point p)
        {
            if (_selected == null) return false;
            _dragLast = p;
            _dragTotal = new Vector(0, 0);
            _dragging = true;
            return true;
        }

        /// <summary>拖动中：计算位移并平移，累计总位移。</summary>
        public void Drag(Point p)
        {
            if (!_dragging || _selected == null) return;
            double dx = p.X - _dragLast.X;
            double dy = p.Y - _dragLast.Y;
            _selected.Translate(dx, dy);
            _dragTotal += new Vector(dx, dy);
            _dragLast = p;
        }

        /// <summary>结束拖动：若真发生了位移，记一条「移动」撤销命令。</summary>
        public void EndDrag()
        {
            if (_dragging && _selected != null && _dragTotal.Length > 0)
            {
                PushUndo(new MoveCommand(_selected, _dragTotal.X, _dragTotal.Y));
            }
            _dragging = false;
        }

        /// <summary>删除选中的标注，记一条「移除」撤销命令。</summary>
        public void DeleteSelected()
        {
            if (_selected == null) return;
            PushUndo(new RemoveCommand(_canvas, _selected));
            Deselect();
        }

        /// <summary>取消选中（回到无选中态）。</summary>
        public void Deselect()
        {
            if (_selected != null) _selected.SetSelected(false);
            _selected = null;
        }

        /// <summary>获取当前选中的标注（供宿主做缩放/旋转交互）。</summary>
        public IAnnotation? Selected => _selected;

        /// <summary>
        /// 橡皮擦：删除所有与给定轨迹相交的标注。
        /// track 是橡皮擦拖过的轨迹点（OverlayCanvas 坐标）。
        /// 命中一个删一个，合并成一条「批量移除」撤销命令。
        /// </summary>
        public void EraseAlong(IReadOnlyList<Point> track)
        {
            var hit = new List<IAnnotation>();
            for (int i = _canvas.Children.Count - 1; i >= 0; i--)
            {
                if (_canvas.Children[i] is not FrameworkElement el) continue;
                if (el.Tag is not IAnnotation anno) continue;
                if (anno == _selected) continue; // 当前选中的暂不擦（避免误删正在编辑的）
                if (TrackHitsAnnotation(track, anno))
                {
                    hit.Add(anno);
                }
            }
            if (hit.Count == 0) return;
            PushUndo(new EraseCommand(_canvas, hit));
            Deselect();
        }

        /// <summary>判断橡皮擦轨迹是否命中某个标注（轨迹任一点到标注距离足够近）。</summary>
        private static bool TrackHitsAnnotation(IReadOnlyList<Point> track, IAnnotation anno)
        {
            foreach (var p in track)
            {
                if (anno.HitTest(p)) return true;
            }
            return false;
        }

        /// <summary>缩放选中的标注：记录起始快照，供撤销。</summary>
        public void BeginResize()
        {
            if (_selected == null) return;
            _resizeSnapshot = _selected.Snapshot();
        }

        /// <summary>按控点缩放选中标注（handle 0-7，p 为反旋转后的局部坐标）。</summary>
        public void ResizeSelected(int handle, Point p)
        {
            if (_selected == null || !_selected.SupportsResize) return;
            _selected.Resize(handle, p);
        }

        /// <summary>结束缩放：若几何有变化，记一条「缩放」撤销命令。</summary>
        public void EndResize()
        {
            if (_selected == null || _resizeSnapshot == null) return;
            PushUndo(new ResizeCommand(_selected, _resizeSnapshot, _selected.Snapshot()));
            _resizeSnapshot = null;
        }

        /// <summary>旋转选中标注（deltaRad 弧度）。</summary>
        public void RotateSelected(double deltaRad)
        {
            if (_selected == null || !_selected.SupportsRotation) return;
            _selected.Rotate(deltaRad);
        }

        /// <summary>开始旋转：记录起始角，供撤销。</summary>
        public void BeginRotate()
        {
            if (_selected == null) return;
            _rotateStartAngle = _selected.Angle;
        }

        /// <summary>结束旋转：若角度有变化，记一条「旋转」撤销命令。</summary>
        public void EndRotate()
        {
            if (_selected == null) return;
            if (Math.Abs(_selected.Angle - _rotateStartAngle) > 1e-6)
            {
                PushUndo(new RotateCommand(_selected, _rotateStartAngle, _selected.Angle));
            }
        }

        public void Undo()
        {
            if (_undoStack.Count == 0) return;
            var cmd = _undoStack.Pop();
            cmd.Undo();
            _redoStack.Push(cmd);
            if (_selected != null) Deselect();
        }

        public void Redo()
        {
            if (_redoStack.Count == 0) return;
            var cmd = _redoStack.Pop();
            cmd.Redo();
            _undoStack.Push(cmd);
        }

        private void Select(IAnnotation anno)
        {
            _selected = anno;
            anno.SetSelected(true);
        }

        private void PushUndo(IUndoCommand cmd)
        {
            _undoStack.Push(cmd);
            _redoStack.Clear();
        }

        // ── 撤销命令实现 ──────────────────────────────

        /// <summary>「新增」：撤销 = 移除元素，重做 = 加回元素。</summary>
        private sealed class AddCommand : IUndoCommand
        {
            private readonly Canvas _canvas;
            private readonly IAnnotation _annotation;

            public AddCommand(Canvas canvas, IAnnotation annotation)
            {
                _canvas = canvas;
                _annotation = annotation;
            }

            public void Undo() => _canvas.Children.Remove(_annotation.Visual);
            public void Redo() => _canvas.Children.Add(_annotation.Visual);
        }

        /// <summary>「移除」：撤销 = 加回元素，重做 = 移除元素。</summary>
        private sealed class RemoveCommand : IUndoCommand
        {
            private readonly Canvas _canvas;
            private readonly IAnnotation _annotation;

            public RemoveCommand(Canvas canvas, IAnnotation annotation)
            {
                _canvas = canvas;
                _annotation = annotation;
            }

            public void Undo() => _canvas.Children.Add(_annotation.Visual);
            public void Redo() => _canvas.Children.Remove(_annotation.Visual);
        }

        /// <summary>「移动」：撤销 = 反向平移，重做 = 正向平移。</summary>
        private sealed class MoveCommand : IUndoCommand
        {
            private readonly IAnnotation _annotation;
            private readonly double _dx;
            private readonly double _dy;

            public MoveCommand(IAnnotation annotation, double dx, double dy)
            {
                _annotation = annotation;
                _dx = dx;
                _dy = dy;
            }

            public void Undo() => _annotation.Translate(-_dx, -_dy);
            public void Redo() => _annotation.Translate(_dx, _dy);
        }

        /// <summary>「批量移除」（橡皮擦）：撤销 = 全部加回，重做 = 全部移除。</summary>
        private sealed class EraseCommand : IUndoCommand
        {
            private readonly Canvas _canvas;
            private readonly IReadOnlyList<IAnnotation> _annotations;

            public EraseCommand(Canvas canvas, IReadOnlyList<IAnnotation> annotations)
            {
                _canvas = canvas;
                _annotations = annotations;
            }

            public void Undo()
            {
                foreach (var a in _annotations) _canvas.Children.Add(a.Visual);
            }

            public void Redo()
            {
                foreach (var a in _annotations) _canvas.Children.Remove(a.Visual);
            }
        }

        /// <summary>「缩放」：撤销/重做都通过快照恢复几何。</summary>
        private sealed class ResizeCommand : IUndoCommand
        {
            private readonly IAnnotation _annotation;
            private readonly object _before;
            private readonly object _after;

            public ResizeCommand(IAnnotation annotation, object before, object after)
            {
                _annotation = annotation;
                _before = before;
                _after = after;
            }

            public void Undo() => _annotation.Restore(_before);
            public void Redo() => _annotation.Restore(_after);
        }

        /// <summary>「旋转」：撤销/重做通过设置角度实现。</summary>
        private sealed class RotateCommand : IUndoCommand
        {
            private readonly IAnnotation _annotation;
            private readonly double _before;
            private readonly double _after;

            public RotateCommand(IAnnotation annotation, double before, double after)
            {
                _annotation = annotation;
                _before = before;
                _after = after;
            }

            public void Undo() => _annotation.Angle = _before;
            public void Redo() => _annotation.Angle = _after;
        }
    }
}
