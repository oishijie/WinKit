namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 撤销命令 —— 把「新增」「移除」「移动」等操作封装成可撤销/重做的原子步骤。
    /// 取代旧的「元素批」撤销模型（Stack&lt;List&lt;UIElement&gt;&gt;），
    /// 使「移动一个标注」「删除一个标注」也能各自单独撤销。
    /// </summary>
    public interface IUndoCommand
    {
        void Undo();
        void Redo();
    }
}
