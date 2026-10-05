using System;
using System.Runtime.InteropServices;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Сколько времени пользователь не трогал мышь и клавиатуру. Пока человек работает, фоновая
    /// подготовка идёт короткими порциями; когда он отошёл или смотрит на модель — крупными.
    /// </summary>
    public static class UserActivity
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        private static bool _unavailable;

        /// <summary>Миллисекунды с последнего ввода (0 — неизвестно).</summary>
        public static long IdleMilliseconds
        {
            get
            {
                if (_unavailable) return 0;
                try
                {
                    var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
                    if (!GetLastInputInfo(ref info)) return 0;
                    return unchecked((uint)Environment.TickCount - info.dwTime);
                }
                catch (Exception)
                {
                    _unavailable = true; // не Windows (проверочная сборка) — считаем, что пользователь активен
                    return 0;
                }
            }
        }

        /// <summary>Пользователь не трогал мышь и клавиатуру хотя бы 1,5 секунды.</summary>
        public static bool IsIdle => IdleMilliseconds > 1500;
    }
}
