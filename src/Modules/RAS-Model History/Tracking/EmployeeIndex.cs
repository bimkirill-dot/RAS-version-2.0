using System;
using System.Collections.Generic;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Кто стоит за автором записи — для поля «Сотрудник» в подробностях записи. Своя запись автора (Live) несёт его
    /// учётную запись Windows и полное имя. У записей, полученных при синхронизации, их записал тот, кто синхронизировал, —
    /// там сотрудник ищется по собственным записям автора с тем же именем пользователя Revit.
    /// Полное имя приходит из каталога в первые секунды после запуска Revit: у самых ранних записей сеанса его может
    /// не быть — тогда оно берётся из других записей той же учётной записи.
    /// Без Revit и WPF: проверяется тестами.
    /// </summary>
    public sealed class EmployeeIndex
    {
        private readonly Dictionary<string, ChangeRecord> _byUser = new Dictionary<string, ChangeRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Запомнить сотрудников по свежим записям; более поздняя запись автора заменяет прежнюю.</summary>
        public void Add(IEnumerable<ChangeRecord> records)
        {
            foreach (var r in records)
            {
                if (!IsOwn(r)) continue;
                if (!string.IsNullOrEmpty(r.FullName)) _names[r.WindowsAccount] = r.FullName;
                if (string.IsNullOrEmpty(r.User)) continue;
                if (_byUser.TryGetValue(r.User, out var known) && known.Time > r.Time) continue;
                _byUser[r.User] = r;
            }
        }

        /// <summary>«Иванов Иван Иванович · RAS\ivanov»; null — неизвестно (запись старой версии, автор без плагина).</summary>
        public string Describe(ChangeRecord r)
        {
            if (r == null) return null;
            if (IsOwn(r)) return Text(r.WindowsAccount, r.FullName);
            return !string.IsNullOrEmpty(r.User) && _byUser.TryGetValue(r.User, out var own) ? Text(own.WindowsAccount, own.FullName) : null;
        }

        /// <summary>Запись сделана самим автором: учётная запись в ней — его.</summary>
        private static bool IsOwn(ChangeRecord r) =>
            r.Source == ChangeSource.Live && string.IsNullOrEmpty(r.RecordedBy) && !string.IsNullOrEmpty(r.WindowsAccount);

        private string Text(string account, string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) _names.TryGetValue(account, out fullName);
            return string.IsNullOrEmpty(fullName) ? account : fullName + " · " + account;
        }
    }
}
