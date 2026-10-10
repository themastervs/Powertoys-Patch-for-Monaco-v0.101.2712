using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace PyPreviewHost2
{
    internal sealed class PreviewHost
    {
        private readonly string filePath;
        private readonly IntPtr parentHwnd;
        private HwndSource source;

        public PreviewHost(string filePath, IntPtr parentHwnd)
        {
            this.filePath = filePath;
            this.parentHwnd = parentHwnd;
        }

        public void Show()
        {
            var editor = new TextEditor
            {
                IsReadOnly = true,
                ShowLineNumbers = false,
                WordWrap = false,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 14,
                Padding = new Thickness(8)
            };

            if (File.Exists(filePath))
            {
                editor.Text = File.ReadAllText(filePath);
                editor.SyntaxHighlighting = SyntaxHighlighter.ForFile(filePath);
            }
            else
            {
                editor.Text = "Archivo no encontrado: " + filePath;
            }

            var parameters = new HwndSourceParameters("PyPreviewHost2")
            {
                ParentWindow = parentHwnd,
                WindowStyle = unchecked((int)0x40000000) | unchecked((int)0x10000000),
                Width = 1000,
                Height = 700
            };

            source = new HwndSource(parameters);
            source.RootVisual = editor;
        }
    }
}
