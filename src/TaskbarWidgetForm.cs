using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal sealed class TaskbarWidgetForm : Form
    {
        private const int SingleBaseWidth = 97;
        private const int DualBaseWidth = 122;
        private const int BaseHeight = 38;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        private int _fiveHourRemainingPercent;
        private int _fiveHourTimeRemainingPercent = -1;
        private int _weeklyRemainingPercent;
        private DateTimeOffset? _fiveHourResetsAt;
        private bool _hasWeeklyWindow;
        private bool _active;
        private float _dpiScale = 1f;
        private AppSettings _settings = new AppSettings();
        private ThemePalette _palette;
        private bool _hovered;
        private bool _positionInitialized;
        private bool _repositioning;
        private bool _mouseDown;
        private bool _dragging;
        private bool _suppressClick;
        private Point _dragStartCursor;
        private Point _dragStartLocation;
        private Point _lastDragCursor;

        public TaskbarWidgetForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            _palette = ThemePalette.FromSettings(_settings);
            ClientSize = new Size(SingleBaseWidth, BaseHeight);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = _palette.Background;
            Cursor = Cursors.Hand;
            SetRoundedRegion();
        }

        public event EventHandler PositionChanged;

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }

        public void ApplyResult(QuotaReadResult result)
        {
            _active = result != null && result.IsCodexRunning && result.Snapshot != null;
            var primary = result == null || result.Snapshot == null ? null : result.Snapshot.FiveHourWindow;
            var weekly = result == null || result.Snapshot == null ? null : result.Snapshot.DisplayWeeklyWindow;
            _fiveHourRemainingPercent = primary == null ? 0 : primary.RemainingPercent;
            _fiveHourTimeRemainingPercent = primary == null ? -1 : primary.TimeRemainingPercent;
            _weeklyRemainingPercent = weekly == null ? 0 : weekly.RemainingPercent;
            _fiveHourResetsAt = primary == null ? null : primary.ResetsAt;
            _hasWeeklyWindow = weekly != null && weekly != primary;
            ApplyScaledSize();
            Reposition();
            Invalidate();
        }

        public void ApplySettings(AppSettings settings)
        {
            if (!ReferenceEquals(_settings, settings) || settings == null || !settings.WidgetPositionSaved)
                _positionInitialized = false;
            _settings = settings ?? new AppSettings();
            _palette = ThemePalette.FromSettings(_settings);
            BackColor = _palette.Background;
            ApplyScaledSize();
            Reposition();
            Invalidate();
        }

        public void SetVisible(bool visible)
        {
            if (visible)
            {
                if (!Visible) Show();
                EnsureOnTop();
            }
            else
            {
                FinishDrag();
                Hide();
            }
        }

        public void EnsureOnTop()
        {
            if (!Visible) Show();
            Reposition(true);
        }

        // Retained for the existing preview tool; refreshes must never dock the widget.
        public void RefreshDynamicAnchor()
        {
            Reposition();
        }

        public Rectangle GetAnchorBounds()
        {
            return Bounds;
        }

        public void ResetPosition()
        {
            FinishDrag();
            _settings.WidgetPositionSaved = false;
            _positionInitialized = false;
            Reposition(true);
        }

        public void Reposition(bool forceZOrder = false)
        {
            if (!IsHandleCreated || IsDisposed || _mouseDown || _repositioning) return;
            _repositioning = true;
            try
            {
                var location = Location;
                if (!_positionInitialized)
                {
                    if (_settings.WidgetPositionSaved)
                        location = new Point(_settings.WidgetX, _settings.WidgetY);
                    else
                    {
                        var work = Screen.PrimaryScreen.WorkingArea;
                        var margin = (int)Math.Round(12 * _dpiScale);
                        location = new Point(work.Right - Width - margin, work.Bottom - Height - margin);
                    }
                    _positionInitialized = true;
                }

                // Choose an actual monitor, not the virtual-screen bounding box (which can contain gaps).
                var area = Screen.FromRectangle(new Rectangle(location, Size)).WorkingArea;
                location = ConstrainToWorkArea(location, Size, area);
                if (Location != location) Location = location;
                if (forceZOrder)
                    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
                RememberPosition();
            }
            finally { _repositioning = false; }
        }

        internal static Point ConstrainToWorkArea(Point location, Size size, Rectangle area)
        {
            return new Point(
                Math.Max(area.Left, Math.Min(location.X, area.Right - size.Width)),
                Math.Max(area.Top, Math.Min(location.Y, area.Bottom - size.Height)));
        }

        // Legacy preview calculation; no longer used to position the application window.
        internal static int CalculateLeadingPlacement(int taskbarStart, int taskbarEnd, int widgetExtent, int leadingEdge, int gap)
        {
            return Math.Max(taskbarStart + 4, Math.Min(leadingEdge - widgetExtent - gap, taskbarEnd - widgetExtent - 4));
        }

        private void RememberPosition()
        {
            if (_settings.WidgetPositionSaved && _settings.WidgetX == Left && _settings.WidgetY == Top) return;
            _settings.WidgetPositionSaved = true;
            _settings.WidgetX = Left;
            _settings.WidgetY = Top;
            var handler = PositionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _suppressClick = e.Button != MouseButtons.Left;
            if (e.Button == MouseButtons.Left)
            {
                _dragStartCursor = PointToScreen(e.Location);
                _lastDragCursor = _dragStartCursor;
                _dragStartLocation = Location;
                _mouseDown = true;
                _dragging = false;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseDown || (e.Button & MouseButtons.Left) == 0) return;
            var cursor = PointToScreen(e.Location);
            var threshold = SystemInformation.DragSize;
            var clickArea = new Rectangle(_dragStartCursor.X - threshold.Width / 2,
                _dragStartCursor.Y - threshold.Height / 2, threshold.Width, threshold.Height);
            if (!_dragging && clickArea.Contains(cursor)) return;
            _dragging = true;
            _suppressClick = true;
            Cursor = Cursors.SizeAll;
            _lastDragCursor = cursor;
            // Do not clamp during a gesture: the cursor can cross monitor edges and gaps freely.
            Location = new Point(_dragStartLocation.X + cursor.X - _dragStartCursor.X,
                _dragStartLocation.Y + cursor.Y - _dragStartCursor.Y);
        }

        protected override void OnClick(EventArgs e)
        {
            // WinForms raises Click before MouseUp. Keep suppression until the next press.
            if (!_suppressClick) base.OnClick(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) FinishDrag();
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) FinishDrag();
        }

        private void FinishDrag()
        {
            if (!_mouseDown) return;
            var moved = _dragging;
            _mouseDown = false;
            _dragging = false;
            Capture = false;
            Cursor = Cursors.Hand;
            if (moved) Reposition();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetRoundedRegion();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _dpiScale = GraphicsExtensions.GetDpiScale(Handle);
            ApplyScaledSize();
            Reposition();
        }

        protected override void WndProc(ref Message message)
        {
            const int DpiChanged = 0x02E0;
            const int DisplayChange = 0x007E;
            const int SettingChange = 0x001A;
            if (message.Msg == DpiChanged)
            {
                var newDpi = message.WParam.ToInt64() & 0xFFFF;
                var suggested = (NativeRect)Marshal.PtrToStructure(message.LParam, typeof(NativeRect));
                _dpiScale = Math.Max(1f, newDpi / 96f);
                var size = GetScaledSize();
                SetWindowPos(Handle, IntPtr.Zero, suggested.Left, suggested.Top, size.Width, size.Height, 0x0010 | 0x0004);
                if (_mouseDown)
                {
                    // Rebase the next drag delta after Windows changes the physical-pixel geometry.
                    _dragStartLocation = Location;
                    _dragStartCursor = _lastDragCursor;
                }
                BeginInvoke(new Action(delegate { Reposition(); }));
                Invalidate();
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            if ((message.Msg == DisplayChange || message.Msg == SettingChange) && IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(delegate { Reposition(); }));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var scale = GetDrawingScale();
            graphics.ScaleTransform(scale, scale);

            var accent = _active ? _palette.QuotaColor(_fiveHourRemainingPercent) : _palette.Faint;
            var weeklyAccent = _active ? _palette.QuotaColor(_weeklyRemainingPercent) : _palette.Faint;
            var baseWidth = GetBaseWidth();
            var backgroundColor = _hovered ? Blend(_palette.Background, _palette.Primary, _palette.IsLight ? 0.06f : 0.09f) : _palette.Background;
            var borderColor = _hovered ? Blend(_palette.Divider, _palette.Primary, 0.45f) : _palette.Divider;
            using (var background = new SolidBrush(backgroundColor))
            using (var border = new Pen(borderColor))
            {
                graphics.FillRoundedRectangle(background, new Rectangle(0, 0, baseWidth - 1, BaseHeight - 1), 8);
                graphics.DrawRoundedRectangle(border, new Rectangle(0, 0, baseWidth - 1, BaseHeight - 1), 8);
            }

            using (var trackPen = new Pen(_palette.Track, 3f))
            using (var fillPen = new Pen(accent, 3f))
            {
                trackPen.StartCap = trackPen.EndCap = LineCap.Round;
                fillPen.StartCap = fillPen.EndCap = LineCap.Round;
                var ring = new RectangleF(9, 8, 25, 25);
                graphics.DrawArc(trackPen, ring, -90, 359.9f);
                if (_active && _fiveHourRemainingPercent > 0)
                {
                    graphics.DrawArc(fillPen, ring, -90, 360f * _fiveHourRemainingPercent / 100f);
                }
            }

            if (_active && _fiveHourTimeRemainingPercent >= 0)
            {
                using (var trackPen = new Pen(_palette.Faint, 1.6f))
                using (var fillPen = new Pen(_palette.Secondary, 1.6f))
                {
                    trackPen.StartCap = trackPen.EndCap = LineCap.Round;
                    fillPen.StartCap = fillPen.EndCap = LineCap.Round;
                    var ring = new RectangleF(13, 12, 17, 17);
                    graphics.DrawArc(trackPen, ring, -90, 359.9f);
                    if (_fiveHourTimeRemainingPercent > 0)
                        graphics.DrawArc(fillPen, ring, -90, 360f * _fiveHourTimeRemainingPercent / 100f);
                }
            }

            if (_hasWeeklyWindow)
            {
                using (var trackPen = new Pen(_palette.Track, 2.2f))
                using (var fillPen = new Pen(weeklyAccent, 2.2f))
                {
                    trackPen.StartCap = trackPen.EndCap = LineCap.Round;
                    fillPen.StartCap = fillPen.EndCap = LineCap.Round;
                    var ring = new RectangleF(96, 10, 17, 17);
                    graphics.DrawArc(trackPen, ring, -90, 359.9f);
                    if (_active && _weeklyRemainingPercent > 0)
                        graphics.DrawArc(fillPen, ring, -90, 360f * _weeklyRemainingPercent / 100f);
                }
            }

            var value = _active ? _fiveHourRemainingPercent + "%" : "--%";
            var resetHours = _active ? FormatResetHours(_fiveHourResetsAt, DateTimeOffset.Now) : "--H";
            var fontScale = _settings.FontScalePercent / 100f;
            using (var valueFont = new Font("Segoe UI", 12.5f * 96f / 72f * fontScale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var labelFont = new Font("Segoe UI", 6.8f * 96f / 72f * fontScale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var weeklyFont = new Font("Segoe UI", 5.8f * 96f / 72f * fontScale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var valueBrush = new SolidBrush(_palette.Text))
            using (var labelBrush = new SolidBrush(_active ? accent : _palette.Muted))
            using (var weeklyBrush = new SolidBrush(_active ? weeklyAccent : _palette.Muted))
            {
                graphics.DrawString(value, valueFont, valueBrush, new RectangleF(42, 2, 52, 25));
                graphics.DrawString(_active ? resetHours : "OFFLINE", labelFont, labelBrush, new RectangleF(43, 23, 48, 14));
                if (_hasWeeklyWindow)
                {
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        graphics.DrawString(_weeklyRemainingPercent.ToString(CultureInfo.InvariantCulture), weeklyFont, weeklyBrush, new RectangleF(95, 9, 20, 20), format);
                }
            }
        }

        internal static string FormatResetHours(DateTimeOffset? resetsAt, DateTimeOffset now)
        {
            if (!resetsAt.HasValue) return "--H";
            var hours = Math.Max(0d, (resetsAt.Value - now).TotalHours);
            return hours.ToString("0.#", CultureInfo.InvariantCulture) + "H";
        }

        private void SetRoundedRegion()
        {
            using (var path = GraphicsExtensions.CreateRoundedPath(
                new RectangleF(0, 0, Width, Height),
                Math.Max(8f, 8f * GetDrawingScale())))
            {
                Region = new Region(path);
            }
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * amount),
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private float GetDrawingScale()
        {
            return _dpiScale * _settings.WidgetScalePercent / 100f;
        }

        private Size GetScaledSize()
        {
            return GraphicsExtensions.ScaleSize(new Size(GetBaseWidth(), BaseHeight), GetDrawingScale());
        }

        private int GetBaseWidth()
        {
            return _hasWeeklyWindow ? DualBaseWidth : SingleBaseWidth;
        }

        private void ApplyScaledSize()
        {
            if (!IsHandleCreated) return;
            Size = GetScaledSize();
            SetRoundedRegion();
        }
    }
}
