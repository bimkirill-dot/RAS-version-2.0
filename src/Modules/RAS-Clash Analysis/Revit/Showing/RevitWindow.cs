using System;
using System.Runtime.InteropServices;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// Revit — вперёд, когда на сайте нажали «Показать в Revit». Windows не даёт фоновой программе забрать экран у окна,
    /// с которым работают (браузер), — на время просьбы поток моста присоединяется к вводу этого окна; не вышло — мигает значок
    /// Revit на панели задач. Revit API не нужен: вызывается из потока моста.
    /// </summary>
    internal static class RevitWindow
    {
        public static void Activate(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            try
            {
                if (IsIconic(window)) ShowWindow(window, SwRestore);
                var foreground = GetForegroundWindow();
                if (foreground == window) return;
                var foregroundThread = GetWindowThreadProcessId(foreground, out _);
                var current = GetCurrentThreadId();
                var attached = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
                try
                {
                    BringWindowToTop(window);
                    if (!SetForegroundWindow(window)) Flash(window);
                }
                finally
                {
                    if (attached) AttachThreadInput(current, foregroundThread, false);
                }
            }
            catch (Exception)
            {
                // окно — удобство, а не дело: не вышло — человек переключится сам, коллизия всё равно откроется
            }
        }

        private static void Flash(IntPtr window)
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Window = window, Flags = 3 | 12, Count = 3 };
            FlashWindowEx(ref info);
        }

        private const int SwRestore = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    }
}
