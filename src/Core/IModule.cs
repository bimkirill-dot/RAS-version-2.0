using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>
    /// Контракт модуля с ядром (PRINCIPLES.md, раздел 4). Ядро находит модули в сборках RASv2.*.dll и запускает их,
    /// а кнопки модулей раскладывает по панелям вкладки (RibbonLayout). Модуль не знает о других модулях.
    /// </summary>
    public interface IModule
    {
        /// <summary>Идентификатор для кода: латиница без пробелов (коды кнопок, метка в журнале). После выпуска не меняется.</summary>
        string Id { get; }

        /// <summary>Название для людей: панель по умолчанию для кнопок, папки данных, окно «О плагине».</summary>
        string Title { get; }

        /// <summary>В каких версиях Revit модуль работает; в остальных ядро его не запускает.</summary>
        IReadOnlyList<int> RevitYears { get; }

        /// <summary>Объявить кнопки, подписаться на события — всё через контекст ядра.</summary>
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

        /// <summary>
        /// Кнопка модуля; команда — класс модуля с IExternalCommand. Код кнопки — «&lt;Id модуля&gt;.&lt;name&gt;»: по нему
        /// раскладка ленты (ribbon.txt) ставит кнопку на панель, а без раскладки она стоит на панели с названием модуля.
        /// icon — имя иконки: картинки Icons\&lt;icon&gt;32.png и Icons\&lt;icon&gt;16.png, вшитые в сборку модуля.
        /// На ленте кнопка появляется после запуска всех модулей, поэтому описание и доступность задаются
        /// на возвращённых данных кнопки. Код после выпуска не меняется: по нему Revit помнит горячие клавиши.
        /// </summary>
        PushButtonData AddButton(string name, string text, Type command, string toolTip, string icon = null);
    }
}
