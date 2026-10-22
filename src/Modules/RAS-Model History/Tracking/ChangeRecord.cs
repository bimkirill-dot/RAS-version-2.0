using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Что произошло с элементом.</summary>
    public enum ChangeKind
    {
        Created,
        Modified,
        Deleted,
        /// <summary>Событие уровня модели: открытие, синхронизация, сохранение, обновление.</summary>
        Event
    }

    /// <summary>Как изменение было обнаружено.</summary>
    public enum ChangeSource
    {
        /// <summary>Зафиксировано в момент изменения у автора (самый точный источник).</summary>
        Live,
        /// <summary>Пришло из файла-хранилища при синхронизации.</summary>
        Sync,
        /// <summary>Пришло из файла-хранилища при «Обновить до последней версии».</summary>
        Reload,
        /// <summary>Обнаружено при открытии модели (изменения с прошлого сеанса).</summary>
        Open
    }

    /// <summary>Вид операции Revit, породившей изменение.</summary>
    public enum OperationKind
    {
        Commit,
        Undo,
        Redo,
        Rollback,
        None
    }

    /// <summary>Тип события модели (для записей Kind = Event).</summary>
    public enum ModelEventType
    {
        None = 0,
        Open,
        Close,
        /// <summary>Изменения попали в хранилище: синхронизация (совместная работа) или сохранение (одиночный файл).</summary>
        Commit,
        /// <summary>Сохранение локальной копии без синхронизации.</summary>
        SaveLocal,
        /// <summary>Открыта та же локальная копия, в которой остались несинхронизированные изменения прошлых сеансов.</summary>
        Resume,
        /// <summary>Локальная копия с несинхронизированными изменениями заменена — изменения в хранилище не попали.</summary>
        Lost,
        Reload,
        /// <summary>Актуализация журнала: перечисленные записи не соответствуют модели и убраны из журнала.</summary>
        Exclude,
        /// <summary>Убранные при актуализации записи возвращены в журнал (поле «ex» — их id).</summary>
        Restore,
        /// <summary>
        /// Модель сохранена из отсоединённой копии (новое хранилище или новый файл): отсоединённую копию журнал не вёл,
        /// поэтому здесь граница — что было с моделью до этого, журнал не знает.
        /// </summary>
        FromDetached,
        /// <summary>
        /// Хранилище заменено копией: синхронизации с «rt» до этого события пропали (DECISIONS.md модуля, «Хранилище заменено
        /// копией»). «sv» — версия хранилища, на которой замену заметили: по ней же видно, что эту замену уже записали.
        /// </summary>
        StorageReplaced,
        /// <summary>Связан журнал прежней истории модели («lk» — его ключ; DECISIONS.md модуля, «Связь журналов»).</summary>
        JournalLinked,
        /// <summary>Связь с журналом прежней истории убрана («lk» — его ключ).</summary>
        JournalUnlinked
    }

    /// <summary>Укрупнённый тип объекта — для фильтра «Тип объекта».</summary>
    public enum ObjectGroup
    {
        Model,
        Annotation,
        View,
        ViewTemplate,
        Sheet,
        Schedule,
        ElementType,
        Family,
        Material,
        Filter,
        Parameter,
        Analytical,
        Settings,
        Unknown
    }

    /// <summary>Изменение одного параметра: было → стало.</summary>
    public sealed class ParamChange
    {
        [JsonPropertyName("n")] public string Name { get; set; }
        [JsonPropertyName("o")] public string Old { get; set; }
        [JsonPropertyName("v")] public string New { get; set; }
    }

    /// <summary>Значение одного параметра (полный набор параметров элемента на момент записи).</summary>
    public sealed class ParamValue
    {
        [JsonPropertyName("n")] public string Name { get; set; }
        [JsonPropertyName("v")] public string Value { get; set; }
    }

    /// <summary>
    /// Одна запись журнала. Хранится построчно в JSONL, поэтому имена полей короткие.
    /// </summary>
    public sealed class ChangeRecord
    {
        /// <summary>Имя удалённого элемента, о котором модуль ничего не знал (удалён до того, как модуль его запомнил).</summary>
        public const string UnknownElementName = "(нет сведений: элемент удалён до того, как плагин его запомнил)";

        /// <summary>Уникальный id записи (для устранения дублей при чтении).</summary>
        [JsonPropertyName("r")] public string RecordId { get; set; }
        [JsonPropertyName("t")] public DateTimeOffset Time { get; set; }
        /// <summary>Автор изменения (имя пользователя Revit).</summary>
        [JsonPropertyName("u")] public string User { get; set; }
        /// <summary>Компьютер, на котором записано.</summary>
        [JsonPropertyName("pc")] public string Machine { get; set; }
        /// <summary>Кто записал (для Sync/Open — пользователь, чей плагин обнаружил изменение).</summary>
        [JsonPropertyName("by")] public string RecordedBy { get; set; }
        /// <summary>
        /// Учётная запись Windows, под которой сделана запись («ДОМЕН\логин»), — того, кто записал (как и «pc»).
        /// Нет — запись прежней версии плагина.
        /// </summary>
        [JsonPropertyName("wa")] public string WindowsAccount { get; set; }
        /// <summary>Полное имя этой учётной записи из каталога компании; нет — не удалось узнать.</summary>
        [JsonPropertyName("fn")] public string FullName { get; set; }

        [JsonPropertyName("k")] public ChangeKind Kind { get; set; }
        [JsonPropertyName("s")] public ChangeSource Source { get; set; }
        [JsonPropertyName("op")] public OperationKind Operation { get; set; }
        /// <summary>Имя транзакции / команды Revit («Перемещение», «Изменение типа» и т.п.).</summary>
        [JsonPropertyName("tx")] public string Transaction { get; set; }
        /// <summary>Id пакета (одна транзакция = один пакет).</summary>
        [JsonPropertyName("g")] public string Batch { get; set; }

        [JsonPropertyName("id")] public long ElementId { get; set; }
        [JsonPropertyName("uid")] public string UniqueId { get; set; }
        [JsonPropertyName("cat")] public string Category { get; set; }
        [JsonPropertyName("cls")] public string ClassName { get; set; }
        [JsonPropertyName("ot")] public ObjectGroup Group { get; set; }
        [JsonPropertyName("n")] public string Name { get; set; }
        /// <summary>Семейство : тип (для экземпляров и типов), вид/шаблон — для видов.</summary>
        [JsonPropertyName("tp")] public string TypeName { get; set; }
        [JsonPropertyName("lv")] public string Level { get; set; }
        [JsonPropertyName("ws")] public string Workset { get; set; }
        [JsonPropertyName("vw")] public string OwnerView { get; set; }

        /// <summary>Создатель элемента (из данных совместной работы).</summary>
        [JsonPropertyName("cr")] public string Creator { get; set; }
        /// <summary>Последний, кто синхронизировал изменение элемента с хранилищем.</summary>
        [JsonPropertyName("lc")] public string LastChangedBy { get; set; }
        /// <summary>Версия элемента (Element.VersionGuid) — для склейки одинаковых записей Sync/Open.</summary>
        [JsonPropertyName("ev")] public string ElementVersion { get; set; }

        [JsonPropertyName("p")] public List<ParamChange> Params { get; set; }
        /// <summary>
        /// Для «Изменён»: true — «было» известно, разница посчитана (пустая — параметры не поменялись);
        /// false — «было» неизвестно (снимка не было), отличие не проверить; null — запись старой версии плагина.
        /// </summary>
        [JsonPropertyName("bk")] public bool? BeforeKnown { get; set; }
        /// <summary>
        /// Все параметры элемента: для удалённого — на момент удаления, для созданного — сразу после создания.
        /// Благодаря этому значение любого параметра удалённого элемента видно всей команде.
        /// </summary>
        [JsonPropertyName("st")] public List<ParamValue> State { get; set; }
        /// <summary>Для сводной записи «и ещё N элементов».</summary>
        [JsonPropertyName("cnt")] public int? SummaryCount { get; set; }
        [JsonPropertyName("nz")] public bool? IsNoise { get; set; }
        [JsonPropertyName("doc")] public string DocTitle { get; set; }

        /// <summary>Сеанс работы с моделью (одно открытие на одном компьютере).</summary>
        [JsonPropertyName("ss")] public string Session { get; set; }
        /// <summary>Тип события модели.</summary>
        [JsonPropertyName("et")] public ModelEventType EventType { get; set; }
        /// <summary>Для Commit / Resume / Lost: сеансы, чьи несинхронизированные изменения это касается.</summary>
        [JsonPropertyName("inc")] public List<string> Sessions { get; set; }
        /// <summary>Для Exclude: id записей, убранных из журнала при актуализации.</summary>
        [JsonPropertyName("ex")] public List<string> Excludes { get; set; }
        /// <summary>
        /// Для Commit / Reload: версия хранилища после синхронизации — «VersionGUID:N:NumberOfSaves» (Document.GetDocumentVersion).
        /// По ней виден момент копии, если хранилище заменят копией: синхронизации, чьих версий в хранилище больше нет, — потеряны
        /// (DECISIONS.md модуля, «Хранилище заменено копией»). Нет — запись до 22.10.2026 или не совместная работа.
        /// </summary>
        [JsonPropertyName("sv")] public string StorageVersion { get; set; }
        /// <summary>Для StorageReplaced: время первой пропавшей синхронизации — изменения, попавшие в хранилище с него до замены, потеряны.</summary>
        [JsonPropertyName("rt")] public DateTimeOffset? LostFrom { get; set; }
        /// <summary>Для JournalLinked / JournalUnlinked: ключ журнала (папка) прежней истории.</summary>
        [JsonPropertyName("lk")] public string LinkedKey { get; set; }

        /// <summary>
        /// Удалён вместе с элементом, от которого зависел (марка — со стеной, проём — с перекрытием, арматура — с балкой):
        /// номер элемента-родителя. Нет — удалён сам.
        /// </summary>
        [JsonPropertyName("pid")] public long? ParentId { get; set; }
        /// <summary>Запись об удалении родителя (её «r») — ссылка «удалён вместе с …» в журнале.</summary>
        [JsonPropertyName("pr")] public string ParentRecord { get; set; }
        /// <summary>Родитель для человека: «Стены · Базовая стена».</summary>
        [JsonPropertyName("pn")] public string ParentName { get; set; }

        /// <summary>Сведения об удалённом элементе взяты из прежних записей журнала (сам модуль его не успел запомнить).</summary>
        [JsonIgnore] public bool InfoFromJournal { get; set; }

        [JsonIgnore] public bool IsSummary => SummaryCount.HasValue;
        [JsonIgnore] public bool HasParams => Params != null && Params.Count > 0;
        [JsonIgnore] public bool HasState => State != null && State.Count > 0;
    }
}
