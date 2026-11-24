using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Jianlan.App.Services;

/// <summary>
/// 文件剪贴板，真身是系统剪贴板的 <c>CF_HDROP</c>（<see cref="DataFormats.FileDrop"/>）。
///
/// 早先这里只是一个进程内的路径列表，所以简览内部复制粘贴能跑，但到资源管理器里
/// Ctrl+V 毫无反应——系统剪贴板上从头到尾没有东西。现在复制/剪切会写进系统剪贴板，
/// 并附一个 <c>Preferred DropEffect</c>，让资源管理器知道这次该"复制"还是"移动"。
///
/// 读取以系统剪贴板为准：用户可能在别的程序里又复制了别的内容，进程内那份记录
/// 立刻就过期了。只有"写系统剪贴板失败"时才退回内部列表——那种情况通常是 Office
/// 之类的程序正占着剪贴板，过一会儿再复制一次就正常。
/// </summary>
internal static class FileClipboard
{
    /// <summary>
    /// 资源管理器靠这一项区分复制与剪切：剪切粘贴是移动，图标还会显示成半透明。
    /// 它不在 <see cref="DataFormats"/> 的常量里，只能按名字取。值是一个 4 字节
    /// 小端整数（DROPEFFECT_*），必须放在 HGLOBAL 里给出去。
    /// </summary>
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    /// <summary>写系统剪贴板失败时的兜底，保证程序内复制粘贴照旧能用。</summary>
    private static IReadOnlyList<string> _fallback = [];

    private static bool _fallbackCut;

    /// <summary>上一次 <see cref="Set"/> 没能写进系统剪贴板，只有内存里这一份。</summary>
    private static bool _fallbackArmed;

    public static bool HasData => Resolve().Paths.Length > 0;

    /// <summary>
    /// 记下复制/剪切的这批文件，并写进系统剪贴板。
    /// </summary>
    /// <returns>是否成功写进系统剪贴板；false 表示只能在简览内部粘贴。</returns>
    public static bool Set(IReadOnlyList<string> paths, bool cut)
    {
        _fallback = paths.ToArray();
        _fallbackCut = cut;

        if (_fallback.Count == 0)
        {
            Clear();
            return false;
        }

        bool written = TryWriteSystem(_fallback, cut);
        _fallbackArmed = !written;
        return written;
    }

    /// <summary>
    /// 取出要粘贴的那批文件。<paramref name="cut"/> 为 true 表示粘贴后要删源。
    /// </summary>
    public static bool TryGet(out string[] paths, out bool cut)
    {
        (paths, cut) = Resolve();
        return paths.Length > 0;
    }

    public static void Clear()
    {
        string[] mine = _fallback.ToArray();
        _fallback = [];
        _fallbackCut = false;
        _fallbackArmed = false;

        // 剪切粘贴成功后要连系统剪贴板一起清掉：否则用户以为这批已经用掉了，
        // 再到资源管理器里 Ctrl+V，会去移动一批已经不在的文件。
        // 只在系统剪贴板确实还是我们那一批时才动它——用户中途复制了别的东西，
        // 不能替他清掉。
        if (mine.Length == 0 || !SystemHolds(mine)) return;

        try
        {
            Clipboard.Clear();
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            Diagnostics.AppLog.Warn("fileclipboard", "E7005", $"清空剪贴板失败：{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
        }
    }

    private static (string[] Paths, bool Cut) Resolve()
    {
        // 系统剪贴板是"最后一次复制"的唯一事实来源
        if (TryReadSystem(out string[] paths, out bool cut)) return (paths, cut);

        // 读不到文件列表。如果上一次我们确实写进去了，那说明内容被别的程序
        // 换成了非文件（一段文字之类）——不能拿旧列表去粘，那是过期数据。
        if (_fallbackArmed) return (_fallback.ToArray(), _fallbackCut);

        return ([], false);
    }

    private static bool TryWriteSystem(IReadOnlyList<string> paths, bool cut)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            data.SetData(PreferredDropEffectFormat, EffectBytes(cut ? DropEffectMove : DropEffectCopy));

            // copy: true 会在这次调用里把内容落成 OLE 里的实体数据。不开它的话，
            // 对方粘贴时是在向我们这个进程"要数据"——简览一关，剪贴板就空了。
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            Diagnostics.AppLog.Warn("fileclipboard", "E7005", $"写入剪贴板失败：{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
            return false;
        }
    }

    private static bool TryReadSystem(out string[] paths, out bool cut)
    {
        paths = [];
        cut = false;

        try
        {
            if (!Clipboard.ContainsFileDropList()) return false;

            string[] list = [.. Clipboard.GetFileDropList().Cast<string>()];
            if (list.Length == 0) return false;

            paths = list;
            cut = (ReadDropEffect() & DropEffectMove) != 0;
            return true;
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            // 剪贴板被别的程序占着是常态，读不到就当没有，别冒泡到界面线程
            return false;
        }
    }

    private static bool SystemHolds(string[] mine)
    {
        if (!TryReadSystem(out string[] current, out _)) return false;
        return current.Length == mine.Length
               && mine.All(p => current.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private static int ReadDropEffect()
    {
        try
        {
            if (Clipboard.GetDataObject()?.GetData(PreferredDropEffectFormat) is not MemoryStream stream)
            {
                return DropEffectCopy;
            }

            if (stream.CanSeek) stream.Position = 0;

            byte[] buffer = new byte[4];
            if (stream.Read(buffer, 0, 4) < 4) return DropEffectCopy;

            return BitConverter.ToInt32(buffer, 0);
        }
        catch (Exception ex) when (ex is InvalidCastException || IsClipboardFailure(ex))
        {
            // 别的程序也可能挂同名格式但塞了别的东西，按"复制"处理最安全
            return DropEffectCopy;
        }
    }

    private static MemoryStream EffectBytes(int effect)
    {
        var stream = new MemoryStream(4);
        stream.Write(BitConverter.GetBytes(effect), 0, 4);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// 剪贴板相关的失败都长这几副面孔：被占用是 <see cref="COMException"/>，
    /// 跨线程是 <see cref="ThreadStateException"/>，历史遗留的包装是 <see cref="ExternalException"/>。
    /// </summary>
    private static bool IsClipboardFailure(Exception ex)
        => ex is ExternalException or ThreadStateException or ArgumentException;
}
