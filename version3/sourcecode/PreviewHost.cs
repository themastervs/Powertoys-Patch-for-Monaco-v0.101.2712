using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PyPreviewHost2.Core;
using PyPreviewHost2.Integration;
using PyPreviewHost2.Ui;

namespace PyPreviewHost2
{
    /// <summary>
    /// PowerToys integration layer: embeds the WPF viewer into the parent HWND.
    /// Contains no file-reading or rendering logic.
    /// </summary>
    internal sealed class PreviewHost : IDisposable
    {
        private readonly string filePath;
        private readonly IntPtr parentHwnd;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private HwndSource source;
        private PreviewView view;
        private ParentResizeTracker tracker;
        private bool disposed;

        public PreviewHost(string filePath, IntPtr parentHwnd)
        {
            this.filePath = filePath;
            this.parentHwnd = parentHwnd;
        }

        public void Show()
        {
            AppSettings settings = AppSettings.Load();
            view = new PreviewView(settings);

            // Unchanged from the original host: WS_CHILD | WS_VISIBLE, 1000x700 initial size.
            var parameters = new HwndSourceParameters("PyPreviewHost2")
            {
                ParentWindow = parentHwnd,
                WindowStyle = unchecked((int)0x40000000) | unchecked((int)0x10000000),
                Width = 1000,
                Height = 700
            };

            source = new HwndSource(parameters);
            source.RootVisual = view;
            source.Disposed += OnSourceDisposed;

            if (settings.AutoResizeToParent)
            {
                tracker = new ParentResizeTracker(parentHwnd, source.Handle, RequestShutdown);
                tracker.Start();
            }

            view.LoadFileAsync(filePath, cts.Token);
        }

        private void OnSourceDisposed(object sender, EventArgs e)
        {
            RequestShutdown();
        }

        private void RequestShutdown()
        {
            Application app = Application.Current;
            if (app != null)
                app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { cts.Cancel(); } catch { }
            if (tracker != null) tracker.Stop();
            if (view != null) view.Dispose();
            if (source != null)
            {
                source.Disposed -= OnSourceDisposed;
                source.Dispose();
            }
            cts.Dispose();
        }
    }
}

namespace PyPreviewHost2.Integration
{
    internal static class NativeMethods
    {
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);
    }

    /// <summary>
    /// Keeps the embedded child window the same size as the parent's client area and
    /// reports when the parent window no longer exists. Polling is used because the
    /// original contract gives the host no resize notification.
    /// </summary>
    internal sealed class ParentResizeTracker
    {
        private readonly IntPtr parent;
        private readonly IntPtr child;
        private readonly Action parentGone;
        private readonly DispatcherTimer timer;
        private int lastWidth = -1;
        private int lastHeight = -1;

        public ParentResizeTracker(IntPtr parent, IntPtr child, Action parentGone)
        {
            this.parent = parent;
            this.child = child;
            this.parentGone = parentGone;
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += (s, e) => Tick();
        }

        public void Start()
        {
            Tick();
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
        }

        private void Tick()
        {
            if (!NativeMethods.IsWindow(parent))
            {
                timer.Stop();
                if (parentGone != null) parentGone();
                return;
            }

            NativeMethods.RECT r;
            if (!NativeMethods.GetClientRect(parent, out r)) return;

            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return;
            if (w == lastWidth && h == lastHeight) return;

            lastWidth = w;
            lastHeight = h;
            NativeMethods.SetWindowPos(child, IntPtr.Zero, 0, 0, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }
}