using System;
using System.Collections.Generic;

namespace RASv2.Core
{
    /// <summary>
    /// Предохранитель (src\Core\DECISIONS.md): модуль, который за короткое время много раз падает в обработчиках
    /// событий Revit, отключается до перезапуска — иначе он сыплет ошибками на каждое действие и тормозит Revit.
    /// Порог — Limit ошибок за Window: единичные ошибки модуль не выключают. Без Revit API: проверяется тестами.
    /// </summary>
    internal sealed class ErrorFuse
    {
        public const int Limit = 20;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private readonly object _sync = new object();
        private readonly Dictionary<string, Queue<DateTime>> _errors = new Dictionary<string, Queue<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tripped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsTripped(string tag)
        {
            lock (_sync) return _tripped.Contains(tag);
        }

        /// <summary>Учесть ошибку модуля. true — именно эта ошибка выбила предохранитель (один раз на модуль).</summary>
        public bool Record(string tag, DateTime now)
        {
            lock (_sync)
            {
                if (_tripped.Contains(tag)) return false;
                if (!_errors.TryGetValue(tag, out var times)) _errors[tag] = times = new Queue<DateTime>();
                times.Enqueue(now);
                while (times.Count > 0 && now - times.Peek() > Window) times.Dequeue();
                if (times.Count < Limit) return false;
                _tripped.Add(tag);
                _errors.Remove(tag);
                return true;
            }
        }
    }
}
