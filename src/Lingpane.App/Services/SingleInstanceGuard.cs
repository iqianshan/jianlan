using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Lingpane.App.Services;

/// <summary>
/// 单实例守卫（FR-10.1）。
///
/// 启动成本约束：只做一次 Mutex 创建（微秒级），命名管道服务器在窗口显示**之后**才启动，
/// 不进启动预算。第二次启动的进程把命令行参数转交给已有实例后立即退出，
/// 这样"双击一个文件夹 → 在已开的窗口里打开"不会出现两个进程。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    // Local\ 前缀表示只在本登录会话内唯一：多用户同时登录时互不干扰
    private const string MutexName = @"Local\Lingpane.SingleInstance.v1";
    private const string PipeName = "Lingpane.Instance.v1";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 单实例相关问题只写日志，不弹窗、不打断启动。
    /// 这类失败的表现是"多开了一个窗口"，没有日志就只能猜。
    /// </summary>
    private static void Trace(string message)
    {
        try
        {
            string dir = Core.Diagnostics.AppPaths.LogsDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "singleinstance.log"),
                $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Environment.ProcessId}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    /// <summary>
    /// 尝试成为主实例。返回 false 表示已有实例在运行，调用方应转交参数后退出。
    /// </summary>
    public static bool TryAcquire(out SingleInstanceGuard guard)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        guard = new SingleInstanceGuard { _mutex = mutex };

        if (!createdNew)
        {
            // 没拿到所有权，这个句柄就没用了，尽早释放
            guard.Dispose();
            Trace("Mutex 已被占用，判定为非主实例");
            return false;
        }

        Trace("成为主实例");
        return true;
    }

    /// <summary>
    /// 在后台监听后续启动请求。callback 在管道线程上执行，调用方需自行切回 UI 线程。
    /// </summary>
    public void StartListening(Action<string[]> onArgs)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Trace("开始监听转交请求");

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? payload = await reader.ReadLineAsync(token);

                    if (!string.IsNullOrEmpty(payload))
                    {
                        Trace($"收到转交参数：{payload.Replace('\n', ' ')}");
                        onArgs(payload.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    // 客户端提前断开：继续等待下一次连接，不退出循环
                    Trace($"管道 IO 异常：{ex.Message}");
                }
                catch (Exception ex)
                {
                    // 其它异常（权限、平台限制）会让监听彻底失效，必须记下来
                    Trace($"管道监听中止：{ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }
        }, token);
    }

    /// <summary>
    /// 把参数转交给主实例。失败返回 false（主实例可能刚好在退出），
    /// 由调用方决定是否继续启动。
    /// </summary>
    public static bool TrySend(string[] args, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(string.Join('\n', args));
            writer.Flush();
            Trace("参数转交成功");
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Trace($"参数转交失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;

        if (_mutex is null) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有所有权时释放会抛这个，忽略
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
