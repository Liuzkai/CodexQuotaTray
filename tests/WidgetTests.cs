using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexQuotaTray;

// Isolated fixtures only. This executable does not include Program, TrayApplicationContext,
// SessionQuotaReader or CodexCompletionMonitor, and never loads the user's settings.
internal static class WidgetTests
{
    private static int _assertions;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass an isolated fixture directory.");
        var directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        TestSettings(Path.Combine(directory, "settings-fixture.ini"));
        TestGeometry();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        TestWindow(Path.Combine(directory, "widget-fixture.ini"));
        Console.WriteLine("PASS: " + _assertions + " assertions; only synthetic data and explicit fixture paths were used.");
        return 0;
    }

    private static void TestSettings(string path)
    {
        File.WriteAllText(path, "Theme=emerald\nWidgetScalePercent=120\nWidgetVisible=False\n");
        var legacy = AppSettings.Load(path);
        Assert(!legacy.WidgetPositionSaved && !legacy.WidgetVisible && legacy.ThemeId == "emerald", "legacy settings migrate without losing preferences");
        legacy.WidgetPositionSaved = true;
        legacy.WidgetX = -1500;
        legacy.WidgetY = -900;
        legacy.Save(path);
        var restored = AppSettings.Load(path);
        Assert(restored.WidgetPositionSaved && restored.WidgetX == -1500 && restored.WidgetY == -900, "negative monitor coordinates round trip");
        Assert(restored.WidgetScalePercent == 120 && restored.ThemeId == "emerald", "position saves preserve other preferences");
        foreach (var coordinates in new[] { "WidgetX=12", "WidgetX=oops\nWidgetY=3", "WidgetX=2147483647\nWidgetY=0", "WidgetX=0\nWidgetY=-2147483648" })
        {
            File.WriteAllText(path, "WidgetPositionSaved=True\n" + coordinates);
            Assert(!AppSettings.Load(path).WidgetPositionSaved, "invalid or incomplete position safely uses default");
        }
        restored.Reset();
        Assert(!restored.WidgetPositionSaved, "reset settings clears stored position");
    }

    private static void TestGeometry()
    {
        var size = new Size(122, 38);
        var main = new Rectangle(0, 0, 1920, 1040);
        var left = new Rectangle(-1920, -200, 1920, 1040);
        var above = new Rectangle(400, -1440, 2560, 1400);
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(400, 300), size, main) == new Point(400, 300), "valid placement is unchanged");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(-1800, -100), size, left) == new Point(-1800, -100), "left monitor with negative coordinates");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(500, -1300), size, above) == new Point(500, -1300), "monitor above primary");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(-1700, 300), size, main) == new Point(0, 300), "removed left display is recoverable");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(3800, 900), size, main) == new Point(1798, 900), "removed right display is recoverable");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(1900, 1030), size, main) == new Point(1798, 1002), "window fits entirely above taskbar");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(1800, 1000), new Size(244, 76), main) == new Point(1676, 964), "200 percent DPI size fits work area");
        Assert(TaskbarWidgetForm.ConstrainToWorkArea(new Point(500, 500), new Size(500, 500), new Rectangle(-100, -100, 200, 200)) == new Point(-100, -100), "oversized window keeps its top left reachable");
    }

    private static void TestWindow(string path)
    {
        var settings = new AppSettings();
        var work = Screen.PrimaryScreen.WorkingArea;
        Point saved;
        using (var widget = new TaskbarWidgetForm())
        {
            var saves = 0;
            var clicks = 0;
            widget.PositionChanged += delegate { saves++; settings.Save(path); };
            widget.Click += delegate { clicks++; };
            widget.ApplySettings(settings);
            var handle = widget.Handle;
            Application.DoEvents();
            Assert(work.Contains(widget.Bounds), "first run is in primary work area");
            var taskbar = FindWindow("Shell_TrayWnd", null);
            Assert(taskbar == IntPtr.Zero || GetWindow(handle, 4) != taskbar, "floating window is not owned by Explorer taskbar");

            widget.Location = new Point(work.Left + 80, work.Top + 80);
            widget.Reposition();
            var start = widget.Location;
            var before = saves;
            Mouse(widget, "OnMouseDown", MouseButtons.Left, new Point(10, 10));
            Mouse(widget, "OnMouseMove", MouseButtons.Left, new Point(11, 10));
            Invoke(widget, "OnClick", EventArgs.Empty);
            Mouse(widget, "OnMouseUp", MouseButtons.Left, new Point(11, 10));
            Assert(clicks == 1 && widget.Location == start && saves == before, "small pointer jitter remains a click without saving");

            Mouse(widget, "OnMouseDown", MouseButtons.Left, new Point(10, 10));
            Mouse(widget, "OnMouseMove", MouseButtons.Left, new Point(160, 100));
            Assert(widget.Location == new Point(start.X + 150, start.Y + 90), "left drag follows screen delta");
            Assert(saves == before, "drag does not write settings on every move");
            var dragging = widget.Location;
            widget.Reposition(true);
            widget.ApplyResult(null);
            Assert(widget.Location == dragging && saves == before, "guard and quota refresh cannot move or save mid-drag");
            Invoke(widget, "OnClick", EventArgs.Empty);
            Mouse(widget, "OnMouseUp", MouseButtons.Left, new Point(10, 10));
            Assert(clicks == 1 && saves == before + 1, "drop saves once and does not open details");
            saved = widget.Location;
            Assert(settings.WidgetX == saved.X && settings.WidgetY == saved.Y, "drag persists final position");

            before = saves;
            for (var tick = 0; tick < 10; tick++)
            {
                widget.EnsureOnTop();
                widget.RefreshDynamicAnchor();
                widget.ApplyResult(null);
                widget.ApplySettings(settings);
            }
            Assert(widget.Location == saved && saves == before, "repeated guard, data and settings refreshes leave the position alone");

            Mouse(widget, "OnMouseDown", MouseButtons.Left, new Point(10, 10));
            Invoke(widget, "OnClick", EventArgs.Empty);
            Mouse(widget, "OnMouseUp", MouseButtons.Left, new Point(10, 10));
            Assert(clicks == 2, "click after a drag still opens details");
            Mouse(widget, "OnMouseDown", MouseButtons.Right, new Point(10, 10));
            Mouse(widget, "OnMouseMove", MouseButtons.Right, new Point(150, 100));
            Invoke(widget, "OnClick", EventArgs.Empty);
            Mouse(widget, "OnMouseUp", MouseButtons.Right, new Point(10, 10));
            Assert(widget.Location == saved && clicks == 2, "right click remains available for context menu without dragging or opening details");

            Mouse(widget, "OnMouseDown", MouseButtons.Left, new Point(10, 10));
            Mouse(widget, "OnMouseMove", MouseButtons.Left, new Point(50, 50));
            widget.Capture = false;
            saved = widget.Location;
            Assert(settings.WidgetX == saved.X && settings.WidgetY == saved.Y, "capture loss completes and saves the drag");
            widget.EnsureOnTop();
            Assert(widget.Location == saved, "capture loss cannot leave placement guard stuck");

            widget.Location = new Point(work.Right - widget.Width, work.Top + 100);
            widget.Reposition();
            var singleWidth = widget.Width;
            var snapshot = new QuotaSnapshot();
            snapshot.Windows.Add(new QuotaWindow { Kind = QuotaWindowKind.FiveHour, WindowMinutes = 300 });
            snapshot.Windows.Add(new QuotaWindow { Kind = QuotaWindowKind.Weekly, WindowMinutes = 10080 });
            widget.ApplyResult(new QuotaReadResult { IsCodexRunning = true, Snapshot = snapshot });
            Assert(widget.Width > singleWidth && work.Contains(widget.Bounds), "weekly quota widening stays inside work area");
            Assert(widget.Top == work.Top + 100, "quota layout change preserves vertical placement");
            settings.WidgetScalePercent = 130;
            widget.ApplySettings(settings);
            Assert(work.Contains(widget.Bounds), "user scale change at screen edge remains reachable");
            settings.WidgetScalePercent = 100;
            widget.ApplySettings(settings);
            widget.ApplyResult(null);

            Dpi(widget, 144, new Point(work.Left + 100, work.Top + 100));
            Assert(widget.Width == 146 && widget.Height == 57, "150 percent DPI recalculates widget size");
            Dpi(widget, 192, new Point(work.Left + 120, work.Top + 120));
            Assert(widget.Width == 194 && widget.Height == 76, "200 percent DPI recalculates widget size");
            Mouse(widget, "OnMouseDown", MouseButtons.Left, new Point(10, 10));
            Mouse(widget, "OnMouseMove", MouseButtons.Left, new Point(60, 50));
            Dpi(widget, 96, new Point(work.Left + 180, work.Top + 180));
            var afterDpi = widget.Location;
            var cursor = (Point)typeof(TaskbarWidgetForm).GetField("_lastDragCursor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(widget);
            var nextCursor = new Point(cursor.X + 20, cursor.Y + 20);
            Mouse(widget, "OnMouseMove", MouseButtons.Left, widget.PointToClient(nextCursor));
            Assert(widget.Location == new Point(afterDpi.X + 20, afterDpi.Y + 20), "DPI transition rebases drag without jumping to old origin");
            Mouse(widget, "OnMouseUp", MouseButtons.Left, new Point(10, 10));

            widget.Location = new Point(900000, 900000);
            WindowMessage(widget, 0x007E, IntPtr.Zero, IntPtr.Zero);
            Application.DoEvents();
            Assert(Screen.FromRectangle(widget.Bounds).WorkingArea.Contains(widget.Bounds), "display-change message recovers off-screen window");
            widget.Location = new Point(-900000, -900000);
            widget.Reposition(true);
            Assert(Screen.FromRectangle(widget.Bounds).WorkingArea.Contains(widget.Bounds), "periodic guard also recovers off-screen window");
            widget.SetVisible(false);
            widget.ApplyResult(null);
            widget.ApplySettings(settings);
            Assert(!widget.Visible, "hidden widget stays hidden during refresh");
            widget.SetVisible(true);
            widget.ResetPosition();
            Assert(work.Contains(widget.Bounds), "reset position restores reachable primary location");
            // Keep the restart fixture away from edges: injected DPI above can differ
            // from the host's actual DPI, so an edge position may legitimately be clamped.
            widget.Location = new Point(work.Left + 100, work.Top + 100);
            widget.Reposition();
            saved = widget.Location;
            settings.Save(path);
        }
        var loaded = AppSettings.Load(path);
        using (var restarted = new TaskbarWidgetForm())
        {
            restarted.ApplySettings(loaded);
            var handle = restarted.Handle;
            Application.DoEvents();
            Assert(restarted.Location == saved, "restart restores saved coordinates");
        }
        loaded.WidgetX = 900000;
        loaded.WidgetY = -900000;
        using (var missingDisplay = new TaskbarWidgetForm())
        {
            missingDisplay.ApplySettings(loaded);
            var handle = missingDisplay.Handle;
            Application.DoEvents();
            Assert(Screen.FromRectangle(missingDisplay.Bounds).WorkingArea.Contains(missingDisplay.Bounds), "startup recovers a saved position on an absent display");
        }
    }

    private static void Dpi(TaskbarWidgetForm widget, int dpi, Point location)
    {
        var rect = new NativeRect { Left = location.X, Top = location.Y, Right = location.X + 200, Bottom = location.Y + 100 };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
        try
        {
            Marshal.StructureToPtr(rect, memory, false);
            WindowMessage(widget, 0x02E0, new IntPtr(dpi | dpi << 16), memory);
            Application.DoEvents();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void WindowMessage(TaskbarWidgetForm widget, int id, IntPtr wParam, IntPtr lParam)
    {
        var message = Message.Create(widget.Handle, id, wParam, lParam);
        Invoke(widget, "WndProc", message);
    }

    private static void Mouse(TaskbarWidgetForm widget, string method, MouseButtons button, Point point)
    {
        Invoke(widget, method, new MouseEventArgs(button, 1, point.X, point.Y, 0));
    }

    private static void Invoke(TaskbarWidgetForm widget, string method, object argument)
    {
        typeof(TaskbarWidgetForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(widget, new[] { argument });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        _assertions++;
        Console.WriteLine("PASS: " + message);
    }
}
