using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>
    /// Контракт модуля с ядром (PRINCIPLES.md, раздел 4). Ядро находит модули в сборках RASv2.*.dll,
    /// создаёт им панель на вкладке и запускает. Модуль не знает о других модулях.
    /// </summary>
    public interface IModule
    {
        /// <summary>Идентификатор для кода: латиница без пробелов (имена кнопок, метка в журнале).</summary>
        string Id { get; }

        /// <summary>Название для людей: панель на ленте, папки данных, окно «О плагине».</summary>
        string Title { get; }

        /// <summary>В каких версиях Revit модуль работает; в остальных ядро его не запускает.</summary>
        IReadOnlyList<int> RevitYears { get; }

        /// <summary>Создать кнопки, подписаться на события — всё через контекст ядра.</summary>
        void Start(IModuleContext context);

        /// <summary>Revit закрывается: дописать несохранённое, отпустить файлы.</summary>
        void Stop();
    }

    /// <summary>Что ядро даёт модулю. Всё общее модуль берёт отсюда и не заводит свои копии.</summary>
    public interface IModuleContext
    {
        UIControlledApplication Application { get; }

        /// <summary>Номер сборки плагина: «2026.10.05-143210».</summary>
        string BuildId { get; }

        int RevitYear { get; }

        /// <summary>Панель модуля на вкладке «RAS v. 2.0».</summary>
        RibbonPanel Panel { get; }

        /// <summary>События Revit — подписка только через ядро.</summary>
        IRevitEvents Events { get; }

        /// <summary>Журнал ошибок с меткой модуля.</summary>
        ModuleLog Log { get; }

        /// <summary>Данные модуля на компьютере: %LocalAppData%\RAS_v_2.0\&lt;модуль&gt;.</summary>
        string LocalDataDir { get; }

        /// <summary>Личные настройки и вид окон модуля: %AppData%\RAS_v_2.0\&lt;модуль&gt;.</summary>
        string UserDataDir { get; }

        /// <summary>Общие данные модуля на сервере: Data\&lt;модуль&gt; (только если модулю они нужны).</summary>
        string SharedDataDir { get; }

        /// <summary>Кнопка на панели модуля; команда — класс модуля с IExternalCommand.</summary>
        PushButton AddButton(string name, string text, Type command, string toolTip);
    }
}
