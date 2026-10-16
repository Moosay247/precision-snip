using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Precision Text")]
[assembly: AssemblyDescription("Copy editable text from any screen region")]
[assembly: AssemblyCompany("Local utility")]
[assembly: AssemblyProduct("Precision Text")]
[assembly: AssemblyVersion("2.1.3.0")]
[assembly: AssemblyFileVersion("2.1.3.0")]

namespace PrecisionSnip
{
    internal static class Program
    {
        internal const string ShowEventName = "Local\\PrecisionText_Show_9E0A4071_0A0A_4E73_9D86_479437F36303";
        private static Mutex instanceMutex;

        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.EnablePerMonitorV2();

            if (args.Length == 1 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTest.Run();
            }

            if (args.Length == 1 && String.Equals(args[0], "--resident-self-test", StringComparison.OrdinalIgnoreCase))
            {
                return ResidentSelfTest.Run();
            }

            bool ownsMutex;
            instanceMutex = new Mutex(true, "Local\\PrecisionText_9E0A4071_0A0A_4E73_9D86_479437F36303", out ownsMutex);
            if (!ownsMutex)
            {
                SignalRunningInstance();
                return 0;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool backgroundOnly = args.Length == 1 && String.Equals(args[0], "--background", StringComparison.OrdinalIgnoreCase);
                using (PrecisionTextContext context = new PrecisionTextContext(!backgroundOnly))
                {
                    Application.Run(context);
                }
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "Precision Text could not start.\r\n\r\n" + exception.Message,
                    "Precision Text",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                instanceMutex.ReleaseMutex();
                instanceMutex.Dispose();
            }
        }

