using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Кто сидит за компьютером: учётная запись Windows («ДОМЕН\логин») и полное имя из каталога компании.
    /// Пишется в каждую запись журнала (ChangeRecord.WindowsAccount / FullName): имя пользователя Revit человек задаёт сам,
    /// и по нему сотрудника не найти. Полное имя спрашивается у контроллера домена — в фоне, один раз за запуск Revit:
    /// без сети запрос может висеть, а Revit ждать не должен. Пока имя не пришло (или его не узнать), FullName — null.
    /// </summary>
    internal static class WindowsUser
    {
        public static string Account { get; } = MakeAccount();

        public static string FullName { get; private set; }

        /// <summary>Узнать полное имя — вызывается при запуске модуля.</summary>
        public static void ResolveInBackground()
        {
            Task.Run(() =>
            {
                try { FullName = DisplayName(); }
                catch (Exception ex) { Diag.Log("Не удалось узнать полное имя пользователя Windows", ex); }
            });
        }

        private static string MakeAccount()
        {
            var user = Environment.UserName;
            if (string.IsNullOrEmpty(user)) return null;
            var domain = Environment.UserDomainName;
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }

        /// <summary>Имя для показа из каталога (GetUserNameEx, NameDisplay). Локальная учётная запись — null.</summary>
        private static string DisplayName()
        {
            uint size = 256;
            var buffer = new StringBuilder((int)size);
            if (!GetUserNameEx(NameDisplay, buffer, ref size))
            {
                if (Marshal.GetLastWin32Error() != ErrorMoreData) return null;
                buffer = new StringBuilder((int)size);
                if (!GetUserNameEx(NameDisplay, buffer, ref size)) return null;
            }
            var name = buffer.ToString().Trim();
            return name.Length == 0 ? null : name;
        }

        private const int NameDisplay = 3;
        private const int ErrorMoreData = 234;

        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserNameEx(int nameFormat, StringBuilder userName, ref uint size);
    }
}
