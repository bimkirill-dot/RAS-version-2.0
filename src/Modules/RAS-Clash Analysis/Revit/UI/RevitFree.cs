using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Revit доступен, пока открыты окна «Менеджера задач» (разработчик, 08.11.2026: «Revit нельзя пользоваться, когда открыто окно
    /// плагина — не важно, настроек, списка задач или отчёта выгрузки; хочу сворачивать Revit»). WPF-окно, открытое ShowDialog,
    /// выключает все окна потока — и главное окно Revit; здесь — включить Revit обратно, как только окно показано: вложенное окно
    /// держит только своё окно-владельца. Revit API в такие окна не ходит — Revit можно пользоваться как обычно.
    /// </summary>
    internal static class RevitFree
    {
        /// <summary>Главное окно Revit (ставит модуль при запуске); нет — ничего не делать (окна вне Revit).</summary>
        public static IntPtr RevitWindow { get; set; }

        /// <summary>Окно-диалог: показано — Revit снова доступен.</summary>
        public static T Free<T>(this T dialog) where T : Window
        {
            dialog.ContentRendered += (s, e) => Enable();
            dialog.Activated += (s, e) => Enable();
            return dialog;
        }

        public static void Enable()
        {
            if (RevitWindow != IntPtr.Zero) EnableWindow(RevitWindow, true);
        }

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool enable);
    }
}
