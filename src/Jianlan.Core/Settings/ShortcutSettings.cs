using System;
using System.Collections.Generic;
using Jianlan.Core.Input;

namespace Jianlan.Core.Settings;

/// <summary>
/// 快捷键设置（settings-schema §2 的 <c>shortcuts</c>）。
///
/// **只存差异，不存全表**：<see cref="Overrides"/> 里没有的命令用程序内置的默认键位。
/// 存全表会把"这一版的默认值"永久冻进用户文件，以后想调整默认键位再也到不了老用户；
/// 与标签只存改过的名字、第二窗格没调过不写盘是同一个口径。
///
/// 值为空串表示"用户明确解绑"。这与"键不存在"是两件事：
/// 前者是意图（这条命令不要快捷键），后者是"没改过"，必须区分。
/// </summary>
public sealed class ShortcutSettings
{
    /// <summary>命令 Id → 键位文本（<c>Ctrl+Shift+R</c>）或空串（解绑）。</summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 全局唤起键（显示/收起主窗口）。<c>null</c> = 关闭，也是默认值：
    /// 系统级热键会抢走其它程序的同一组合，默认开着等于替用户做了这个决定。
    /// </summary>
    public string? ShowWindow { get; set; }

    /// <summary>
    /// 语法归一：认不出的键丢掉（回落默认），认得出的写成规范形式。
    /// 冲突与"该不该允许"不在这里判——那需要整张表，归 <c>KeyBindings</c>。
    /// </summary>
    public void Normalize()
    {
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string key, string? value) in Overrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            string commandId = key.Trim();
            if (commandId.Length > 120) continue;

            // 空白 = 用户把这一项的键位清掉了，是有效意图，不能当成"没写"
            if (string.IsNullOrWhiteSpace(value))
            {
                clean[commandId] = string.Empty;
                continue;
            }

            // 解析失败 = 写坏了。丢掉这一项、回落默认，比让整条命令失去快捷键更安全
            if (ShortcutGesture.TryParse(value, out ShortcutGesture gesture) && !gesture.IsEmpty)
            {
                clean[commandId] = gesture.Format();
            }
        }

        Overrides = clean;
        ShowWindow = NormalizeGesture(ShowWindow);
    }

    /// <summary>空串/空白/非法值一律归一为 <c>null</c>（关闭）。</summary>
    public static string? NormalizeGesture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return ShortcutGesture.TryParse(text, out ShortcutGesture gesture) && !gesture.IsEmpty
            ? gesture.Format()
            : null;
    }
}
