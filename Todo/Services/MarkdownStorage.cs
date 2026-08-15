using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WinKit.Common;
using WinKit.Todo.Models;

namespace WinKit.Todo.Services
{
    public class MarkdownStorage
    {
        private readonly string _filePath;

        public MarkdownStorage()
        {
            AppPaths.EnsureDirectories();
            _filePath = AppPaths.Todos;

            if (!File.Exists(_filePath))
            {
                File.WriteAllText(_filePath, string.Empty, Encoding.UTF8);
            }
        }

        public List<TodoItem> LoadTodos()
        {
            var todos = new List<TodoItem>();
            if (!File.Exists(_filePath)) return todos;

            var lines = File.ReadAllLines(_filePath, Encoding.UTF8);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // 完成态：标准任务列表标记 "- [x] Title"（大小写不敏感），
                // 未完成用 "- [ ] Title"，旧版 "- Title" 视为未完成（向后兼容）。
                bool done = false;
                string title;
                if (trimmed.StartsWith("- [x]") || trimmed.StartsWith("- [X]"))
                {
                    done = true;
                    title = trimmed.Substring(5).Trim();
                }
                else if (trimmed.StartsWith("- [ ]"))
                {
                    title = trimmed.Substring(5).Trim();
                }
                else
                {
                    title = trimmed.StartsWith("- ") ? trimmed.Substring(2) : trimmed;
                }

                title = title.Replace("\\n", "\n").Replace("\\r", "\r");
                todos.Add(new TodoItem { Title = title, IsDone = done });
            }
            return todos;
        }

        public void SaveTodos(IEnumerable<TodoItem> items)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                var escaped = item.Title.Replace("\r", "\\r").Replace("\n", "\\n");
                var prefix = item.IsDone ? "- [x] " : "- [ ] ";
                sb.AppendLine($"{prefix}{escaped}");
            }
            File.WriteAllText(_filePath, sb.ToString(), Encoding.UTF8);
        }
    }
}
