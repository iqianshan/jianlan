using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PreviewProbe;

internal static class Program
{
    private const string HandlerKeyFormat = @"ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    // 处理器多数是 Apartment 的 COM 对象，主线程必须是 STA，否则 CoCreateInstance 直接给 RPC_E_CHANGED_MODE
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.WriteLine("用法：PreviewProbe.exe <扩展名或文件路径> [...]");
            return 1;
        }

        Console.WriteLine($"进程位宽：{(Environment.Is64BitProcess ? "x64" : "x86")}");

        foreach (string arg in args)
        {
            string ext = arg.StartsWith('.') ? arg : Path.GetExtension(arg);
            if (string.IsNullOrEmpty(ext))
            {
                Console.WriteLine($"{arg,-10} 取不到扩展名");
                continue;
            }

            Guid clsid = ReadHandlerClsid(ext);
            if (clsid == Guid.Empty)
            {
                Console.WriteLine($"{ext,-8} 没有注册预览处理器");
                continue;
            }

            Type? type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (type is null)
            {
                Console.WriteLine($"{ext,-8} {clsid} 无法解析为 COM 类型");
                continue;
            }

            try
            {
                object? instance = Activator.CreateInstance(type);
                if (instance is null)
                {
                    Console.WriteLine($"{ext,-8} {clsid} 激活返回 null");
                    continue;
                }

                try
                {
                    string handler = Hr(Query(instance, new Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")));
                    string file = Hr(Query(instance, new Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")));
                    string item = Hr(Query(instance, new Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")));
                    string stream = Hr(Query(instance, new Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")));
                    string site = Hr(Query(instance, new Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")));

                    Console.WriteLine(
                        $"{ext,-8} {clsid} 激活 OK  IPreviewHandler={handler} WithFile={file} " +
                        $"WithItem={item} WithStream={stream} IObjectWithSite={site}");
                }
                finally
                {
                    if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ext,-8} {clsid} 激活失败 {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        return 0;
    }

    private static Guid ReadHandlerClsid(string ext)
    {
        string suffix = ext + @"\" + HandlerKeyFormat;

        // 两个位宽视图都要看：中文环境的预览处理器多半是 32 位 DLL，注册在 Wow6432Node 下
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);

            foreach (RegistryKey root in new RegistryKey[] { hkcu, hklm })
            {
                foreach (string prefix in new[] { "Software\\Classes\\", string.Empty })
                {
                    using RegistryKey? key = root.OpenSubKey(prefix + suffix);
                    if (key is null) continue;

                    if (key.GetValue(string.Empty) is string text && Guid.TryParse(text, out Guid parsed)) return parsed;
                }
            }
        }

        return Guid.Empty;
    }

    private static string Hr(int hr) => hr == 0 ? "OK" : $"0x{hr:X8}";

    private static int Query(object instance, Guid iid)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(instance);

        try
        {
            int hr = Marshal.QueryInterface(unknown, ref iid, out IntPtr pointer);
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            return hr;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
