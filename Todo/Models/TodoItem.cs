using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinKit.Todo.Models
{
    public class TodoItem : INotifyPropertyChanged
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        private bool _isDone;
        public bool IsDone
        {
            get => _isDone;
            set => SetField(ref _isDone, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
