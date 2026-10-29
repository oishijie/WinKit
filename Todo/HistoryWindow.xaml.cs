using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using WinKit.Todo.Models;
using WinKit.Todo.Services;
using WinButton = System.Windows.Controls.Button;

namespace WinKit.Todo
{
    /// <summary>
    /// 待办历史（回收站）窗口：还原 / 彻底删除 / 清空，失焦自动隐藏。
    /// </summary>
    public partial class HistoryWindow : Window
    {
        private readonly RecycleBinService _recycleBinService;
        private readonly ObservableCollection<RecycleBinItem> _items = new();
        private readonly MainWindow _todoWindow;

        public HistoryWindow(MainWindow todoWindow)
        {
            InitializeComponent();
            _todoWindow = todoWindow;
            _recycleBinService = todoWindow.RecycleBinService;
            _recycleBinService.ItemsChanged += OnRecycleBinItemsChanged;

            HistoryList.ItemsSource = _items;
            _items.CollectionChanged += (s, e) => UpdateUIStates();
            HistoryList.SelectionChanged += (s, e) => UpdateUIStates();

            Loaded += (s, e) => ReloadRecycleBin();
        }

        private void OnRecycleBinItemsChanged(object? sender, EventArgs e)
        {
            ReloadRecycleBin();
        }

        public void ReloadRecycleBin()
        {
            _items.Clear();
            foreach (var item in _recycleBinService.LoadItems())
            {
                _items.Add(item);
            }
            UpdateUIStates();
        }

        private void UpdateUIStates()
        {
            CountText.Text = $"{_items.Count} 条已删除待办";
            EmptyPlaceholder.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearAllBtn.IsEnabled = _items.Count > 0;

            var selected = HistoryList.SelectedItems.Count;
            RestoreSelectedBtn.IsEnabled = selected > 0;
            DeleteSelectedBtn.IsEnabled = selected > 0;
            RestoreSelectedBtn.Content = selected > 0 ? $"还原所选({selected})" : "还原所选";
            DeleteSelectedBtn.Content = selected > 0 ? $"删除所选({selected})" : "删除所选";
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            // 与剪贴板面板一致：失焦自动隐藏
            Hide();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0) return;

            if (System.Windows.MessageBox.Show("确定清空全部待办历史记录吗？此操作不可撤销。",
                    "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _items.Clear();
                _recycleBinService.Clear();
            }
        }

        private void RestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is WinButton btn && btn.Tag is RecycleBinItem item)
            {
                // 还原待办：完整保持原 ID 和原始创建时间
                var restoredTodo = new TodoItem
                {
                    Id = item.Id,
                    Title = item.Title,
                    CreatedAt = item.CreatedAt
                };

                _todoWindow.RestoreTodoItem(restoredTodo);
                _items.Remove(item);
                _recycleBinService.SaveItems(_items);
            }
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is WinButton btn && btn.Tag is RecycleBinItem item)
            {
                _items.Remove(item);
                _recycleBinService.SaveItems(_items);
            }
        }

        private void RestoreSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = HistoryList.SelectedItems.Cast<RecycleBinItem>().ToList();
            if (selected.Count == 0) return;

            foreach (var item in selected)
            {
                // 还原待办：完整保持原 ID 和原始创建时间（与单项还原一致）
                var restoredTodo = new TodoItem
                {
                    Id = item.Id,
                    Title = item.Title,
                    CreatedAt = item.CreatedAt
                };
                _todoWindow.RestoreTodoItem(restoredTodo);
                _items.Remove(item);
            }
            _recycleBinService.SaveItems(_items);
        }

        private void DeleteSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = HistoryList.SelectedItems.Cast<RecycleBinItem>().ToList();
            if (selected.Count == 0) return;

            if (System.Windows.MessageBox.Show($"确定彻底删除所选 {selected.Count} 条待办吗？此操作不可撤销。",
                    "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (var item in selected)
                {
                    _items.Remove(item);
                }
                _recycleBinService.SaveItems(_items);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _recycleBinService.ItemsChanged -= OnRecycleBinItemsChanged;
            base.OnClosed(e);
        }
    }
}
