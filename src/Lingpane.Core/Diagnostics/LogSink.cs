namespace Lingpane.Core.Diagnostics;

/// <summary>
/// Core 写 app.log 的出口。依赖方向是 App → Core，Core 不能反向引用 <c>AppLog</c>，
/// 所以由 App 在启动时把写入方法挂上来（见 <c>App.xaml.cs</c>）。
///
/// 未装配时静默丢弃：单元测试跑一行代码不该为了写日志去搭日志目录。
/// </summary>
public static class LogSink
{
    /// <summary>参数为（事件名、错误码、正文），与 <c>AppLog.Warn</c> 一致。</summary>
    public static Action<string, string, string>? Write { get; set; }

    public static void Warn(string eventName, string code, string message)
        => Write?.Invoke(eventName, code, message);
}
