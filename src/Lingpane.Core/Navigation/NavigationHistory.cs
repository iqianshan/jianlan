namespace Lingpane.Core.Navigation;

/// <summary>
/// 前进/后退栈（FR-6.2）。行为对齐资源管理器：
/// 依次打开 A→B→C，后退得到 B、A；在 B 再前进回到 C。
/// 后退后再打开新目录，前进栈必须清空——否则前进会跳到一个与当前分支无关的位置。
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>栈深上限。资源管理器不设上限，但无限增长在长会话里没有意义。</summary>
    public const int MaxDepth = 100;

    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    /// <summary>当前所在路径。<see cref="Push"/> 之后更新。</summary>
    public string? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public int BackDepth => _back.Count;

    public int ForwardDepth => _forward.Count;

    /// <summary>导航成功之后调用。同一路径重复推入会被忽略（刷新不算一次导航）。</summary>
    public void Push(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (string.Equals(path, Current, StringComparison.OrdinalIgnoreCase)) return;

        if (Current is not null)
        {
            _back.Add(Current);
            if (_back.Count > MaxDepth) _back.RemoveAt(0);
        }

        Current = path;
        _forward.Clear();
    }

    public bool TryGoBack(out string path)
    {
        if (_back.Count == 0 || Current is null)
        {
            path = string.Empty;
            return false;
        }

        _forward.Add(Current);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        path = Current;
        return true;
    }

    public bool TryGoForward(out string path)
    {
        if (_forward.Count == 0 || Current is null)
        {
            path = string.Empty;
            return false;
        }

        _back.Add(Current);
        Current = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        path = Current;
        return true;
    }

    /// <summary>清空（例如导航到不存在的目录失败后，回退到上一个有效位置时使用）。</summary>
    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
        Current = null;
    }
}
