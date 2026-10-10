using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: AssemblyTitle("PyPreviewHost2")]
[assembly: AssemblyDescription("Universal text, source-code and binary previewer host for the PowerToys preview pane")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: ComVisible(false)]

namespace PyPreviewHost2
{
    /// <summary>
    /// Launch contract of the existing host (unchanged):
    /// args[0] = file path, args[1] = parent HWND as hexadecimal (no 0x prefix).
    /// Exit codes: 1 = fewer than two arguments, 2 = HWND is not valid hexadecimal.
    /// </summary>
    internal sealed class LaunchArguments
    {
        public string FilePath { get; private set; }
        public IntPtr ParentHwnd { get; private set; }

        public static bool TryParse(string[] args, out LaunchArguments result, out int exitCode)
        {
            result = null;
            exitCode = 0;

            if (args == null || args.Length < 2)
            {
                exitCode = 1;
                return false;
            }

            long hwndValue;
            if (!long.TryParse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hwndValue))
            {
                exitCode = 2;
                return false;
            }

            result = new LaunchArguments { FilePath = args[0], ParentHwnd = new IntPtr(hwndValue) };
            return true;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                LaunchArguments launch;
                int exitCode;
                if (!LaunchArguments.TryParse(args, out launch, out exitCode))
                    return exitCode;

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    LogError(e.Exception, true);
                    e.Handled = true;
                };

                using (var host = new PreviewHost(launch.FilePath, launch.ParentHwnd))
                {
                    host.Show();
                    app.Run();
                }
                return 0;
            }
            catch (Exception ex)
            {
                LogError(ex, false);
                return 99;
            }
        }

        internal static void LogError(Exception ex, bool append)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "PyPreviewHost2-error.txt");
                string text = DateTime.Now.ToString("s") + Environment.NewLine + ex + Environment.NewLine;
                if (append)
                    File.AppendAllText(path, text);
                else
                    File.WriteAllText(path, text);
            }
            catch { }
        }
    }
}