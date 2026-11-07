using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架卡片：根据 RackItem 的类型渲染成图片卡 / 文本卡 / 待办卡。
    /// 点击复制到剪贴板；拖拽走 DoDragDrop（图片=位图，文本/待办=文字）。
    /// </summary>
    public partial class RackCard : UserControl
    {
        private readonly RackItem _item;
        private Point _dragStart;
        private bool _mouseDown;

        public RackCard(RackItem item)
        {
            InitializeComponent();
            _item = item;
            Render();
        }

        private void Render()
        {
            switch (_item.Type)
            {
                case RackItemType.Image:
                    ImgPreview.Source = _item.Image;
                    ImgPreview.Visibility = Visibility.Visible;
                    TextArea.Visibility = Visibility.Collapsed;
                    break;

                case RackItemType.Text:
                    ImgPreview.Visibility = Visibility.Collapsed;
                    TextArea.Visibility = Visibility.Visible;
                    TypeBar.Background = new SolidColorBrush(Color.FromRgb(0xAC, 0x7F, 0x5E));
                    TextContent.Text = _item.Text;
                    DoneMark.Visibility = Visibility.Collapsed;
                    break;

                case RackItemType.Todo:
                    ImgPreview.Visibility = Visibility.Collapsed;
                    TextArea.Visibility = Visibility.Visible;
                    TypeBar.Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
                    TextContent.Text = _item.Text;
                    DoneMark.Visibility = _item.IsDone ? Visibility.Visible : Visibility.Collapsed;
                    TextContent.TextDecorations = _item.IsDone
                        ? TextDecorations.Strikethrough
                        : null;
                    break;
            }
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = true;
            _dragStart = e.GetPosition(this);
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed) return;

            var pos = e.GetPosition(this);
            // 拖出判定：位移超过阈值才认为是拖拽（否则是点击复制）
            if (System.Math.Abs(pos.X - _dragStart.X) < 4 &&
                System.Math.Abs(pos.Y - _dragStart.Y) < 4)
                return;

            _mouseDown = false;
            StartDrag();
        }

        private void StartDrag()
        {
            DataObject data;
            if (_item.Type == RackItemType.Image && _item.Image != null)
            {
                data = new DataObject();
                data.SetImage(_item.Image);
            }
            else
            {
                data = new DataObject(DataFormats.UnicodeText, _item.Text ?? "");
            }
            // 一律 COPY，绝不 MOVE（避免搬走宿主数据）
            System.Windows.DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }

        /// <summary>点击复制到剪贴板（由 RackWindow 在 MouseLeftButtonUp 调用）。</summary>
        public void CopyToClipboard()
        {
            try
            {
                if (_item.Type == RackItemType.Image && _item.Image != null)
                    System.Windows.Clipboard.SetImage(_item.Image);
                else if (!string.IsNullOrEmpty(_item.Text))
                    System.Windows.Clipboard.SetText(_item.Text);
            }
            catch
            {
                // 剪贴板被占用等，静默失败
            }
        }
    }
}
