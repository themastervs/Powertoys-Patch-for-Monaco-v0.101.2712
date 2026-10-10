using System;
using System.IO;
using System.Globalization;
using System.Windows;

namespace PyPreviewHost2
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length < 2)
                    return 1;

                string filePath = args[0];
                long hwndValue;

                // PowerToys passes the parent HWND as a hexadecimal argument.
                if (!long.TryParse(args[1], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out hwndValue))
                    return 2;

                var app = new Application();
                var host = new PreviewHost(filePath, new IntPtr(hwndValue));
                host.Show();
                app.Run();
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "PyPreviewHost2-error.txt"),
                        ex.ToString());
                }
                catch { }
                return 99;
            }
        }
    }
}
