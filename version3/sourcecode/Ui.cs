using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using PyPreviewHost2.Core;

namespace PyPreviewHost2.Ui
{
    internal static class Theme
    {
        public static readonly Brush Background = Make("#1E1E1E");
        public static readonly Brush Foreground = Make("#DCDCDC");
        public static readonly Brush LineNumbers = Make("#858585");
        public static readonly Brush Selection = Make("#264F78");
        public static readonly Brush StatusBackground = Make("#252526");
        public static readonly Brush StatusForeground = Make("#B0B0B0");
        public static readonly Brush HexOffset = Make("#858585");
        public static readonly Brush HexByte = Make("#DCDCDC");
        public static readonly Brush HexDim = Make("#5A5A5A");
        public static readonly Brush HexAscii = Make("#B5CEA8");
        public static readonly Brush HexHeader = Make("#569CD6");
        public static readonly Brush ErrorText = Make("#F48771");

        private static Brush Make(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    /// <summary>Slim dark scrollbars (replaces the default white WPF ones) and a dark scroll-viewer corner.</summary>
    internal static class DarkScrollBars
    {
        private const string StyleXaml = @"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       TargetType=""ScrollBar"">
  <Setter Property=""Background"" Value=""#1E1E1E"" />
  <Setter Property=""Width"" Value=""12"" />
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ScrollBar"">
        <Grid Background=""{TemplateBinding Background}"">
          <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0"" Focusable=""False"" IsTabStop=""False"" />
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0"" Focusable=""False"" IsTabStop=""False"" />
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType=""Thumb"">
                    <Border x:Name=""b"" Background=""#424242"" CornerRadius=""3"" Margin=""2"" />
                    <ControlTemplate.Triggers>
                      <Trigger Property=""IsMouseOver"" Value=""True"">
                        <Setter TargetName=""b"" Property=""Background"" Value=""#686868"" />
                      </Trigger>
                      <Trigger Property=""IsDragging"" Value=""True"">
                        <Setter TargetName=""b"" Property=""Background"" Value=""#8A8A8A"" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""Orientation"" Value=""Horizontal"">
            <Setter TargetName=""PART_Track"" Property=""IsDirectionReversed"" Value=""False"" />
            <Setter Property=""Width"" Value=""Auto"" />
            <Setter Property=""Height"" Value=""12"" />
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        public static void Apply(FrameworkElement root)
        {
            try
            {
                // The light square between the vertical and horizontal bars uses SystemColors.ControlBrush.
                root.Resources[SystemColors.ControlBrushKey] = Theme.Background;
                var style = (Style)XamlReader.Parse(StyleXaml);
                root.Resources[typeof(ScrollBar)] = style;
            }
            catch (Exception)
            {
                // Keep the default scrollbars if the template cannot be loaded.
            }
        }
    }
    internal static class ClipboardHelper
    {
        /// <summary>Copies text (persisting after exit) with a few retries; clipboard access can fail transiently.</summary>
        public static bool SetText(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Virtualised hexadecimal viewer. Draws only the visible rows with DrawingContext (no per-byte controls)
    /// and reads blocks on demand from a HexDataSource, so large files are never loaded in full.
    /// </summary>
    internal sealed class HexView : Grid, IDisposable
    {
        private const double PadX = 8;
        private const double PadY = 4;
        private const long MaxCopyBytes = 4L * 1024 * 1024;

        private readonly ScrollBar scroll;
        private HexDataSource source;
        private int bpr;
        private double fontSize;
        private readonly Typeface typeface;
        private double charW = 8, lineH = 16;
        private long length;
        private long selAnchor = -1, selCaret = -1;
        private bool selAscii, dragging;
        private double xOffset, xHex, xAscii, dataY;
        private int offsetDigits = 8;

        public event Action<string> Message;
        public event EventHandler SelectionChanged;

        public HexView(string fontFamily, double fontSize, int bytesPerRow)
        {
            this.fontSize = fontSize;
            bpr = bytesPerRow;
            typeface = new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            Background = Theme.Background;
            ClipToBounds = true;
            Focusable = true;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            scroll = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 0, SmallChange = 1 };
            SetColumn(scroll, 1);
            Children.Add(scroll);
            scroll.ValueChanged += (s, e) => { RefreshLength(); InvalidateVisual(); };

            ComputeMetrics();
        }

        public int BytesPerRow
        {
            get { return bpr; }
            set
            {
                if (value == bpr) return;
                long firstByte = (long)Math.Floor(scroll.Value) * bpr;
                bpr = value;
                ComputeLayout();
                UpdateScroll();
                scroll.Value = Math.Min(scroll.Maximum, firstByte / bpr);
                InvalidateVisual();
            }
        }

        public double FontSizeValue
        {
            get { return fontSize; }
            set
            {
                fontSize = value;
                ComputeMetrics();
                UpdateScroll();
            }
        }

        public bool HasSelection { get { return selAnchor >= 0; } }

        public void SetSource(HexDataSource newSource)
        {
            source = newSource;
            selAnchor = selCaret = -1;
            length = 0;
            RefreshLength();
            ComputeMetrics();
            scroll.Value = 0;
            UpdateScroll();
            RaiseSelection();
        }

        public string SelectionSummary()
        {
            if (selAnchor < 0) return null;
            long lo = Math.Min(selAnchor, selCaret), hi = Math.Max(selAnchor, selCaret);
            return "Offset 0x" + HexFormatter.FormatOffset(lo, offsetDigits) + " (" + lo.ToString("N0", CultureInfo.InvariantCulture)
                + "), " + (hi - lo + 1).ToString("N0", CultureInfo.InvariantCulture) + " byte(s) selected";
        }

        // ---------- layout ----------

        private double Dpi { get { return VisualTreeHelper.GetDpi(this).PixelsPerDip; } }

        private FormattedText Ft(string text, Brush brush)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, fontSize, brush, Dpi);
        }

        private void ComputeMetrics()
        {
            FormattedText ft = Ft("0000000000", Brushes.White);
            charW = ft.WidthIncludingTrailingWhitespace / 10.0;
            lineH = Math.Ceiling(ft.Height) + 1;
            ComputeLayout();
        }

        private void ComputeLayout()
        {
            offsetDigits = HexFormatter.OffsetDigits(length);
            xOffset = PadX;
            xHex = xOffset + (offsetDigits + 2) * charW;
            xAscii = xHex + (HexFormatter.HexWidthChars(bpr) + 1) * charW;
            dataY = PadY + lineH + 4;
        }

        private void RefreshLength()
        {
            if (source == null) return;
            try
            {
                long l = source.Length;
                if (l != length)
                {
                    length = l;
                    ComputeLayout();
                    UpdateScroll();
                }
            }
            catch (Exception) { }
        }

        private void UpdateScroll()
        {
            double h = ActualHeight;
            int pageRows = Math.Max(1, (int)Math.Floor((h - dataY) / lineH));
            long totalRows = (length + bpr - 1) / bpr;
            double max = Math.Max(0, totalRows - pageRows);
            scroll.Maximum = max;
            scroll.ViewportSize = pageRows;
            scroll.LargeChange = Math.Max(1, pageRows - 1);
            if (scroll.Value > max) scroll.Value = max;
            scroll.IsEnabled = max > 0;
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateScroll();
        }

        // ---------- rendering ----------

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double viewW = Math.Max(0, ActualWidth - scroll.ActualWidth);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, viewW, ActualHeight)));
            try { Draw(dc, viewW); }
            finally { dc.Pop(); }
        }

        private void Draw(DrawingContext dc, double viewW)
        {
            if (source == null) return;

            dc.DrawText(Ft("Offset", Theme.HexHeader), new Point(xOffset, PadY));
            byte[] idx = new byte[bpr];
            for (int i = 0; i < bpr; i++) idx[i] = (byte)i;
            dc.DrawText(Ft(HexFormatter.FormatHexColumn(idx, 0, bpr, bpr), Theme.HexHeader), new Point(xHex, PadY));
            dc.DrawText(Ft("ASCII (byte representation, not decoded text)", Theme.HexHeader), new Point(xAscii, PadY));
            dc.DrawRectangle(Theme.HexDim, null, new Rect(0, dataY - 3, viewW, 1));

            if (length == 0)
            {
                dc.DrawText(Ft("(empty file - 0 bytes)", Theme.HexOffset), new Point(xOffset, dataY));
                return;
            }

            long first = (long)Math.Floor(scroll.Value);
            long totalRows = (length + bpr - 1) / bpr;
            int rows = (int)Math.Ceiling((ActualHeight - dataY) / lineH);
            if (first + rows > totalRows) rows = (int)(totalRows - first);
            if (rows <= 0) return;

            byte[] buf = new byte[rows * bpr];
            int got;
            try { got = source.Read(first * bpr, buf, buf.Length); }
            catch (Exception ex)
            {
                dc.DrawText(Ft("Read error: " + ex.Message, Theme.ErrorText), new Point(xOffset, dataY));
                return;
            }

            long selLo = -1, selHi = -1;
            if (selAnchor >= 0)
            {
                selLo = Math.Min(selAnchor, selCaret);
                selHi = Math.Max(selAnchor, selCaret);
            }

            for (int r = 0; r < rows; r++)
            {
                int n = Math.Min(bpr, got - r * bpr);
                if (n <= 0)
                {
                    dc.DrawText(Ft("(data unavailable: the file may have changed)", Theme.ErrorText), new Point(xOffset, dataY + r * lineH));
                    break;
                }
                long rowOff = (first + r) * bpr;
                double y = dataY + r * lineH;

                if (selLo >= 0 && rowOff + n - 1 >= selLo && rowOff <= selHi)
                {
                    int a = (int)Math.Max(0, selLo - rowOff);
                    int b = (int)Math.Min(n - 1, selHi - rowOff);
                    dc.DrawRectangle(Theme.Selection, null,
                        new Rect(xHex + HexFormatter.HexColumn(a) * charW, y,
                                 (HexFormatter.HexColumn(b) + 2 - HexFormatter.HexColumn(a)) * charW, lineH));
                    dc.DrawRectangle(Theme.Selection, null, new Rect(xAscii + a * charW, y, (b - a + 1) * charW, lineH));
                }

                dc.DrawText(Ft(HexFormatter.FormatOffset(rowOff, offsetDigits), Theme.HexOffset), new Point(xOffset, y));

                FormattedText hex = Ft(HexFormatter.FormatHexColumn(buf, r * bpr, n, bpr), Theme.HexByte);
                FormattedText asc = Ft(HexFormatter.FormatAscii(buf, r * bpr, n), Theme.HexAscii);
                for (int i = 0; i < n; i++)
                {
                    byte v = buf[r * bpr + i];
                    if (v == 0) hex.SetForegroundBrush(Theme.HexDim, HexFormatter.HexColumn(i), 2);
                    if (v < 0x20 || v > 0x7E) asc.SetForegroundBrush(Theme.HexDim, i, 1);
                }
                dc.DrawText(hex, new Point(xHex, y));
                dc.DrawText(asc, new Point(xAscii, y));
            }
        }

        // ---------- mouse and keyboard ----------

        private bool HitTestByte(Point p, out long index, out bool inAscii)
        {
            index = -1;
            inAscii = false;
            if (source == null || length == 0) return false;

            long first = (long)Math.Floor(scroll.Value);
            long row = first + (long)Math.Floor((p.Y - dataY) / lineH);
            long maxRow = (length - 1) / bpr;
            if (row < 0) row = 0;
            if (row > maxRow) row = maxRow;

            int col = 0;
            if (p.X >= xAscii - charW * 0.5)
            {
                inAscii = true;
                col = (int)Math.Floor((p.X - xAscii) / charW);
            }
            else
            {
                double cc = (p.X - xHex) / charW;
                for (int i = 0; i < bpr; i++)
                    if (cc >= HexFormatter.HexColumn(i)) col = i;
            }
            if (col < 0) col = 0;
            if (col > bpr - 1) col = bpr - 1;

            long idx = row * bpr + col;
            if (idx >= length) idx = length - 1;
            index = idx;
            return true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            RefreshLength();
            Point p = e.GetPosition(this);
            long idx;
            bool asc;
            if (HitTestByte(p, out idx, out asc))
            {
                selAnchor = selCaret = idx;
                selAscii = asc;
                dragging = true;
                CaptureMouse();
                InvalidateVisual();
                RaiseSelection();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Point p = e.GetPosition(this);
            if (p.Y < dataY && scroll.Value > 0) scroll.Value = Math.Max(0, scroll.Value - 1);
            else if (p.Y > ActualHeight) scroll.Value = Math.Min(scroll.Maximum, scroll.Value + 1);

            long idx;
            bool asc;
            if (HitTestByte(p, out idx, out asc))
            {
                selCaret = idx;
                InvalidateVisual();
                RaiseSelection();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (dragging)
            {
                dragging = false;
                ReleaseMouseCapture();
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll.Value = Math.Max(0, Math.Min(scroll.Maximum, scroll.Value - e.Delta / 120.0 * 3));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.C: if (ctrl) { CopyDefault(); e.Handled = true; } break;
                case Key.A: if (ctrl) { SelectAll(); e.Handled = true; } break;
                case Key.Up: scroll.Value = Math.Max(0, scroll.Value - 1); e.Handled = true; break;
                case Key.Down: scroll.Value = Math.Min(scroll.Maximum, scroll.Value + 1); e.Handled = true; break;
                case Key.PageUp: scroll.Value = Math.Max(0, scroll.Value - scroll.LargeChange); e.Handled = true; break;
                case Key.PageDown: scroll.Value = Math.Min(scroll.Maximum, scroll.Value + scroll.LargeChange); e.Handled = true; break;
                case Key.Home: if (ctrl) { scroll.Value = 0; e.Handled = true; } break;
                case Key.End: if (ctrl) { scroll.Value = scroll.Maximum; e.Handled = true; } break;
            }
        }

        private void RaiseSelection()
        {
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        private void Notify(string text)
        {
            if (Message != null) Message(text);
        }

        // ---------- selection and copying ----------

        public void SelectAll()
        {
            if (length == 0) return;
            selAnchor = 0;
            selCaret = length - 1;
            InvalidateVisual();
            RaiseSelection();
        }

        private byte[] ReadSelection(out long start)
        {
            start = 0;
            if (selAnchor < 0 || source == null) { Notify("Nothing selected."); return null; }
            long lo = Math.Min(selAnchor, selCaret), hi = Math.Max(selAnchor, selCaret);
            long count = hi - lo + 1;
            if (count > MaxCopyBytes) { Notify("Selection too large to copy (limit 4 MiB)."); return null; }
            byte[] b = new byte[count];
            int got;
            try { got = source.Read(lo, b, b.Length); }
            catch (Exception ex) { Notify("Read error: " + ex.Message); return null; }
            if (got < b.Length) Array.Resize(ref b, got);
            start = lo;
            return b;
        }

        private void Copy(string text, string what)
        {
            Notify(ClipboardHelper.SetText(text) ? what + " copied." : "Could not access the clipboard.");
        }

        public void CopyDefault()
        {
            if (selAscii) CopyAscii(); else CopyHex();
        }

        public void CopyHex()
        {
            long s;
            byte[] b = ReadSelection(out s);
            if (b != null) Copy(HexFormatter.FormatBytesHex(b, 0, b.Length), "Hex bytes");
        }

        public void CopyAscii()
        {
            long s;
            byte[] b = ReadSelection(out s);
            if (b != null) Copy(HexFormatter.FormatAscii(b, 0, b.Length), "ASCII representation");
        }

        public void CopyDump()
        {
            long s;
            byte[] b = ReadSelection(out s);
            if (b == null) return;
            var sb = new StringBuilder();
            for (int pos = 0; pos < b.Length; pos += bpr)
            {
                int n = Math.Min(bpr, b.Length - pos);
                if (pos > 0) sb.Append("\r\n");
                sb.Append(HexFormatter.FormatDumpLine(s + pos, b, pos, n, bpr, offsetDigits));
            }
            Copy(sb.ToString(), "Hex dump");
        }

        public void CopyOffset()
        {
            if (selAnchor < 0) { Notify("Nothing selected."); return; }
            long lo = Math.Min(selAnchor, selCaret);
            Copy("0x" + HexFormatter.FormatOffset(lo, offsetDigits), "Offset");
        }

        public void GoToStart() { scroll.Value = 0; }
        public void GoToEnd() { scroll.Value = scroll.Maximum; }

        public void Dispose()
        {
            source = null;
        }
    }

    /// <summary>
    /// The complete viewer: text/hex content area plus a restrained status bar.
    /// Reading runs on a background thread; results are applied on the UI thread.
    /// </summary>
    internal sealed class PreviewView : Grid, IDisposable
    {
        private readonly AppSettings settings;
        private readonly PreviewService service = new PreviewService();
        private readonly TextEditor editor;
        private readonly HexView hexView;
        private readonly TextBox errorBox;
        private readonly Border host;
        private readonly TextBlock status;

        private PreviewResult result;
        private PreviewResult textShownFor;
        private HexDataSource hexSource;
        private bool hexMode;
        private bool showingError;
        private bool loading = true;
        private string highlightNote;
        private string transient;

        public PreviewView(AppSettings settings)
        {
            this.settings = settings;
            DarkScrollBars.Apply(this);
            Background = Theme.Background;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            host = new Border { Background = Theme.Background };
            SetRow(host, 0);
            Children.Add(host);

            status = new TextBlock
            {
                Foreground = Theme.StatusForeground,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var statusBar = new Border
            {
                Background = Theme.StatusBackground,
                Padding = new Thickness(8, 3, 8, 3),
                Child = status
            };
            SetRow(statusBar, 1);
            Children.Add(statusBar);

            editor = new TextEditor
            {
                IsReadOnly = true,
                ShowLineNumbers = settings.LineNumbers,
                WordWrap = settings.WordWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Theme.Background,
                Foreground = Theme.Foreground,
                FontFamily = new FontFamily(settings.FontFamilyName),
                FontSize = settings.FontSize,
                Padding = new Thickness(8)
            };
            editor.LineNumbersForeground = Theme.LineNumbers;
            editor.TextArea.SelectionBrush = Theme.Selection;
            editor.TextArea.SelectionBorder = null;
            editor.TextArea.SelectionCornerRadius = 0;
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.Options.CutCopyWholeLine = false;
            editor.Options.HighlightCurrentLine = false;
            // Copy the exact document text of the selection (no newline normalisation).
            editor.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(OnEditorPreviewExecuted));

            hexView = new HexView(settings.FontFamilyName, settings.FontSize, settings.BytesPerRow);
            hexView.Message += m => { transient = m; UpdateStatus(); };
            hexView.SelectionChanged += (s, e) => { transient = hexView.SelectionSummary(); UpdateStatus(); };

            errorBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Theme.Background,
                Foreground = Theme.ErrorText,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12),
                FontFamily = new FontFamily(settings.FontFamilyName),
                FontSize = settings.FontSize,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            ContextMenu = new ContextMenu();
            ContextMenuOpening += (s, e) => BuildMenu();
            PreviewKeyDown += OnPreviewKeyDown;

            UpdateStatus();
        }

        // ---------- loading ----------

        public void LoadFileAsync(string path, CancellationToken ct)
        {
            loading = true;
            UpdateStatus();
            System.Windows.Threading.Dispatcher dispatcher = Dispatcher;
            Task.Run(() =>
            {
                PreviewResult r;
                try { r = service.Load(path, settings, ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { r = PreviewResult.Failure(path, "Unexpected error: " + ex.Message); }
                try
                {
                    dispatcher.BeginInvoke(new Action(() => { if (!ct.IsCancellationRequested) Apply(r); }));
                }
                catch (Exception) { }
            });
        }

        private void Apply(PreviewResult r)
        {
            loading = false;
            result = r;
            try
            {
                if (r.IsError)
                {
                    ShowError(r.ErrorMessage);
                }
                else
                {
                    hexMode = r.InitialView == ViewMode.Hex || r.Text == null;
                    Render();
                }
            }
            catch (Exception ex)
            {
                ShowError("Display error: " + ex.Message);
            }
            UpdateStatus();
        }

        private void Render()
        {
            showingError = false;
            if (hexMode && ShowHex()) { UpdateStatus(); return; }
            if (result.Text != null) ShowText();
            UpdateStatus();
        }

        private void SetContent(UIElement element)
        {
            if (!ReferenceEquals(host.Child, element)) host.Child = element;
        }

        private void ShowText()
        {
            hexMode = false;
            if (!ReferenceEquals(textShownFor, result))
            {
                editor.Text = result.Text ?? string.Empty;
                IHighlightingDefinition def = null;
                highlightNote = null;
                if (!result.HighlightingDisabled && result.LanguageId != null)
                {
                    string error;
                    if (!SyntaxHighlighter.TryGet(result.LanguageId, out def, out error))
                    {
                        def = null;
                        highlightNote = "Highlighting unavailable: " + error;
                    }
                }
                editor.SyntaxHighlighting = def;
                textShownFor = result;
                editor.ScrollToHome();
            }
            SetContent(editor);
        }

        private bool ShowHex()
        {
            try
            {
                if (hexSource == null)
                {
                    hexSource = HexDataSource.Open(result.FilePath);
                    hexView.SetSource(hexSource);
                }
                hexMode = true;
                SetContent(hexView);
                return true;
            }
            catch (Exception ex)
            {
                transient = "Cannot open the file for the hex view: " + ex.Message;
                hexMode = false;
                return false;
            }
        }

        private void ShowError(string message)
        {
            showingError = true;
            errorBox.Text = message;
            SetContent(errorBox);
        }

        // ---------- status bar ----------

        private void UpdateStatus()
        {
            string text;
            if (loading) text = "Loading...";
            else if (result == null) text = "";
            else if (showingError || result.IsError) text = "Error - " + FirstLine(result.ErrorMessage ?? "unknown");
            else
            {
                var parts = new List<string>();
                parts.Add(string.IsNullOrEmpty(result.Extension) ? result.FileName : result.Extension);
                parts.Add(hexMode ? "Hex view" : result.ReaderName + (result.FormatName != null ? " - " + result.FormatName : ""));
                parts.Add(TextStats.FormatSize(result.FileSize));
                if (!hexMode && result.EncodingName != null)
                    parts.Add(result.EncodingName + (result.EncodingIsGuess ? " [guess]" : ""));
                if (!hexMode && result.Category != ReaderCategory.SpecializedBinary)
                {
                    if (result.LineEndings != null) parts.Add(result.LineEndings);
                    parts.Add(editor.LineCount + (result.IsPartial ? "+" : "") + " lines");
                }
                if (hexMode) parts.Add("offsets in hex; ASCII column = byte representation, not decoded text");
                if (result.IsPartial) parts.Add("PARTIAL");
                if (result.ContentLooksBinary) parts.Add("BINARY");
                if (result.Category == ReaderCategory.Unknown) parts.Add("UNKNOWN FORMAT");
                if (!string.IsNullOrEmpty(transient)) parts.Add(transient);
                else if (!string.IsNullOrEmpty(result.Notice)) parts.Add(result.Notice);
                if (!hexMode && highlightNote != null) parts.Add(highlightNote);
                text = string.Join("  |  ", parts);
            }
            status.Text = text;
            status.ToolTip = text;
        }

        private static string FirstLine(string s)
        {
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        // ---------- commands ----------

        private void OnEditorPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command == ApplicationCommands.Copy)
            {
                e.Handled = true;
                CopyEditorSelection();
            }
        }

        private void CopyEditorSelection()
        {
            if (editor.TextArea.Selection.IsEmpty) return;
            string text = editor.TextArea.Selection.GetText();
            transient = ClipboardHelper.SetText(text) ? "Copied." : "Could not access the clipboard.";
            UpdateStatus();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            switch (e.Key)
            {
                case Key.H: ToggleHex(); e.Handled = true; break;
                case Key.W: ToggleWrap(); e.Handled = true; break;
                case Key.L: ToggleLineNumbers(); e.Handled = true; break;
                case Key.OemPlus:
                case Key.Add: SetFont(settings.FontSize + 1); e.Handled = true; break;
                case Key.OemMinus:
                case Key.Subtract: SetFont(settings.FontSize - 1); e.Handled = true; break;
                case Key.D0:
                case Key.NumPad0: SetFont(14); e.Handled = true; break;
            }
        }

        private bool CanSwitchView()
        {
            return result != null && !result.IsError && !showingError;
        }

        private void ToggleHex()
        {
            if (!CanSwitchView()) return;
            transient = null;
            if (!hexMode) { if (!ShowHex()) { UpdateStatus(); return; } }
            else if (result.Text != null) hexMode = false;
            else return;
            Render();
        }

        private void ToggleWrap()
        {
            settings.WordWrap = !settings.WordWrap;
            editor.WordWrap = settings.WordWrap;
            settings.Save();
        }

        private void ToggleLineNumbers()
        {
            settings.LineNumbers = !settings.LineNumbers;
            editor.ShowLineNumbers = settings.LineNumbers;
            settings.Save();
        }

        private void SetFont(double size)
        {
            size = Math.Max(6, Math.Min(48, size));
            settings.FontSize = size;
            editor.FontSize = size;
            hexView.FontSizeValue = size;
            errorBox.FontSize = size;
            settings.Save();
        }

        private void SetBytesPerRow(int n)
        {
            settings.BytesPerRow = n;
            hexView.BytesPerRow = n;
            settings.Save();
        }

        // ---------- context menu ----------

        private static MenuItem Item(string header, Action action, bool checkedState = false, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = checkedState };
            if (action != null) mi.Click += (s, e) => action();
            return mi;
        }

        private void BuildMenu()
        {
            ContextMenu menu = ContextMenu;
            menu.Items.Clear();
            if (result == null || result.IsError || showingError) { menu.Items.Add(Item("No content", null, false, false)); return; }

            if (hexMode)
            {
                bool sel = hexView.HasSelection;
                menu.Items.Add(Item("Copy hex bytes (Ctrl+C)", hexView.CopyHex, false, sel));
                menu.Items.Add(Item("Copy ASCII representation", hexView.CopyAscii, false, sel));
                menu.Items.Add(Item("Copy as hex dump lines", hexView.CopyDump, false, sel));
                menu.Items.Add(Item("Copy start offset", hexView.CopyOffset, false, sel));
                menu.Items.Add(Item("Select all (Ctrl+A)", hexView.SelectAll));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Go to start", hexView.GoToStart));
                menu.Items.Add(Item("Go to end", hexView.GoToEnd));
                var bprMenu = new MenuItem { Header = "Bytes per row" };
                foreach (int n in new[] { 8, 16, 24, 32 })
                {
                    int captured = n;
                    bprMenu.Items.Add(Item(n.ToString(), () => SetBytesPerRow(captured), settings.BytesPerRow == n));
                }
                menu.Items.Add(bprMenu);
            }
            else
            {
                menu.Items.Add(Item("Copy (Ctrl+C)", CopyEditorSelection, false, !editor.TextArea.Selection.IsEmpty));
                menu.Items.Add(Item("Select all (Ctrl+A)", () => editor.SelectAll()));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Word wrap (Ctrl+W)", ToggleWrap, settings.WordWrap));
                menu.Items.Add(Item("Line numbers (Ctrl+L)", ToggleLineNumbers, settings.LineNumbers));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Larger font (Ctrl++)", () => SetFont(settings.FontSize + 1)));
            menu.Items.Add(Item("Smaller font (Ctrl+-)", () => SetFont(settings.FontSize - 1)));
            menu.Items.Add(Item("Reset font size (Ctrl+0)", () => SetFont(14)));
            menu.Items.Add(new Separator());
            if (hexMode)
                menu.Items.Add(Item(result.Text != null ? "View as text (Ctrl+H)" : "View as text (not available for this file)",
                    ToggleHex, false, result.Text != null));
            else
                menu.Items.Add(Item("View raw bytes as hex (Ctrl+H)", ToggleHex));
        }

        public void Dispose()
        {
            hexView.Dispose();
            if (hexSource != null)
            {
                hexSource.Dispose();
                hexSource = null;
            }
        }
    }
}