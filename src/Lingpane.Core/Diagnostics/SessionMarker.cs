using System.Text.Json;

namespace Lingpane.Core.Diagnostics;

/// <summary>一次会话的标记内容。字段都进日志/诊断，键名不随界面语言变。</summary>
public sealed record SessionStamp(int ProcessId, DateTimeOffset StartedAt, string Version);

/// <summary>
/// 上次退出是否干净（FR-10.6）。
///
/// 做法只有一条：启动时写一个标记文件，正常退出时删掉，下次启动若还在就说明上一次没走完收尾。
/// 不用"崩溃日志的时间戳比对"——异常可能发生在写日志之前（进程被杀、断电、显卡驱动挂掉），
/// 那种情况没有日志可对，但标记文件仍然留着。
///
/// 判据是"文件存在"，所以读取失败（内容坏了、被截断）也必须当作"上次异常"：
/// 宁可多提示一次，也不要因为解析不出内容就悄悄当成正常退出。
/// </summary>
public static class SessionMarker
{
    public const string FileName = "session.json";

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    /// <summary>写标记。失败（目录只读、便携盘拔了）不影响启动——只是这次失去检测能力。</summary>
    public static void Write(string directory, SessionStamp stamp)
    {
        Directory.CreateDirectory(directory);

        // 写临时文件再替换：直接覆盖时若正好在这一刻断电，会留下半截 JSON，
        // 下次读的时候也就分不清"坏文件"与"上一次崩溃"了——虽然两者都按崩溃处理，
        // 但"正常退出时留下半截文件"会让用户一直看到误报。
        string target = PathIn(directory);
        string temp = target + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(stamp));
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// 读上一次的标记。返回 null 表示"上次正常退出"（没有文件）；
    /// 文件在但内容解析不出时返回一个只有时间未知的 stamp——调用方照样要提示。
    /// </summary>
    public static SessionStamp? Read(string directory)
    {
        string path = PathIn(directory);
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            SessionStamp? stamp = JsonSerializer.Deserialize<SessionStamp>(json);
            return stamp ?? Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unknown;
        }
    }

    /// <summary>读不出内容时的占位：只知道"上次不正常"，不知道什么时候开始的。</summary>
    public static SessionStamp Unknown { get; } = new(0, DateTimeOffset.MinValue, string.Empty);

    /// <summary>正常退出时删掉标记。删不掉也无妨：下次会多一次提示，不会丢数据。</summary>
    public static void Clear(string directory)
    {
        try
        {
            string path = PathIn(directory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只读盘 / 文件被占用：留着就留着，下一次启动会提示一次
        }
    }
}
