using System.Runtime.InteropServices;
using Avalonia;
using Jsr4Z8.Core.Native;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.Core.Queue;

namespace Jsr4Z8.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless release self-test (also proves native DLLs resolve from the
        // single-file bundle): Jsr4Z8.UI.exe --selftest [file.nef]
        if (args.Length is 1 or 2 && args[0] == "--selftest")
        {
            Environment.Exit(Selftest.Run(args.Length == 2 ? args[1] : null));
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Release QA self-test, no UI: ①PayloadManager.EnsureExtracted on the real
    /// embedded payload (progress to console; a pre-existing verified extraction is
    /// the fast path) ②assert the extracted backend engine (resources\jsr_backend.exe)
    /// and resources tree exist ③LibRaw smoke — version probe always, NEF decode when
    /// a file argument is given.
    /// Prints SELFTEST OK / SELFTEST FAIL and returns 0 / 1.
    /// </summary>
    private static class Selftest
    {
        public static int Run(string? nefPath)
        {
            AttachParentConsole();
            try
            {
                Console.WriteLine("JSR for Z8 selftest");
                Console.WriteLine($"  libraw: {LibRawNative.GetVersion()}");

                Console.WriteLine("  载荷: 提取/校验嵌入后端（首次约 406 MB，请稍候）...");
                string backend = new PayloadManager().EnsureExtracted(new ConsoleProgress());

                string engineExe = Path.Combine(backend, "resources", TaskQueueRunner.EngineFileName);
                string resources = Path.Combine(backend, "resources");
                if (!File.Exists(engineExe))
                    throw new InvalidOperationException($"载荷缺少直调引擎 {engineExe}");
                if (!Directory.Exists(resources))
                    throw new InvalidOperationException($"载荷缺少 {resources}");
                // EnsureExtracted already hash-verified every manifest entry: it
                // returns a pre-existing extraction only after a full manifest
                // re-hash passes, otherwise re-extracts with streaming SHA-256.
                Console.WriteLine($"  载荷: OK -> {backend}");

                if (nefPath is null)
                {
                    Console.WriteLine("  LibRaw: 跳过 NEF 解码冒烟（未提供文件）");
                }
                else
                {
                    IntPtr handle = LibRawNative.OpenRaw(nefPath, out int error);
                    if (handle == IntPtr.Zero)
                        throw new InvalidOperationException(
                            $"LibRaw 打开失败: {LibRawNative.GetErrorMessage(error)} ({nefPath})");
                    try
                    {
                        int width = LibRawNative.GetWidth(handle);
                        int height = LibRawNative.GetHeight(handle);
                        if (width <= 0 || height <= 0)
                            throw new InvalidOperationException($"LibRaw 无效尺寸 {width}x{height}");
                        Console.WriteLine(
                            $"  LibRaw: OK {LibRawNative.GetMake(handle)} {LibRawNative.GetModel(handle)} {width}x{height}");
                    }
                    finally
                    {
                        LibRawNative.Close(handle);
                    }
                }

                Console.WriteLine("SELFTEST OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SELFTEST FAIL: {ex.Message}");
                return 1;
            }
        }

        /// <summary>One console line per whole percent of extracted raw bytes.</summary>
        private sealed class ConsoleProgress : IProgress<double>
        {
            private int _lastPercent = -1;

            public void Report(double value)
            {
                int percent = (int)(Math.Clamp(value, 0.0, 1.0) * 100);
                if (percent == _lastPercent)
                    return;
                _lastPercent = percent;
                Console.WriteLine($"    提取进度 {percent}%");
            }
        }

        // OutputType=WinExe detaches from the launching console: reattach to the
        // parent console so --selftest progress is visible; redirected stdout
        // (already a valid handle) keeps receiving writes as-is.
        private const uint AttachParentProcess = 0xFFFFFFFF;
        private const int StdOutputHandle = -11;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private static void AttachParentConsole()
        {
            if (GetStdHandle(StdOutputHandle) != IntPtr.Zero)
                return; // already redirected / bound - writes go there
            if (!AttachConsole(AttachParentProcess))
                return; // no parent console (e.g. double-click launch) - stay silent
            var console = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                CreateFileW("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero),
                ownsHandle: true);
            if (console.IsInvalid)
                return;
            Console.SetOut(new StreamWriter(new FileStream(console, FileAccess.Write)) { AutoFlush = true });
        }
    }
}
