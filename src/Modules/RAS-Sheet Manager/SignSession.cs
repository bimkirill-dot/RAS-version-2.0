using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RASv2.SheetManager.Logic;
using RASv2.SheetManager.Signing;

namespace RASv2.SheetManager
{
    /// <summary>Центр подписи: как у остальных подписей, в начало координат или указать на виде.</summary>
    public enum SignPoint { Same, Origin, Pick }

    /// <summary>
    /// Одно нажатие «Подпись»: что окно получает от команды (семейство, таблицы, настройки, папки) и что возвращает (выбранные
    /// подписи, таблица, масштаб, точка). Окно открыто внутри команды — читать семейство (Revit API) из него можно.
    /// </summary>
    public sealed class SignSession
    {
        public SignFamily Family;
        public List<SignTableChoice> Tables;
        public SignSettings Settings;
        public SignPersonal Personal;
        /// <summary>Общие настройки — не с сервера (сервер не ответил): сохранить их сейчас не получится.</summary>
        public bool SettingsOffline;
        public string WorkRoot, PreviewCache;
        public XYZ Anchor;
        public int AnchorSpread;
        /// <summary>Сохранить общие настройки: null — сохранено, иначе — почему нет.</summary>
        public Func<SignSettings, string> SaveSettings;
        /// <summary>Строка в журнал модуля (ras.log).</summary>
        public Action<string> Log;
        /// <summary>Справочник инициалов для подписей ГИП (фамилия → «Д.»); пополнился — InitialsChanged, сохранит команда.</summary>
        public Dictionary<string, string> Initials = new Dictionary<string, string>();
        public bool InitialsChanged;

        // ── результат окна ──
        public string Folder;
        public string Table;
        public bool TableExisted;
        public SignFamilyState State;
        public string SourceEncoding;
        public string Backup;
        public List<SignJob> Jobs = new List<SignJob>();
        public double Scale = 1;
        /// <summary>Центр подписи: как у остальных подписей, в начало координат или указать на виде.</summary>
        public SignPoint Point = SignPoint.Same;
        public bool RemoveUnused;
        /// <summary>Сколько в папке уже очищено сейчас — для отчёта.</summary>
        public int CleanedNow;
    }
}
