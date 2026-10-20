using System;

namespace RASv2.Core
{
    /// <summary>
    /// Адреса сайтов модулей во внутренней сети (PRINCIPLES.md, «Сайт модуля»): код модуля → «http://имя:порт». Только в ядре,
    /// не в RasPaths: тот подключён и в загрузчик — его правка потребовала бы у всех переустановки плагина. Сайт коллизий пока на
    /// компьютере разработчика (20.10.2026: «сервером будет мой компьютер на тестовое время»); переедет на сервер — поменять
    /// здесь и выпустить плагин.
    /// </summary>
    internal static class ModuleSites
    {
        public static string Of(string moduleId) =>
            string.Equals(moduleId, "ClashAnalysis", StringComparison.OrdinalIgnoreCase) ? "http://WIN-QJI8IV2IK11:5080" : null;
    }
}