        private static void SignalRunningInstance()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowEventName))
                    {
                        signal.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(15);
                }
            }
        }
    }

    internal static class ResidentSelfTest
    {
        internal static int Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int result = 9;
            using (PrecisionTextContext context = new PrecisionTextContext(false))
            using (System.Windows.Forms.Timer testTimer = new System.Windows.Forms.Timer())
            {
                int phase = 0;
                testTimer.Interval = 50;
                testTimer.Tick += delegate
                {
                    if (phase == 0)
                    {
                        if (context.LifetimeMilliseconds != 60 * 60 * 1000)
                        {
                            result = 6;
                            context.ExitForTest();
                            return;
                        }

                        phase = 1;
                        context.TriggerHotkeyForTest();
                        return;
                    }

                    if (!context.HasOpenOverlay)
                    {
                        result = 7;
                        context.ExitForTest();
                        return;
                    }

                    context.CloseOverlayForTest();
                    result = 0;
                    context.ExitForTest();
                };
                testTimer.Start();
                Application.Run(context);
            }

            return result;
        }
    }

    internal sealed class PrecisionTextContext : ApplicationContext
    {
        private readonly Control dispatcher;
        private readonly EventWaitHandle showSignal;
        private readonly RegisteredWaitHandle signalRegistration;
        private readonly HotkeyWindow hotkeyWindow;
        private readonly NotifyIcon trayIcon;
        private readonly System.Windows.Forms.Timer lifetimeTimer;
        private TextSelectionOverlay overlay;
        private bool disposed;

        internal int LifetimeMilliseconds
        {
            get { return lifetimeTimer.Interval; }
        }

        internal bool HasOpenOverlay
        {
            get { return overlay != null && !overlay.IsDisposed; }
        }

        internal PrecisionTextContext(bool showImmediately)
        {
            dispatcher = new Control();
            IntPtr dispatcherHandle = dispatcher.Handle;

            bool signalCreated;
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName, out signalCreated);
            signalRegistration = ThreadPool.RegisterWaitForSingleObject(
                showSignal,
                HandleExternalSignal,
                null,
                Timeout.Infinite,
                false);

            hotkeyWindow = new HotkeyWindow();
            hotkeyWindow.HotkeyPressed += delegate { ShowSelection(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Capture text", null, delegate { ShowSelection(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "Precision Text - ready for one hour";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { ShowSelection(); };

            lifetimeTimer = new System.Windows.Forms.Timer();
            lifetimeTimer.Interval = 60 * 60 * 1000;
            lifetimeTimer.Tick += delegate
            {
                lifetimeTimer.Stop();
                ExitThread();
            };
            lifetimeTimer.Start();

            ThreadPool.QueueUserWorkItem(delegate { OcrProcessor.Prewarm(); });

            if (showImmediately)
            {
                ShowSelection();
            }
        }

        private void HandleExternalSignal(object state, bool timedOut)
        {
            if (disposed || dispatcher.IsDisposed)
            {
                return;
            }

            try
            {
                dispatcher.BeginInvoke(new Action(ShowSelection));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ShowSelection()
        {
            if (disposed)
            {
                return;
            }

            if (overlay != null && !overlay.IsDisposed)
            {
                overlay.Activate();
                return;
            }

            overlay = new TextSelectionOverlay();
            overlay.FormClosed += delegate
            {
                overlay.Dispose();
                overlay = null;
            };
            overlay.Show();
            overlay.Activate();
        }

        internal void TriggerHotkeyForTest()
        {
            hotkeyWindow.TriggerForTest();
        }

        internal void CloseOverlayForTest()
        {
            if (overlay != null && !overlay.IsDisposed)
            {
                overlay.Close();
            }
        }

        internal void ExitForTest()
        {
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            if (!disposed)
            {
                disposed = true;
                if (overlay != null && !overlay.IsDisposed)
                {
                    overlay.Close();
                }
                trayIcon.Visible = false;
                trayIcon.Dispose();
                lifetimeTimer.Stop();
                lifetimeTimer.Dispose();
                hotkeyWindow.Dispose();
                signalRegistration.Unregister(null);
                showSignal.Dispose();
                dispatcher.Dispose();
            }
            base.ExitThreadCore();
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x51;
        private const int WhKeyboardLowLevel = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSystemKeyDown = 0x0104;
        private const int WmSystemKeyUp = 0x0105;
        private const int VirtualKeyControl = 0x11;
        private const int VirtualKeyAlt = 0x12;
        private const int VirtualKeyS = 0x53;
        private readonly KeyboardHookProcedure hookProcedure;
        private IntPtr hookHandle;
        private bool chordDown;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookType,
            KeyboardHookProcedure procedure,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetWindowText(IntPtr window, string text);

        private delegate IntPtr KeyboardHookProcedure(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct LowLevelKeyboardInput
        {
            internal uint VirtualKeyCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInformation;
        }

        internal event EventHandler HotkeyPressed;

        internal HotkeyWindow()
        {
            CreateParams parameters = new CreateParams();
            parameters.Caption = "PrecisionTextHotkeyWindow";
            parameters.X = -32000;
            parameters.Y = -32000;
            parameters.Width = 1;
            parameters.Height = 1;
            CreateHandle(parameters);
            SetWindowText(Handle, "PrecisionTextHotkeyWindow");

            hookProcedure = HandleKeyboardInput;
            hookHandle = SetWindowsHookEx(WhKeyboardLowLevel, hookProcedure, GetModuleHandle(null), 0);
            if (hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new Win32Exception(error, "The Ctrl+Alt+S keyboard listener could not start.");
            }
        }

        private IntPtr HandleKeyboardInput(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                LowLevelKeyboardInput input = (LowLevelKeyboardInput)Marshal.PtrToStructure(
                    data,
                    typeof(LowLevelKeyboardInput));

                bool isKeyDown = message.ToInt32() == WmKeyDown || message.ToInt32() == WmSystemKeyDown;
                bool isKeyUp = message.ToInt32() == WmKeyUp || message.ToInt32() == WmSystemKeyUp;

                if (input.VirtualKeyCode == VirtualKeyS)
                {
                    if (isKeyDown &&
                        (GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0 &&
                        (GetAsyncKeyState(VirtualKeyAlt) & 0x8000) != 0)
                    {
                        if (!chordDown)
                        {
                            chordDown = true;
                            RaiseHotkeyPressed();
                        }
                        return new IntPtr(1);
                    }

                    if (isKeyUp)
                    {
                        chordDown = false;
                    }
                }
            }

            return CallNextHookEx(hookHandle, code, message, data);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
            {
                RaiseHotkeyPressed();
            }
            base.WndProc(ref message);
        }

        private void RaiseHotkeyPressed()
        {
            EventHandler handler = HotkeyPressed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        internal void TriggerForTest()
        {
            RaiseHotkeyPressed();
        }

        public void Dispose()
        {
            if (hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    internal static class SelfTest
    {
        internal static int Run()
        {
            Rectangle expected = new Rectangle(10, 20, 30, 40);
            Point[] seconds =
            {
                new Point(40, 60),
                new Point(10, 20),
                new Point(10, 60),
                new Point(40, 20)
            };
            Point[] firsts =
            {
                new Point(10, 20),
                new Point(40, 60),
                new Point(40, 20),
                new Point(10, 60)
            };

            for (int index = 0; index < firsts.Length; index++)
            {
                if (SelectionMath.Normalize(firsts[index], seconds[index]) != expected)
                {
                    return 2;
                }
            }

            Rectangle desktop = SystemInformation.VirtualScreen;
            using (Bitmap probe = ScreenCapture.Capture(new Rectangle(desktop.Left, desktop.Top, 2, 2)))
            {
                if (probe.Width != 2 || probe.Height != 2)
                {
                    return 3;
                }
            }

            using (Bitmap sample = new Bitmap(720, 260, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(sample))
            using (Font questionFont = new Font("Arial", 24.0f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font answerFont = new Font("Arial", 23.0f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                graphics.DrawString("An intangible product or action is a:", questionFont, Brushes.Black, 8, 10);
                graphics.DrawEllipse(Pens.DarkCyan, 38, 75, 14, 14);
                graphics.DrawString("good.", answerFont, Brushes.Black, 64, 70);
                graphics.DrawEllipse(Pens.DarkCyan, 38, 120, 14, 14);
                graphics.DrawString("market.", answerFont, Brushes.Black, 64, 115);
                graphics.DrawEllipse(Pens.DarkCyan, 38, 165, 14, 14);
                graphics.DrawString("service.", answerFont, Brushes.Black, 64, 160);

                string recognized = OcrProcessor.Recognize(sample);
                string diagnosticPath = Environment.GetEnvironmentVariable("PRECISION_TEXT_TEST_OUTPUT");
                if (!String.IsNullOrEmpty(diagnosticPath))
                {
                    File.WriteAllText(diagnosticPath, recognized, Encoding.UTF8);
                }
                if (String.IsNullOrWhiteSpace(recognized) ||
                    !Regex.IsMatch(recognized, @"(?im)^\*\s+good\.?\s*$") ||
                    !Regex.IsMatch(recognized, @"(?im)^\*\s+market\.?\s*$"))
                {
                    return 4;
                }
            }

            string normalized = OcrFormatter.NormalizeExplicitBullet("O market.");
            if (normalized != "* market.")
            {
                return 5;
            }

            return 0;
        }
    }

    internal static class NativeMethods
    {
        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmFlush();

        internal static void EnablePerMonitorV2()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        internal static void WaitForDesktopComposition()
        {
            try
            {
                DwmFlush();
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    internal static class SelectionMath
    {
        internal static Rectangle Normalize(Point first, Point second)
        {
            int left = Math.Min(first.X, second.X);
            int top = Math.Min(first.Y, second.Y);
            int right = Math.Max(first.X, second.X);
            int bottom = Math.Max(first.Y, second.Y);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }

    internal static class ScreenCapture
    {
        internal static Bitmap Capture(Rectangle screenBounds)
        {
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            {
                throw new InvalidOperationException("The selected area has no usable size.");
            }

            Bitmap bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        screenBounds.Left,
                        screenBounds.Top,
                        0,
                        0,
                        screenBounds.Size,
                        CopyPixelOperation.SourceCopy);
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    internal sealed class TextSelectionOverlay : Form
    {
        private readonly Rectangle virtualScreen;
        private bool isDragging;
        private Point dragStart;
        private Point dragCurrent;

        internal TextSelectionOverlay()
        {
            virtualScreen = SystemInformation.VirtualScreen;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Bounds = virtualScreen;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "PrecisionTextOverlay";
            Opacity = 0.28d;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Precision Text";
            TopMost = true;

            MouseDown += HandleMouseDown;
            MouseMove += HandleMouseMove;
            MouseUp += HandleMouseUp;
            KeyDown += HandleKeyDown;
            Shown += HandleShown;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (isDragging)
            {
                Rectangle selection = GetSelection();
                if (selection.Width > 0 && selection.Height > 0)
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(80, 0, 150, 255)))
                    using (Pen outside = new Pen(Color.Black, 6.0f))
                    using (Pen border = new Pen(Color.White, 3.0f))
                    {
                        outside.Alignment = PenAlignment.Outset;
                        border.Alignment = PenAlignment.Inset;
                        e.Graphics.FillRectangle(fill, selection);
                        e.Graphics.DrawRectangle(outside, selection.X, selection.Y, Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));
                        e.Graphics.DrawRectangle(border, selection.X, selection.Y, Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));
                    }

                    DrawBadge(e.Graphics, selection.Width + " x " + selection.Height, new Point(selection.Right, selection.Bottom));
                }
            }
            else
            {
                Point mouse = PointToClient(MousePosition);
                DrawBadge(e.Graphics, "Drag around text    Esc cancels", new Point(mouse.X + 18, mouse.Y + 24));
            }
        }

        private void HandleShown(object sender, EventArgs e)
        {
            Activate();
            Focus();
        }

        private void HandleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                Close();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            isDragging = true;
            dragStart = ConstrainToClient(e.Location);
            dragCurrent = dragStart;
            Capture = true;
            Invalidate();
        }

        private void HandleMouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging)
            {
                return;
            }

            dragCurrent = ConstrainToClient(e.Location);
            Invalidate();
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            if (!isDragging || e.Button != MouseButtons.Left)
            {
                return;
            }

            dragCurrent = ConstrainToClient(e.Location);
            Capture = false;
            isDragging = false;

            Rectangle localSelection = GetSelection();
            if (localSelection.Width < 4 || localSelection.Height < 4)
            {
                Invalidate();
                return;
            }

            Rectangle screenSelection = new Rectangle(
                virtualScreen.Left + localSelection.Left,
                virtualScreen.Top + localSelection.Top,
                localSelection.Width,
                localSelection.Height);

            try
            {
                Hide();
                Application.DoEvents();
                NativeMethods.WaitForDesktopComposition();

                string text;
                using (Bitmap selectedPixels = ScreenCapture.Capture(screenSelection))
                {
                    text = OcrProcessor.Recognize(selectedPixels);
                }

                if (String.IsNullOrWhiteSpace(text))
                {
                    Close();
                    MessageBox.Show(
                        "No readable text was found in that area.",
                        "Precision Text",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                TextClipboard.Copy(text);
                Close();
            }
            catch (Exception exception)
            {
                Close();
                MessageBox.Show(
                    "The selected text could not be copied.\r\n\r\n" + exception.Message,
                    "Precision Text",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void HandleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private Point ConstrainToClient(Point point)
        {
            return new Point(
                Math.Max(0, Math.Min(ClientSize.Width, point.X)),
                Math.Max(0, Math.Min(ClientSize.Height, point.Y)));
        }

        private Rectangle GetSelection()
        {
            return Rectangle.Intersect(ClientRectangle, SelectionMath.Normalize(dragStart, dragCurrent));
        }

        private void DrawBadge(Graphics graphics, string message, Point anchor)
        {
            Size measured = TextRenderer.MeasureText(message, Font, Size.Empty, TextFormatFlags.NoPadding);
            Rectangle badge = new Rectangle(anchor.X + 8, anchor.Y + 8, measured.Width + 24, measured.Height + 16);

            if (badge.Right > ClientSize.Width - 10)
            {
                badge.X = Math.Max(10, anchor.X - badge.Width - 8);
            }
            if (badge.Bottom > ClientSize.Height - 10)
            {
                badge.Y = Math.Max(10, anchor.Y - badge.Height - 8);
            }

            using (Brush background = new SolidBrush(Color.White))
            using (Pen outline = new Pen(Color.Black, 2.0f))
            {
                graphics.FillRectangle(background, badge);
                graphics.DrawRectangle(outline, badge);
            }

            TextRenderer.DrawText(
                graphics,
                message,
                Font,
                badge,
                Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal static class OcrProcessor
    {
        private const string WinRtSuffix = ", Windows.Foundation, ContentType=WindowsRuntime";
        private static readonly object RecognitionGate = new object();
        private static readonly Assembly WindowsRuntimeAssembly = Assembly.LoadFrom(
            Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.WindowsRuntime.dll"));
        private static Type engineType;
        private static Type decoderType;
        private static Type softwareBitmapType;
        private static Type ocrResultType;
        private static Type pixelFormatType;
        private static Type alphaModeType;
        private static object engine;
        private static object bgra8;
        private static object ignoreAlpha;
        private static MethodInfo createDecoderMethod;
        private static MethodInfo getSoftwareBitmapMethod;
        private static MethodInfo recognizeMethod;
        private static MethodInfo asRandomAccessStreamMethod;
        private static MethodInfo asTaskMethod;

        internal static void Prewarm()
        {
            try
            {
                using (Bitmap sample = new Bitmap(180, 48, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(sample))
                using (Font font = new Font("Arial", 22.0f, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawString("ready", font, Brushes.Black, 4, 6);
                    Recognize(sample);
                }
            }
            catch
            {
                // The next real capture will report a useful OCR error if needed.
            }
        }

        internal static string Recognize(Bitmap bitmap)
        {
            lock (RecognitionGate)
            {
                EnsureRuntime();
                return RecognizeCore(bitmap);
            }
        }

        private static void EnsureRuntime()
        {
            if (engine != null)
            {
                return;
            }

            engineType = GetWinRtType("Windows.Media.Ocr.OcrEngine");
            decoderType = GetWinRtType("Windows.Graphics.Imaging.BitmapDecoder");
            softwareBitmapType = GetWinRtType("Windows.Graphics.Imaging.SoftwareBitmap");
            ocrResultType = GetWinRtType("Windows.Media.Ocr.OcrResult");
            pixelFormatType = GetWinRtType("Windows.Graphics.Imaging.BitmapPixelFormat");
            alphaModeType = GetWinRtType("Windows.Graphics.Imaging.BitmapAlphaMode");

            engine = engineType.GetMethod(
                "TryCreateFromUserProfileLanguages",
                BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            if (engine == null)
            {
                throw new InvalidOperationException("Windows OCR is unavailable for the current display language.");
            }

            bgra8 = Enum.Parse(pixelFormatType, "Bgra8");
            ignoreAlpha = Enum.Parse(alphaModeType, "Ignore");
            createDecoderMethod = FindMethod(decoderType, "CreateAsync", true, 1);
            getSoftwareBitmapMethod = FindMethod(decoderType, "GetSoftwareBitmapAsync", false, 2);
            recognizeMethod = FindMethod(engineType, "RecognizeAsync", false, 1);

            Type streamExtensions = WindowsRuntimeAssembly.GetType("System.IO.WindowsRuntimeStreamExtensions", true);
            asRandomAccessStreamMethod = streamExtensions.GetMethod(
                "AsRandomAccessStream",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new Type[] { typeof(Stream) },
                null);
            asTaskMethod = FindAsTaskMethod();
        }

        private static string RecognizeCore(Bitmap bitmap)
        {
            Bitmap input = bitmap;
            Bitmap resized = null;
            int maximum = Convert.ToInt32(engineType.GetProperty(
                "MaxImageDimension",
                BindingFlags.Public | BindingFlags.Static).GetValue(null, null), CultureInfo.InvariantCulture);

            if (bitmap.Width > maximum || bitmap.Height > maximum)
            {
                double scale = Math.Min((double)maximum / bitmap.Width, (double)maximum / bitmap.Height);
                int width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                int height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height));
                }
                input = resized;
            }

            try
            {
                using (MemoryStream encoded = new MemoryStream())
                {
                    input.Save(encoded, ImageFormat.Png);
                    encoded.Position = 0;

                    object randomAccess = AsRandomAccessStream(encoded);
                    try
                    {
                        object decoderOperation = createDecoderMethod.Invoke(null, new object[] { randomAccess });
                        object decoder = AwaitOperation(decoderOperation, decoderType);

                        object bitmapOperation = getSoftwareBitmapMethod.Invoke(decoder, new object[] { bgra8, ignoreAlpha });
                        object softwareBitmap = AwaitOperation(bitmapOperation, softwareBitmapType);
                        try
                        {
                            object recognizeOperation = recognizeMethod.Invoke(engine, new object[] { softwareBitmap });
                            object result = AwaitOperation(recognizeOperation, ocrResultType);
                            return OcrFormatter.Format(result);
                        }
                        finally
                        {
                            IDisposable disposableBitmap = softwareBitmap as IDisposable;
                            if (disposableBitmap != null)
                            {
                                disposableBitmap.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        IDisposable disposableStream = randomAccess as IDisposable;
                        if (disposableStream != null)
                        {
                            disposableStream.Dispose();
                        }
                    }
                }
            }
            finally
            {
                if (resized != null)
                {
                    resized.Dispose();
                }
            }
        }

        private static Type GetWinRtType(string fullName)
        {
            Type type = Type.GetType(fullName + WinRtSuffix, false);
            if (type == null)
            {
                throw new InvalidOperationException("The Windows OCR component " + fullName + " is unavailable.");
            }
            return type;
        }

        private static object AsRandomAccessStream(Stream stream)
        {
            return asRandomAccessStreamMethod.Invoke(null, new object[] { stream });
        }

        private static object AwaitOperation(object operation, Type resultType)
        {
            Task task = (Task)asTaskMethod.MakeGenericMethod(resultType).Invoke(null, new object[] { operation });
            task.GetAwaiter().GetResult();
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        private static MethodInfo FindAsTaskMethod()
        {
            Type extensions = WindowsRuntimeAssembly.GetType("System.WindowsRuntimeSystemExtensions", true);

            foreach (MethodInfo method in extensions.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "AsTask" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1)
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsGenericType)
                {
                    continue;
                }

                Type definition = parameters[0].ParameterType.GetGenericTypeDefinition();
                if (definition.FullName == "Windows.Foundation.IAsyncOperation`1")
                {
                    return method;
                }
            }

            throw new MissingMethodException("Windows Runtime task conversion is unavailable.");
        }

        private static MethodInfo FindMethod(Type type, string name, bool isStatic, int parameterCount)
        {
            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.Name == name && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }
            throw new MissingMethodException(type.FullName, name);
        }
    }

    internal sealed class RecognizedLine
    {
        internal string Text { get; set; }
        internal double Left { get; set; }
        internal double Top { get; set; }
        internal double Height { get; set; }
        internal bool HasGeometry { get; set; }
        internal bool IsBullet { get; set; }
    }

    internal static class OcrFormatter
    {
        private static readonly Regex ExplicitBullet = new Regex(
            @"^\s*(?:[oO0]|[\u2022\u2023\u2043\u25CB\u25CF\u25E6\u25A0\u25A1\u25AA\u25AB\u2610\u2611\u2713\u2714])\s+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MarkerOnly = new Regex(
            @"^\s*(?:[oO0]|[\u2022\u2023\u2043\u25CB\u25CF\u25E6\u25A0\u25A1\u25AA\u25AB\u2610\u2611\u2713\u2714])\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string Format(object result)
        {
            List<RecognizedLine> lines = new List<RecognizedLine>();
            List<double> characterWidths = new List<double>();

            IEnumerable resultLines = GetProperty(result, "Lines") as IEnumerable;
            foreach (object sourceLine in resultLines)
            {
                string lineText = (Convert.ToString(GetProperty(sourceLine, "Text"), CultureInfo.CurrentCulture) ?? String.Empty).Trim();
                if (lineText.Length == 0)
                {
                    continue;
                }

                RecognizedLine line = new RecognizedLine();
                line.Text = NormalizeExplicitBullet(lineText);
                line.IsBullet = line.Text.StartsWith("* ", StringComparison.Ordinal);

                double left = Double.MaxValue;
                double top = Double.MaxValue;
                double bottom = Double.MinValue;
                IEnumerable sourceWords = GetProperty(sourceLine, "Words") as IEnumerable;
                foreach (object word in sourceWords)
                {
                    object bounds = GetProperty(word, "BoundingRect");
                    double x = ToDouble(GetProperty(bounds, "X"));
                    double y = ToDouble(GetProperty(bounds, "Y"));
                    double width = ToDouble(GetProperty(bounds, "Width"));
                    double height = ToDouble(GetProperty(bounds, "Height"));
                    string wordText = Convert.ToString(GetProperty(word, "Text"), CultureInfo.CurrentCulture) ?? String.Empty;

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y + height);
                    if (wordText.Length > 0)
                    {
                        characterWidths.Add(width / Math.Max(1, wordText.Length));
                    }
                }

                if (left != Double.MaxValue)
                {
                    line.Left = left;
                    line.Top = top;
                    line.Height = Math.Max(1.0d, bottom - top);
                    line.HasGeometry = true;
                }

                lines.Add(line);
            }

            if (lines.Count == 0)
            {
                return (Convert.ToString(GetProperty(result, "Text"), CultureInfo.CurrentCulture) ?? String.Empty).Trim();
            }

            double characterWidth = Median(characterWidths, 8.0d);
            MergeSeparateRadioMarkers(lines);
            MarkIndentedAnswerChoices(lines, characterWidth);

            StringBuilder output = new StringBuilder();
            for (int index = 0; index < lines.Count; index++)
            {
                if (index > 0)
                {
                    output.Append("\r\n");
                }

                string text = lines[index].Text.Trim();
                if (lines[index].IsBullet && !text.StartsWith("* ", StringComparison.Ordinal))
                {
                    output.Append("* ");
                }
                output.Append(text);
            }

            return output.ToString().Trim();
        }

        private static object GetProperty(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                throw new MissingMemberException(instance.GetType().FullName, name);
            }
            return property.GetValue(instance, null);
        }

        private static double ToDouble(object value)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        internal static string NormalizeExplicitBullet(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return String.Empty;
            }

            Match match = ExplicitBullet.Match(text);
            if (match.Success)
            {
                return "* " + text.Substring(match.Length).TrimStart();
            }

            return text.Trim();
        }

        private static void MergeSeparateRadioMarkers(List<RecognizedLine> lines)
        {
            HashSet<int> markerIndexes = new HashSet<int>();
            HashSet<int> claimedTextIndexes = new HashSet<int>();

            for (int markerIndex = 0; markerIndex < lines.Count; markerIndex++)
            {
                RecognizedLine marker = lines[markerIndex];
                if (!marker.HasGeometry || !MarkerOnly.IsMatch(marker.Text))
                {
                    continue;
                }

                double markerCenter = marker.Top + marker.Height / 2.0d;
                int bestTextIndex = -1;
                double bestDistance = Double.MaxValue;

                for (int textIndex = 0; textIndex < lines.Count; textIndex++)
                {
                    if (textIndex == markerIndex || claimedTextIndexes.Contains(textIndex))
                    {
                        continue;
                    }

                    RecognizedLine candidate = lines[textIndex];
                    if (!candidate.HasGeometry || MarkerOnly.IsMatch(candidate.Text) || candidate.Left <= marker.Left)
                    {
                        continue;
                    }

                    double candidateCenter = candidate.Top + candidate.Height / 2.0d;
                    double verticalDistance = Math.Abs(candidateCenter - markerCenter);
                    double tolerance = Math.Max(marker.Height, candidate.Height) * 0.9d;
                    if (verticalDistance > tolerance)
                    {
                        continue;
                    }

                    double score = verticalDistance + (candidate.Left - marker.Left) * 0.01d;
                    if (score < bestDistance)
                    {
                        bestDistance = score;
                        bestTextIndex = textIndex;
                    }
                }

                if (bestTextIndex >= 0)
                {
                    lines[bestTextIndex].IsBullet = true;
                    claimedTextIndexes.Add(bestTextIndex);
                    markerIndexes.Add(markerIndex);
                }
            }

            List<int> indexes = new List<int>(markerIndexes);
            indexes.Sort();
            for (int index = indexes.Count - 1; index >= 0; index--)
            {
                lines.RemoveAt(indexes[index]);
            }
        }

        private static void MarkIndentedAnswerChoices(List<RecognizedLine> lines, double characterWidth)
        {
            for (int questionIndex = 0; questionIndex < lines.Count - 2; questionIndex++)
            {
                string question = lines[questionIndex].Text.TrimEnd();
                if (!(question.EndsWith(":", StringComparison.Ordinal) || question.EndsWith("?", StringComparison.Ordinal)))
                {
                    continue;
                }

                int firstAnswer = questionIndex + 1;
                if (!lines[firstAnswer].HasGeometry)
                {
                    continue;
                }

                double answerLeft = lines[firstAnswer].Left;
                double tolerance = Math.Max(characterWidth * 2.5d, lines[firstAnswer].Height * 0.8d);
                int end = firstAnswer;

                while (end < lines.Count)
                {
                    RecognizedLine candidate = lines[end];
                    if (!candidate.HasGeometry || candidate.Text.Length > 160 || Math.Abs(candidate.Left - answerLeft) > tolerance)
                    {
                        break;
                    }
                    end++;
                }

                int count = end - firstAnswer;
                bool visiblyIndented = !lines[questionIndex].HasGeometry ||
                    answerLeft - lines[questionIndex].Left >= Math.Max(characterWidth * 2.0d, lines[firstAnswer].Height * 0.8d);

                if (count >= 2 && visiblyIndented)
                {
                    for (int answerIndex = firstAnswer; answerIndex < end; answerIndex++)
                    {
                        lines[answerIndex].IsBullet = true;
                    }
                    questionIndex = end - 1;
                }
            }
        }

        private static double Median(List<double> values, double fallback)
        {
            if (values.Count == 0)
            {
                return fallback;
            }

            values.Sort();
            int middle = values.Count / 2;
            if ((values.Count & 1) == 1)
            {
                return values[middle];
            }
            return (values[middle - 1] + values[middle]) / 2.0d;
        }
    }

    internal static class TextClipboard
    {
        internal static void Copy(string text)
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.UnicodeText, true, text);
            data.SetData(DataFormats.Text, true, text);
            Clipboard.SetDataObject(data, true, 10, 100);
        }
    }
}


