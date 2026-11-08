using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>Что с NWC в окне хода.</summary>
    public enum NwcState { Waiting, Working, Done, Skipped, Failed }

    /// <summary>Строка окна хода: NWC запуска, её шаг и итог.</summary>
    public sealed class NwcProgress
    {
        public NwcJob Job { get; set; }
        public NwcState State { get; set; }
        public string Step { get; set; } = "ждёт";
        public string Note { get; set; }
        public TimeSpan Time { get; set; }
        /// <summary>Когда менялась модель (проверка до открытия) — в историю выгрузок.</summary>
        public DateTime? ModelModified { get; set; }
        /// <summary>Итог NWC и RVT модели по отдельности (одно открытие — две функции, 08.11.2026); null — функция не нужна.</summary>
        public NwcState? Nwc { get; set; }
        public NwcState? Rvt { get; set; }
        public string NwcNote { get; set; }
        public string RvtNote { get; set; }
        /// <summary>Пересохранение модели на место (08.11.2026); null — не нужно.</summary>
        public NwcState? Resave { get; set; }
        public string ResaveNote { get; set; }
    }

    /// <summary>Понятная ошибка модели — в журнал и окно без стека (стек — у непредвиденных, и они же — в ras.log).</summary>
    internal sealed class NwcError : Exception
    {
        public NwcError(string message) : base(message) { }
    }

    /// <summary>
    /// Пересохранение на место (08.11.2026) — первым после открытия, пока модель не тронута: резервная копия, «Сохранить как»
    /// центральную туда же (сжать — по настройкам), освободить всё; дальше NWC и RVT — по копии, открытой заново.
    /// Выгрузка RVT (08.11.2026) — тем же открытием, после NWC: удалить все связи, очистить от неиспользуемого три раза, «Сохранить как»
    /// в папку RVT (рабочие наборы — оставить и освободить или без них, по общим настройкам).
    /// Выгрузка NWC (DECISIONS.md модуля, «Порядок на одну модель», «Выгрузка NWC — первая версия»): на каждую модель — копия во
    /// временную папку, связи в копии — «не загружать», открыть отсоединённой со всеми рабочими наборами, удалить внешнее (связи
    /// Revit, IFC, DWG, облака точек), проверить и очистить вид, выгрузить NWC во временную папку, заменить прежнюю NWC, закрыть
    /// без сохранения, копию удалить. Каждый шаг — в журнал со временем и памятью; окна и предупреждения Revit — закрываются и
    /// пишутся в журнал. Только в контексте Revit API (из окна, открытого командой).
    /// </summary>
    internal sealed class NwcExporter
    {
        public static string TempRoot => Path.Combine(Path.GetTempPath(), "RAS_v_2.0", "NWC");
        public static string HistoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAS_v_2.0", "RAS-Clash Analysis", "Выгрузка NWC", "история.json");

        // предупреждений Revit на модель — в журнал не больше стольких, остальные — счётом
        private const int RevitMessagesPerModel = 40;

        private readonly UIApplication _ui;
        private readonly Application _app;
        private readonly ExportJournal _j;
        private readonly int _year;
        private readonly NwcHistory _history = NwcHistory.Load(HistoryPath);
        // записи этой выгрузки — в файл истории слиянием: фоновый Revit другой версии пишет туда же
        private readonly List<NwcHistory.Entry> _written = new List<NwcHistory.Entry>();
        private NwcProgress _current;
        private Action<NwcProgress> _changed;
        private int _revitMessages;
        private int _dialogs;
        // Shift при «Запустить» — выгрузить и актуальные NWC
        // копий моделей Revit Server наперёд одновременно (ModelPrefetch)
        private readonly int _parallel;
        private ModelPrefetch _prefetch;
        // дата изменения текущей модели — в историю
        private DateTime? _modified;

        public NwcExporter(UIApplication ui, ExportJournal journal, int year, int parallel)
        {
            _ui = ui;
            _app = ui.Application;
            _j = journal;
            _year = year;
            _parallel = parallel;
        }

        /// <summary>Шапка журнала: где, кто, что и с какими настройками; план — что выгружается и что нет.</summary>
        public void Header(string title, ExportSettings common, List<NwcJob> jobs, List<string> notes)
        {
            _j.Line($"Выгрузка NWC — «{title}»");
            _j.Line($"Revit {_app.VersionNumber} ({_app.SubVersionNumber}, сборка {_app.VersionBuild}), {Environment.UserName} на {Environment.MachineName}, " +
                    $"плагин {typeof(NwcExporter).Assembly.GetName().Version}, .NET {Environment.Version}");
            _j.Line($"Временная папка: {TempRoot}; свободно на диске: {FreeSpace(TempRoot)}");
            _j.Line($"Удалять перед выгрузкой: связи Revit {Yes(common.RemoveRevitLinks)}, DWG и CAD {Yes(common.RemoveCad)}, облака точек {Yes(common.RemovePointClouds)}, IFC {Yes(common.RemoveIfc)} (общие; строка может задать свои)");
            if (jobs.Any(x => x.RvtPath != null))
            {
                var r = common.Rvt ?? new RvtOptions();
                _j.Line($"RVT: проверка при открытии {Yes(r.Audit)}; рабочие наборы — {(r.KeepWorksets ? "оставить, все освободить" : "без рабочих наборов")}; " +
                        $"имя — {(string.IsNullOrWhiteSpace(r.TrimAfter) ? "как у модели" : "обрезать всё после «" + r.TrimAfter.Trim() + "»")}; уже есть — {(r.Overwrite ? "перезаписать" : "пропустить")}; " +
                        $"связи — все удаляются; очистка от неиспользуемого — 3 раза ({PurgeKind})");
            }
            // что выгружать, решает человек (разработчик, 07.11.2026: «не нужно самому пропускать модели — для этого есть
            // „Экспортировать модели, отредактированные после“»): модели, которые не менялись, тоже выгружаются
            _j.Line($"NWC к выгрузке: {jobs.Count(x => x.Skip == null)}, пропускаются: {jobs.Count(x => x.Skip != null)}");
            var i = 0;
            foreach (var job in jobs)
            {
                i++;
                _j.Line($"  {i,3}. {job.ModelName}  →  {(job.Off ? "NWC не выгружается" : job.NwcPath)}");
                if (job.RvtPath != null) _j.Line("       RVT  →  " + job.RvtPath);
                if (job.Resave)
                {
                    var r = job.Export.Resave ?? new ResaveOptions();
                    _j.Line($"       пересохранение на место: сжать {Yes(r.Compact)}, проверить (Audit) {Yes(r.Audit)}; резервная копия → {job.BackupDir ?? r.BackupDir}");
                }
                _j.Line($"       модель: {job.Model.Path}{(job.Model.Year > 0 ? $" (Revit {job.Model.Year})" : "")}; строки: {string.Join(", ", job.Rows)}");
                _j.Line($"       вид: {(job.Export.Nwc.Scope == NwcScope.View ? "«" + job.View + "»" : "целый проект")}; " + OptionsText(job.Export.Nwc) +
                        (job.Edited?.On == true ? $"; отбор: отредактированные {job.Edited.Mode} {job.Edited.From:dd.MM.yyyy}{(job.Edited.To != null ? "–" + job.Edited.To.Value.ToString("dd.MM.yyyy") : "")}" : ""));
                if (job.Skip != null) _j.Line("       пропускается: " + job.Skip);
            }
            foreach (var note in notes) _j.Warn(note);
            _j.Line("");
        }

        /// <summary>Выгрузить всё по порядку; «Отмена» — после текущей модели.</summary>
        public void Run(IList<NwcProgress> items, Action<NwcProgress> changed, Func<bool> cancelled)
        {
            _changed = changed;
            _ui.DialogBoxShowing += OnDialog;
            _app.FailuresProcessing += OnFailures;
            try
            {
                // проверка до открытия — у всех сразу: наперёд копируются только те, что будут выгружаться
                _j.Line($"══ Проверка до открытия — {items.Count(p => p.Job.Skip == null)} {ExportTask.ModelsWord(items.Count(p => p.Job.Skip == null))}");
                foreach (var p in items)
                {
                    if (cancelled()) break;
                    _current = p;
                    if (p.Job.Skip != null)
                    {
                        p.State = NwcState.Skipped;
                        p.Step = p.Note = p.Job.Skip;
                        _j.Line($"── {p.Job.ModelName}: пропущена — {p.Job.Skip}");
                        changed(p);
                        continue;
                    }
                    _j.Line("── " + p.Job.ModelName);
                    try
                    {
                        var skip = false;
                        Step("Проверка до открытия", () => skip = Before(p.Job));
                        p.ModelModified = _modified;
                        if (skip) p.State = NwcState.Skipped;
                    }
                    catch (Exception ex)
                    {
                        p.State = NwcState.Failed;
                        p.Note = ex is NwcError ? ex.Message : "Проверка до открытия: " + ex.Message;
                        _j.Error(p.Job.ModelName + " — " + p.Note, ex is NwcError ? null : ex);
                    }
                    changed(p);
                }
                _j.Line("");

                var due = items.Where(p => p.State == NwcState.Waiting).ToList();
                _prefetch = new ModelPrefetch(due.Select(p => p.Job), _parallel, _year, TempRoot, _j);
                _prefetch.Start(due.FirstOrDefault()?.Job);
                _j.Line("");

                for (var i = 0; i < items.Count; i++)
                {
                    var p = items[i];
                    if (p.State != NwcState.Waiting) continue;
                    if (cancelled())
                    {
                        _prefetch.Dispose();
                        p.State = NwcState.Skipped;
                        p.Step = "отменено";
                        changed(p);
                        _j.Line($"══ [{i + 1}/{items.Count}] {p.Job.ModelName} — отменено");
                        continue;
                    }
                    var clock = Stopwatch.StartNew();
                    _current = p;
                    _revitMessages = 0;
                    _j.Line($"══ [{i + 1}/{items.Count}] {p.Job.ModelName}");
                    One(p);
                    p.Time = clock.Elapsed;
                    _j.Line($"   итог: {StateText(p.State)}{(p.Note == null ? "" : " — " + p.Note)}; {NwcHistory.Duration(p.Time.TotalSeconds)}");
                    _j.Line("");
                    changed(p);
                }
            }
            finally
            {
                _prefetch?.Dispose();
                _ui.DialogBoxShowing -= OnDialog;
                _app.FailuresProcessing -= OnFailures;
                _current = null;
                try { NwcHistory.Merge(HistoryPath, _written); }
                catch (Exception ex) { _j.Line("история выгрузок не записана: " + ex.Message); }
                Summary(items);
            }
        }

        private void One(NwcProgress p)
        {
            var job = p.Job;
            p.State = NwcState.Working;
            // что делать с моделью: NWC — галочка «NWC»; RVT — галочка «RVT» (не пропущен и не упал до открытия — CheckRvt)
            var wantNwc = !job.Off;
            var wantRvt = job.RvtPath != null && p.Rvt == null;
            var wantResave = job.Resave && p.Resave == null;
            if (wantNwc) p.Nwc = NwcState.Working;
            if (wantRvt) p.Rvt = NwcState.Waiting;
            if (wantResave) p.Resave = NwcState.Waiting;
            var rvt = job.Export.Rvt ?? new RvtOptions();
            var temp = System.IO.Path.Combine(TempRoot, DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var copy = System.IO.Path.Combine(temp, ExportNodes.FileName(job.Model.Path));
            var nwcName = ExportNodes.FileName(job.NwcPath);
            nwcName = nwcName.EndsWith(".nwc", StringComparison.OrdinalIgnoreCase) ? nwcName.Substring(0, nwcName.Length - 4) : nwcName;
            Document doc = null;
            var entry = new NwcHistory.Entry { Nwc = job.NwcPath, At = DateTime.Now };
            var clock = Stopwatch.StartNew();
            ModelPrefetch.Copy ahead = null;
            var rvtSaved = false;
            try
            {
                Step("Копия модели", () =>
                {
                    ahead = _prefetch?.Take(job);
                    if (ahead != null)
                    {
                        var wait = Stopwatch.StartNew();
                        while (!ahead.Task.IsCompleted)
                        {
                            _current.Step = "Копия модели — ждём копию наперёд";
                            _changed(_current);
                            ahead.Task.Wait(300);
                        }
                        if (ahead.Error == null)
                        {
                            copy = ahead.File;
                            _j.Line($"   копия наперёд: {copy}, {NwcHistory.SizeText(ahead.Size)}, копировалась {NwcHistory.Duration(ahead.Seconds)}, " +
                                    (wait.Elapsed.TotalSeconds < 1 ? "была готова" : $"ждали {NwcHistory.Duration(wait.Elapsed.TotalSeconds)}"));
                            return;
                        }
                        _j.Line("   копия наперёд не вышла (" + ahead.Error + ") — копирует Revit");
                    }
                    Directory.CreateDirectory(temp);
                    var source = ModelPathUtils.ConvertUserVisiblePathToModelPath(job.Model.Path);
                    try
                    {
                        _app.CopyModel(source, copy, true);
                    }
                    catch (Exception ex) when (!ExportNodes.IsServerPath(job.Model.Path))
                    {
                        _j.Line("   CopyModel не вышел (" + ex.Message + ") — копия файлом");
                        if (!File.Exists(job.Model.Path)) throw new NwcError("файла модели нет: " + job.Model.Path);
                        File.Copy(job.Model.Path, copy, true);
                    }
                    if (!File.Exists(copy)) throw new NwcError("копия модели не появилась во временной папке");
                    _j.Line($"   копия: {copy}, {NwcHistory.SizeText(new FileInfo(copy).Length)}");
                });

                var copyPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(copy);
                var workshared = false;
                Step("Сведения о файле", () =>
                {
                    var info = BasicFileInfo.Extract(copy);
                    workshared = info.IsWorkshared;
                    _j.Line($"   формат: Revit {info.Format}; совместная работа: {Yes(info.IsWorkshared)}; файл хранилища: {Yes(info.IsCentral)}; сохранил: {info.Username}");
                    if (int.TryParse(info.Format, out var format))
                    {
                        if (format > _year) throw new NwcError($"модель Revit {format} — новее этого Revit {_year}: выгружайте в Revit {format}");
                        if (format < _year) _j.Warn($"{job.ModelName}: модель Revit {format} — при открытии обновится до {_year} (дольше; в Revit {format} — быстрее)");
                    }
                });

                // пересохранение — модель на место такой, какая была: связи загружаются как обычно (иначе у коллег станут «не
                // загружены»); резервная копия — с нетронутой копии, до открытия
                if (wantResave)
                {
                    try
                    {
                        Step("Пересохранение: резервная копия", () => Backup(copy, job));
                    }
                    catch (Exception ex) when (wantNwc || wantRvt)
                    {
                        p.Resave = NwcState.Failed;
                        p.ResaveNote = "резервная копия не записана — модель не пересохраняется: " + ex.Message;
                        _j.Error(job.ModelName + " — " + p.ResaveNote);
                        wantResave = false;
                    }
                }
                if (!wantResave) Step("Связи — не загружать", () => DoNotLoadLinks(copyPath));

                // RVT без рабочих наборов — их убирает только открытие («отсоединить и удалить рабочие наборы»): без NWC — сразу так; с NWC —
                // NWC по обычному открытию (виды — с рабочими наборами), потом второе открытие той же копии (без нового копирования)
                var discard = wantRvt && workshared && !rvt.KeepWorksets;
                var audit = wantRvt && rvt.Audit || wantResave && (job.Export.Resave ?? new ResaveOptions()).Audit;
                void Open(bool dropWorksets)
                {
                    var options = new OpenOptions { Audit = audit };
                    if (workshared)
                    {
                        if (dropWorksets) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
                        else
                        {
                            options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                            options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                        }
                    }
                    doc = _app.OpenDocumentFile(copyPath, options);
                    if (doc == null) throw new NwcError("Revit не открыл модель");
                    _j.Line($"   открыта: «{doc.Title}»; элементов: {new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount()}" +
                            (doc.IsWorkshared ? $"; рабочих наборов: {new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Count()}" : "") +
                            (dropWorksets ? "; рабочие наборы удалены (RVT — без них)" : "") + (audit ? "; с проверкой (Audit)" : ""));
                }
                Step(audit ? "Открытие с проверкой" : "Открытие", () => Open(discard && !wantNwc && !wantResave));

                if (wantResave)
                {
                    p.Resave = NwcState.Working;
                    try
                    {
                        Step("Пересохранение на место", () => ResaveTo(doc, job));
                        p.Resave = NwcState.Done;
                        p.ResaveNote = "пересохранена" + ((job.Export.Resave ?? new ResaveOptions()).Compact ? ", сжата" : "");
                    }
                    catch (Exception ex) when (wantNwc || wantRvt)
                    {
                        p.Resave = NwcState.Failed;
                        p.ResaveNote = ex is NwcError ? ex.Message : $"{p.Step}: {ex.Message}";
                        _j.Error(job.ModelName + " — пересохранение: " + p.ResaveNote, ex is NwcError ? null : ex);
                        if (!(ex is NwcError)) RASv2.Core.Log.Write("ClashAnalysis", "Пересохранение: " + job.Model.Path, ex);
                    }
                    // дальше NWC и RVT — по копии, открытой заново: модель стала центральной на месте, и правки в ней заняли бы её
                    // элементы у всех (без рабочих наборов — нет: правки не сохраняются)
                    if ((wantNwc || wantRvt) && doc != null && doc.IsWorkshared)
                    {
                        var first = doc;
                        doc = null;
                        Step("Закрытие (после пересохранения)", () => first.Close(false));
                        Step("Связи — не загружать", () => DoNotLoadLinks(copyPath));
                        audit = wantRvt && rvt.Audit;
                        Step(audit ? "Открытие с проверкой (для NWC и RVT)" : "Открытие (для NWC и RVT)", () => Open(discard && !wantNwc));
                    }
                }

                if (wantNwc)
                {
                    try
                    {
                        // вид до удаления — замер (07.11.2026, модель АР: 86 видимых — только камеры и рамки, а у разработчика в Revit на этом
                        // виде всё есть). Та же модель, тот же Revit то пустой вид, то полный (08.11.2026: два запуска — 0, третий — 35 588;
                        // никто ничего не нажимал) — пустой сразу после открытия: закрыть и открыть ещё раз; снова пустой — ошибка с пробами
                        var before = job.Export.Nwc.Scope == NwcScope.View ? CountVisibleModel(doc, job.View) : -1;
                        if (before >= 0) _j.Line($"   до удаления на виде «{job.View}» видно элементов модели: {before}");
                        if (before == 0)
                        {
                            _j.Warn($"{job.ModelName}: вид «{job.View}» пустой сразу после открытия — модель закрывается и открывается ещё раз");
                            var first = doc;
                            doc = null;
                            Step("Закрытие (пустой вид)", () => first.Close(false));
                            Step("Открытие (второй раз)", () => Open(false));
                            before = CountVisibleModel(doc, job.View);
                            _j.Line($"   после повторного открытия на виде видно элементов модели: {before}");
                        }
                        Step("Удаление внешнего", () => RemoveExternal(doc, job.Export));

                        View3D view = null;
                        Step("Вид", () => view = PrepareView(doc, job, entry));

                        var exported = System.IO.Path.Combine(temp, nwcName + ".nwc");
                        Step("Выгрузка NWC", () =>
                        {
                            // с копией наперёд своя временная папка не создавалась (копия — в папке pre_…), а NWC пишется сюда (06.11.2026)
                            Directory.CreateDirectory(temp);
                            doc.Export(temp, nwcName, Options(job.Export.Nwc, view));
                            if (!File.Exists(exported)) throw new NwcError("Revit не создал NWC (выгрузка прошла без ошибки, а файла нет)");
                            entry.Size = new FileInfo(exported).Length;
                            if (entry.Size == 0) throw new NwcError("NWC пустая (0 байт)");
                            _j.Line($"   NWC: {NwcHistory.SizeText(entry.Size)}");
                        });

                        Step("Замена NWC", () => Replace(exported, job.NwcPath));

                        entry.Seconds = clock.Elapsed.TotalSeconds;
                        entry.Signature = NwcRunPlan.Signature(job);
                        entry.ModelModified = p.ModelModified;
                        foreach (var note in NwcHistory.Compare(_history.Last(job.NwcPath), entry)) _j.Warn(job.ModelName + ": " + note);
                        _history.Put(entry);
                        _written.Add(entry);
                        p.Nwc = NwcState.Done;
                        p.NwcNote = NwcHistory.SizeText(entry.Size);
                    }
                    catch (Exception ex) when (doc != null && wantRvt)
                    {
                        // NWC не вышла, модель открыта — RVT всё равно сохраняется (функции независимы)
                        p.Nwc = NwcState.Failed;
                        p.NwcNote = ex is NwcError ? ex.Message : $"{p.Step}: {ex.Message}";
                        _j.Error(job.ModelName + " — NWC: " + p.NwcNote, ex is NwcError ? null : ex);
                        if (!(ex is NwcError)) RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: " + job.Model.Path + " — шаг «" + p.Step + "»", ex);
                    }
                }

                if (wantRvt)
                {
                    p.Rvt = NwcState.Working;
                    try
                    {
                        if (discard && wantNwc)
                        {
                            var first = doc;
                            doc = null;
                            Step("Закрытие (RVT — без рабочих наборов)", () => first.Close(false));
                            Step(audit ? "Открытие с проверкой, без рабочих наборов" : "Открытие без рабочих наборов", () => Open(true));
                        }
                        // все связи — как «Диспетчер связей → Удалить» (разработчик, 08.11.2026), какие бы настройки удаления ни были у NWC
                        Step("RVT: удаление связей", () => RemoveExternal(doc, AllExternal(job.Export), "RAS: удалить связи перед сохранением RVT"));
                        Step("RVT: очистка от неиспользуемого", () => Purge(doc));
                        long size = 0;
                        Step("RVT: сохранение", () => size = SaveRvt(doc, job.RvtPath));
                        rvtSaved = true;
                        p.Rvt = NwcState.Done;
                        p.RvtNote = NwcHistory.SizeText(size);
                    }
                    catch (Exception ex) when (wantNwc && p.Nwc == NwcState.Done)
                    {
                        p.Rvt = NwcState.Failed;
                        p.RvtNote = ex is NwcError ? ex.Message : $"{p.Step}: {ex.Message}";
                        _j.Error(job.ModelName + " — RVT: " + p.RvtNote, ex is NwcError ? null : ex);
                        if (!(ex is NwcError)) RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка RVT: " + job.Model.Path + " — шаг «" + p.Step + "»", ex);
                    }
                }
                p.State = p.Nwc == NwcState.Failed || p.Rvt == NwcState.Failed || p.Resave == NwcState.Failed ? NwcState.Failed : NwcState.Done;
                p.Step = p.State == NwcState.Done ? "выгружена" : "ошибка";
                p.Note = Notes(p);
            }
            catch (Exception ex)
            {
                // копия, открытие — общие у NWC и RVT: ошибка — у той и другой (что не сделано)
                var note = ex is NwcError ? ex.Message : $"{p.Step}: {ex.Message}";
                p.State = NwcState.Failed;
                if (wantNwc && p.Nwc != NwcState.Done && p.Nwc != NwcState.Failed)
                {
                    p.Nwc = NwcState.Failed;
                    p.NwcNote = note;
                }
                if (wantRvt && p.Rvt != NwcState.Done && p.Rvt != NwcState.Failed)
                {
                    p.Rvt = NwcState.Failed;
                    p.RvtNote = note;
                }
                if (job.Resave && p.Resave != NwcState.Done && p.Resave != NwcState.Failed)
                {
                    p.Resave = NwcState.Failed;
                    p.ResaveNote = note;
                }
                p.Note = Notes(p) ?? note;
                _j.Error(job.ModelName + " — " + note, ex is NwcError ? null : ex);
                if (!(ex is NwcError)) RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: " + job.Model.Path + " — шаг «" + p.Step + "»", ex);
            }
            finally
            {
                if (doc != null)
                {
                    try { Step("Закрытие", () => doc.Close(false)); }
                    catch (Exception ex) { _j.Error(job.ModelName + " — модель не закрылась: " + ex.Message, ex); }
                }
                // папка «…_backup» центрального файла — заказчику не нужна (Revit заведёт её снова при первой синхронизации)
                if (rvtSaved)
                {
                    var backup = BackupDir(job.RvtPath);
                    try
                    {
                        if (Directory.Exists(backup)) Directory.Delete(backup, true);
                    }
                    catch (Exception ex)
                    {
                        _j.Line("   папка резервных копий RVT не удалена: " + backup + " — " + ex.Message);
                    }
                }
                // копия наперёд — удалить и освободить место следующей
                if (ahead != null) _prefetch.Done(ahead);
                try
                {
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                }
                catch (Exception ex)
                {
                    _j.Line("   временная папка не удалена: " + temp + " — " + ex.Message);
                }
                if (_revitMessages > RevitMessagesPerModel) _j.Line($"   сообщений Revit всего: {_revitMessages} (в журнале — первые {RevitMessagesPerModel})");
            }
        }

        /// <summary>Итог модели строкой: только NWC — как было («12 МБ»); с RVT, пересохранением — «NWC: …; RVT: …; пересохранение: …».</summary>
        private static string Notes(NwcProgress p)
        {
            if (p.Rvt == null && p.Resave == null) return p.NwcNote;
            var parts = new List<string>();
            if (p.Resave != null) parts.Add("пересохранение: " + (p.ResaveNote ?? StateText(p.Resave.Value)));
            if (p.Nwc != null) parts.Add("NWC: " + (p.NwcNote ?? StateText(p.Nwc.Value)));
            if (p.Rvt != null) parts.Add("RVT: " + (p.RvtNote ?? StateText(p.Rvt.Value)));
            return string.Join("; ", parts);
        }

        /// <summary>Резервная копия модели перед пересохранением — нетронутая копия, в папку запуска (дата и время), прежние не трогаются.</summary>
        private void Backup(string copy, NwcJob job)
        {
            var dir = job.BackupDir ?? (job.Export.Resave ?? new ResaveOptions()).BackupDir;
            Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, ExportNodes.FileName(job.Model.Path));
            var tmp = target + ".ras-tmp";
            File.Copy(copy, tmp, true);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            _j.Line($"   резервная копия: {target}, {NwcHistory.SizeText(new FileInfo(target).Length)}");
        }

        /// <summary>
        /// Пересохранить на место (разработчик, 08.11.2026): «Сохранить как» туда же, откуда модель взята (Revit Server или папка), —
        /// с рабочими наборами центральной, «Сжать» — по настройкам; затем «Освободить всё».
        /// </summary>
        private void ResaveTo(Document doc, NwcJob job)
        {
            var resave = job.Export.Resave ?? new ResaveOptions();
            var central = doc.IsWorkshared;
            var options = new SaveAsOptions { OverwriteExistingFile = true, Compact = resave.Compact };
            if (central) options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
            var server = ExportNodes.IsServerPath(job.Model.Path);
            long before = !server && File.Exists(job.Model.Path) ? new FileInfo(job.Model.Path).Length : -1;
            doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(job.Model.Path), options);
            if (central) Relinquish(doc);
            var after = !server && File.Exists(job.Model.Path) ? new FileInfo(job.Model.Path).Length : -1;
            _j.Line($"   пересохранена: {job.Model.Path}" + (central ? " — центральная, с рабочими наборами" : "") + (resave.Compact ? ", сжата" : "") +
                    (before > 0 && after > 0 ? $"; было {NwcHistory.SizeText(before)}, стало {NwcHistory.SizeText(after)}" : ""));
        }

        /// <summary>«Освободить всё»; не вышло — синхронизация с освобождением; что осталось занятым — несостыковка.</summary>
        private void Relinquish(Document doc)
        {
            try
            {
                WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), new TransactWithCentralOptions());
            }
            catch (Exception ex)
            {
                _j.Line("   «Освободить всё» не вышло (" + ex.Message + ") — синхронизация с освобождением");
                var sync = new SynchronizeWithCentralOptions { Comment = "RAS: освободить всё", SaveLocalBefore = false, SaveLocalAfter = false };
                sync.SetRelinquishOptions(new RelinquishOptions(true));
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), sync);
            }
            var owned = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Where(w => w.IsEditable).Select(w => w.Name).ToList();
            if (owned.Count == 0) _j.Line("   рабочие наборы освобождены");
            else _j.Warn($"{doc.Title}: рабочие наборы не освобождены ({owned.Count}): " + string.Join(", ", owned.Take(10)));
        }

        /// <summary>Удалить все связи (RVT для заказчика): Revit, IFC, DWG и CAD, облака точек — настройки удаления NWC не важны.</summary>
        private static ExportSettings AllExternal(ExportSettings s)
        {
            var all = s.Clone();
            all.RemoveRevitLinks = all.RemoveCad = all.RemovePointClouds = all.RemoveIfc = true;
            return all;
        }

        /// <summary>Чем очищается модель: в Revit 2026 — стандартная очистка, в Revit 2023 её в API нет — неиспользуемые семейства и типы.</summary>
#if REVIT2023
        private const string PurgeKind = "Revit 2023: неиспользуемые семейства и типы — «Советник по производительности»";
        private static readonly Guid PurgeRule = new Guid("e8c63650-70b7-435a-9010-ec97660c1bda");
#else
        private const string PurgeKind = "стандартная «Очистить от неиспользуемых»";
#endif

        /// <summary>
        /// «Очистить от неиспользуемых» три раза подряд (разработчик, 08.11.2026: «три раза — самый оптимальный вариант»): удалённое
        /// освобождает следующее (тип — семейство, семейство — материал). Ничего не нашлось — дальше не повторять.
        /// </summary>
        private void Purge(Document doc)
        {
            var total = 0;
            for (var pass = 1; pass <= 3; pass++)
            {
                var ids = Unused(doc);
                int deleted = 0, failed = 0;
                if (ids.Count > 0)
                    using (var t = new Transaction(doc, "RAS: очистить от неиспользуемого"))
                    {
                        t.Start();
                        foreach (var id in ids)
                        {
                            // удалён вместе с другим (тип — с семейством)
                            if (doc.GetElement(id) == null) continue;
                            try
                            {
                                doc.Delete(id);
                                deleted++;
                            }
                            catch (Exception)
                            {
                                failed++;
                            }
                        }
                        var status = t.Commit();
                        if (status != TransactionStatus.Committed) throw new NwcError("очистка не применилась: " + status);
                    }
                total += deleted;
                _j.Line($"   очистка {pass}: неиспользуемых {ids.Count}, удалено {deleted}" + (failed > 0 ? $", не удалось {failed}" : ""));
                if (ids.Count == 0) break;
            }
            _j.Line($"   очищено всего: {total} ({PurgeKind})");
        }

        private static List<ElementId> Unused(Document doc)
        {
#if REVIT2023
            var adviser = PerformanceAdviser.GetPerformanceAdviser();
            var rule = adviser.GetAllRuleIds().FirstOrDefault(r => r.Guid == PurgeRule);
            if (rule == null) throw new NwcError("в Revit нет правила «Неиспользуемые семейства и типы» — очистка не выполнена");
            return adviser.ExecuteRules(doc, new List<PerformanceAdviserRuleId> { rule })
                .SelectMany(m => m.GetFailingElements()).Distinct().ToList();
#else
            return doc.GetUnusedElements(new HashSet<ElementId>()).ToList();
#endif
        }

        /// <summary>
        /// «Сохранить как» RVT: прежний (и его «…_backup») — удалить; с рабочими наборами — новый центральный файл и «Освободить всё»;
        /// без них — обычный файл. Размер RVT.
        /// </summary>
        private long SaveRvt(Document doc, string target)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            if (File.Exists(target))
            {
                File.Delete(target);
                _j.Line("   прежний RVT удалён: " + target);
            }
            var backup = BackupDir(target);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            var central = doc.IsWorkshared;
            var options = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 };
            if (central) options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
            doc.SaveAs(target, options);
            if (central) Relinquish(doc);
            if (!File.Exists(target)) throw new NwcError("Revit не записал RVT (сохранение прошло без ошибки, а файла нет)");
            var size = new FileInfo(target).Length;
            _j.Line($"   RVT записан: {target} — {NwcHistory.SizeText(size)}, " + (central ? "центральный файл с рабочими наборами" : "без рабочих наборов"));
            return size;
        }

        private static string BackupDir(string rvt) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(rvt) ?? "", System.IO.Path.GetFileNameWithoutExtension(rvt) + "_backup");

        /// <summary>Шаг: имя — в окно хода и журнал, время и память — в журнал; ошибка шага — дальше, к модели.</summary>
        private void Step(string name, Action action)
        {
            _current.Step = name;
            _changed(_current);
            _j.StepStart(name);
            var clock = Stopwatch.StartNew();
            var ok = false;
            try
            {
                action();
                ok = true;
            }
            finally
            {
                _j.StepDone(name, clock.Elapsed.TotalSeconds, ok, Memory());
            }
        }

        /// <summary>
        /// До копии: NWC не занята, папка есть и доступна; RVT — то же, уже лежит и «не перезаписывать» — RVT пропускается; дата модели
        /// и отбор «отредактированные». true — пропустить модель.
        /// </summary>
        private bool Before(NwcJob job)
        {
            // NWC у модели не выгружается (только RVT и / или пересохранение) — проверки NWC не нужны
            var rvtOnly = job.Off;
            if (job.Resave)
            {
                try
                {
                    var dir = job.BackupDir ?? (job.Export.Resave ?? new ResaveOptions()).BackupDir;
                    try
                    {
                        Directory.CreateDirectory(dir);
                    }
                    catch (Exception ex)
                    {
                        throw new NwcError($"папка резервных копий недоступна: {dir} — {ex.Message}");
                    }
                }
                catch (NwcError ex) when (!job.Off || job.RvtPath != null)
                {
                    _current.Resave = NwcState.Failed;
                    _current.ResaveNote = ex.Message;
                    _j.Error(job.ModelName + " — пересохранение: " + ex.Message);
                }
            }
            if (job.RvtPath != null)
            {
                try
                {
                    if (CheckRvt(job)) return true;
                }
                catch (NwcError ex) when (!job.Off)
                {
                    // RVT не выйдет — NWC всё равно выгружается
                    _current.Rvt = NwcState.Failed;
                    _current.RvtNote = ex.Message;
                    _j.Error(job.ModelName + " — RVT: " + ex.Message);
                }
            }
            var exists = rvtOnly ? job.RvtPath != null && File.Exists(job.RvtPath) : File.Exists(job.NwcPath);
            DateTime? nwcDate = null;
            if (!rvtOnly)
            {
                var folder = System.IO.Path.GetDirectoryName(job.NwcPath);
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    throw new NwcError($"папка NWC недоступна: {folder} — {ex.Message}");
                }
            }
            if (!rvtOnly && exists)
            {
                var info = new FileInfo(job.NwcPath);
                nwcDate = info.LastWriteTime;
                _j.Line($"   прежняя NWC: {NwcHistory.SizeText(info.Length)}, {info.LastWriteTime:dd.MM.yyyy HH:mm}");
                try
                {
                    using (new FileStream(job.NwcPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException)
                {
                    throw new NwcError("NWC занята — открыта в Navisworks? Закройте её и повторите");
                }
            }
            else if (!rvtOnly)
                _j.Line("   прежней NWC нет");

            var modified = _modified = ModelDate(job.Model);
            _j.Line(modified == null ? "   дата изменения модели не известна" : $"   модель изменена: {modified:dd.MM.yyyy HH:mm}");
            var last = rvtOnly ? null : _history.Last(job.NwcPath);
            if (last?.Signature != null && last.Signature != NwcRunPlan.Signature(job))
                _j.Line("   вид или параметры выгрузки поменялись с прошлого раза — NWC выгружается заново");
            var fresh = rvtOnly ? null : NwcRunPlan.Fresh(job, modified, nwcDate, last);
            if (fresh != null) _j.Line("   модель не менялась с прошлой выгрузки, выгружается: " + fresh);
            if (!NwcRunPlan.Due(job, modified, exists))
            {
                _current.Step = _current.Note = $"не изменялась в срок ({modified:dd.MM.yyyy}) — {(rvtOnly ? "RVT актуален" : "NWC актуальна")}";
                _j.Line("   пропущена по отбору «отредактированные»: " + _current.Note);
                return true;
            }
            return false;
        }

        /// <summary>
        /// RVT до копии: папка есть и доступна; RVT уже лежит — «не перезаписывать» (общие настройки): RVT пропускается (true — и вся
        /// модель, если NWC у неё не выгружается), иначе — не занят ли.
        /// </summary>
        private bool CheckRvt(NwcJob job)
        {
            var folder = System.IO.Path.GetDirectoryName(job.RvtPath);
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                throw new NwcError($"папка RVT недоступна: {folder} — {ex.Message}");
            }
            if (!File.Exists(job.RvtPath))
            {
                _j.Line("   прежнего RVT нет");
                return false;
            }
            var info = new FileInfo(job.RvtPath);
            _j.Line($"   прежний RVT: {NwcHistory.SizeText(info.Length)}, {info.LastWriteTime:dd.MM.yyyy HH:mm}");
            if (!(job.Export.Rvt ?? new RvtOptions()).Overwrite)
            {
                _current.Rvt = NwcState.Skipped;
                _current.RvtNote = "RVT уже есть — не перезаписывается (общие настройки → «Экспорт RVT»)";
                _j.Line("   " + _current.RvtNote);
                if (!job.Off) return false;
                _current.Step = _current.Note = _current.RvtNote;
                return true;
            }
            try
            {
                using (new FileStream(job.RvtPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            catch (IOException)
            {
                throw new NwcError("RVT занят — открыт в Revit? Закройте его и повторите");
            }
            return false;
        }

        /// <summary>Дата изменения модели: Revit Server — сведения о модели (служба её версии), файл — дата файла; нет — null.</summary>
        private DateTime? ModelDate(ExportModel model)
        {
            try
            {
                if (!ExportNodes.IsServerPath(model.Path))
                    return File.Exists(model.Path) ? File.GetLastWriteTime(model.Path) : (DateTime?)null;
                var parts = model.Path.Trim().Substring("RSN://".Length).Split('/');
                var year = model.Year > 0 ? model.Year : _year;
                // ожидание — в своём потоке: продолжение не ищет занятый поток Revit
                var task = Task.Run(() => RevitServerClient.ModelInfoAsync(parts[0], year, parts.Skip(1).Take(parts.Length - 2).ToList(), parts[parts.Length - 1]));
                if (!task.Wait(TimeSpan.FromSeconds(20)))
                {
                    _j.Line("   Revit Server не ответил за 20 с — дата модели не известна");
                    return null;
                }
                return task.Result.DateModified;
            }
            catch (Exception ex)
            {
                _j.Line("   дата модели не получена: " + (ex.InnerException ?? ex).Message);
                return null;
            }
        }

        /// <summary>В копии до открытия: связи Revit и CAD — «не загружать» (TransmissionData). Не вышло — связи удалятся после открытия.</summary>
        private void DoNotLoadLinks(ModelPath copy)
        {
            try
            {
                var data = TransmissionData.ReadTransmissionData(copy);
                if (data == null)
                {
                    _j.Line("   в файле нет сведений о внешних ссылках");
                    return;
                }
                var off = new List<string>();
                var kinds = new Dictionary<string, int>();
                foreach (var id in data.GetAllExternalFileReferenceIds())
                {
                    var r = data.GetLastSavedReferenceData(id);
                    var kind = r.ExternalFileReferenceType.ToString();
                    kinds[kind] = kinds.TryGetValue(kind, out var n) ? n + 1 : 1;
                    if (r.ExternalFileReferenceType != ExternalFileReferenceType.RevitLink && r.ExternalFileReferenceType != ExternalFileReferenceType.CADLink) continue;
                    data.SetDesiredReferenceData(id, r.GetPath(), r.PathType, false);
                    off.Add(ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetPath()));
                }
                data.IsTransmitted = true;
                TransmissionData.WriteTransmissionData(copy, data);
                _j.Line($"   внешних ссылок: {(kinds.Count == 0 ? "нет" : string.Join(", ", kinds.Select(k => k.Key + " " + k.Value)))}; «не загружать»: {off.Count}");
                foreach (var path in off.Take(15)) _j.Line("     · " + path);
                if (off.Count > 15) _j.Line($"     · … ещё {off.Count - 15}");
            }
            catch (Exception ex)
            {
                _j.Warn("связи не отключены до открытия (загрузятся и будут удалены после): " + ex.Message);
            }
        }

        /// <summary>Удалить связи Revit, IFC, DWG и другие CAD (связанные и импортированные), облака точек — по настройкам строки; что осталось — несостыковка.</summary>
        private void RemoveExternal(Document doc, ExportSettings s, string transaction = "RAS: удалить внешнее перед выгрузкой NWC")
        {
            // номера и имена — до удаления: удалили тип связи DWG — Revit удалил и его вставки, и имя удалённого элемента уже не
            // прочитать (04.11.2026: «The referenced object is not valid» — выгрузка падала на этом шаге)
            var revit = Snap(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)));
            var groups = new List<(string Name, bool On, List<(ElementId Id, string Name)> Items)>
            {
                ("связи Revit", s.RemoveRevitLinks, revit.Where(e => !IsIfc(e.Name)).ToList()),
                ("связи IFC", s.RemoveIfc, revit.Where(e => IsIfc(e.Name)).ToList()),
                ("DWG и CAD — связи", s.RemoveCad, Snap(new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)))),
                ("DWG и CAD — вставки", s.RemoveCad, Snap(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))),
                ("облака точек", s.RemovePointClouds, Snap(new FilteredElementCollector(doc).OfClass(typeof(PointCloudType)))),
            };
            using (var t = new Transaction(doc, transaction))
            {
                t.Start();
                foreach (var g in groups)
                {
                    if (g.Items.Count == 0) continue;
                    var names = string.Join(", ", g.Items.Select(e => e.Name).Distinct().Take(10)) + (g.Items.Count > 10 ? ", …" : "");
                    if (!g.On)
                    {
                        _j.Line($"   {g.Name}: {g.Items.Count} — не удаляются (настройка): {names}");
                        continue;
                    }
                    int deleted = 0, already = 0, failed = 0;
                    foreach (var e in g.Items)
                    {
                        // уже удалён вместе с другим (вставка — с типом связи DWG, вложенная связь — с родительской)
                        if (doc.GetElement(e.Id) == null)
                        {
                            already++;
                            continue;
                        }
                        try
                        {
                            doc.Delete(e.Id);
                            deleted++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            _j.Line($"     не удалён «{e.Name}»: {ex.Message}");
                        }
                    }
                    _j.Line($"   {g.Name}: {g.Items.Count} — удалено {deleted}{(already > 0 ? $", уже удалены вместе со связью {already}" : "")}" +
                            $"{(failed > 0 ? $", не удалось {failed}" : "")}: {names}");
                }
                var status = t.Commit();
                if (status != TransactionStatus.Committed) throw new NwcError("удаление связей не применилось: " + status);
            }
            var left = new List<string>();
            if (s.RemoveRevitLinks || s.RemoveIfc)
            {
                var n = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount();
                if (n > 0) left.Add($"экземпляров связей Revit: {n}");
            }
            if (s.RemoveCad)
            {
                var n = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).GetElementCount();
                if (n > 0) left.Add($"вставок DWG и CAD: {n}");
            }
            if (left.Count > 0) _j.Warn($"{doc.Title}: после удаления остались — " + string.Join(", ", left));
        }

        /// <summary>
        /// Пустой вид — откуда (08.11.2026: у разработчика в Revit на этом виде всё есть, у плагина — 0 ещё до удаления связей): по
        /// очереди, каждый раз с откатом — показать скрытые фильтры, все рабочие наборы, убрать фильтр стадий, отключить шаблон; в
        /// журнал — сколько элементов модели тогда видно, и сколько их в модели вообще. Модель не меняется (всё откатывается).
        /// </summary>
        private void Probe(Document doc, View3D view)
        {
            var total = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Count(IsModelElement);
            _j.Line($"   в модели элементов модели: {total}; пробую, откуда пустой вид (с откатом):");
            void Try(string what, Action<View> change)
            {
                try
                {
                    using (var tx = new Transaction(doc, "RAS: проверка вида"))
                    {
                        tx.Start();
                        var rules = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId) as View ?? view : view;
                        change(rules);
                        doc.Regenerate();
                        var n = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements().Count(IsModelElement);
                        tx.RollBack();
                        _j.Line($"     {what}: видно {n}");
                    }
                }
                catch (Exception ex)
                {
                    _j.Line($"     {what}: не вышло — {ex.Message}");
                }
            }
            Try("показать скрытые фильтры", r =>
            {
                foreach (var f in r.GetFilters()) if (!r.GetFilterVisibility(f)) r.SetFilterVisibility(f, true);
            });
            if (doc.IsWorkshared)
                Try("показать все рабочие наборы", r =>
                {
                    foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    {
                        r.SetWorksetVisibility(w.Id, WorksetVisibility.Visible);
                        if (r.Id != view.Id) view.SetWorksetVisibility(w.Id, WorksetVisibility.Visible);
                    }
                });
            Try("фильтр стадий — «Показать все»", r =>
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                    .FirstOrDefault(f => f.Name.IndexOf("все", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("all", StringComparison.OrdinalIgnoreCase) >= 0);
                var p = r.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                if (all != null && p != null && !p.IsReadOnly) p.Set(all.Id);
                else throw new InvalidOperationException("фильтра «Показать все» нет или он задан не здесь");
            });
            Try("без шаблона вида", r => view.ViewTemplateId = ElementId.InvalidElementId);
            try
            {
                var p = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                var w = doc.IsWorkshared
                    ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Count(x => !x.IsVisibleByDefault)
                    : 0;
                _j.Line($"     фильтр стадий вида: {p?.AsValueString() ?? "—"}; рабочих наборов «не видны по умолчанию»: {w}; подрезка вида: " +
                        (view.CropBoxActive ? "включена" : "нет"));
            }
            catch (Exception) { /* не важно */ }
        }

        private static readonly List<ElementId> ServiceCategories = new[]
            { BuiltInCategory.OST_Cameras, BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_IOSSketchGrid, BuiltInCategory.OST_Viewers }
            .Select(c => new ElementId(c)).ToList();

        private static bool IsModelElement(Element e) =>
            e.Category != null && e.Category.CategoryType == CategoryType.Model && !ServiceCategories.Contains(e.Category.Id);

        /// <summary>Сколько элементов модели (не камер, рамок, сетки) видно на 3D-виде с этим именем; вида нет — −1.</summary>
        private static int CountVisibleModel(Document doc, string name)
        {
            try
            {
                var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name.Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (view == null) return -1;
                var service = new[] { BuiltInCategory.OST_Cameras, BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_IOSSketchGrid, BuiltInCategory.OST_Viewers }
                    .Select(c => new ElementId(c)).ToList();
                return new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements()
                    .Count(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !service.Contains(e.Category.Id));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static bool IsIfc(string name) => (name ?? "").IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Номера и имена элементов — снимком, пока все целы.</summary>
        private static List<(ElementId Id, string Name)> Snap(FilteredElementCollector collector) =>
            collector.ToElements().Select(e => (e.Id, e.Name ?? "")).ToList();

        /// <summary>
        /// Вид для выгрузки (разработчик, 03.11.2026: «просто проверяем свой вид Navisworks перед выгрузкой»): найти по имени, в журнал —
        /// шаблон, стадия, детализация, скрытые категории, видимых элементов по категориям; рамка 3D и временное скрытие —
        /// выключить (несостыковка). Нет вида или в нём пусто — ошибка модели.
        /// </summary>
        private View3D PrepareView(Document doc, NwcJob job, NwcHistory.Entry entry)
        {
            if (job.Export.Nwc.Scope == NwcScope.Model)
            {
                _j.Line("   экспорт: целый проект — вид не нужен");
                entry.Elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
                return null;
            }
            var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate).ToList();
            var name = (job.View ?? "").Trim();
            var view = views.FirstOrDefault(v => v.Name == name);
            if (view == null)
            {
                view = views.FirstOrDefault(v => string.Equals(v.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (view != null) _j.Warn($"{job.ModelName}: вид называется «{view.Name}», а задан «{name}» — взят он");
            }
            if (view == null)
                throw new NwcError($"нет 3D-вида «{name}». 3D-виды модели: " +
                                   (views.Count == 0 ? "нет" : string.Join(", ", views.Select(v => "«" + v.Name + "»").Take(15)) + (views.Count > 15 ? ", …" : "")));

            var template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId)?.Name : null;
            _j.Line($"   вид «{view.Name}»: шаблон {(template == null ? "нет" : "«" + template + "»")}; стадия: {view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsValueString() ?? "—"}; " +
                    $"детализация: {view.DetailLevel}; стиль: {view.DisplayStyle}");
            var hidden = new List<string>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c.CategoryType != CategoryType.Model) continue;
                try
                {
                    if (view.CanCategoryBeHidden(c.Id) && view.GetCategoryHidden(c.Id)) hidden.Add(c.Name);
                }
                catch (Exception)
                {
                    // категория без видимости в виде — пропустить
                }
            }
            _j.Line($"   скрытые категории модели ({hidden.Count}): {(hidden.Count == 0 ? "нет" : string.Join(", ", hidden.OrderBy(x => x)))}");

            var fixes = new List<string>();
            if (view.IsSectionBoxActive) fixes.Add("рамка 3D-вида");
            if (view.IsTemporaryHideIsolateActive()) fixes.Add("временное скрытие / изоляция");
            if (fixes.Count > 0)
            {
                using (var t = new Transaction(doc, "RAS: вид для выгрузки NWC"))
                {
                    t.Start();
                    if (view.IsSectionBoxActive) view.IsSectionBoxActive = false;
                    if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    t.Commit();
                }
                _j.Warn($"{job.ModelName}: в виде «{view.Name}» было включено — {string.Join(", ", fixes)}; выключено перед выгрузкой");
            }

            // что в шаблоне выключено целиком — в журнал (пустой вид: откуда)
            var off = new List<string>();
            var rules = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId) as View ?? view : view;
            try
            {
                if (rules.AreModelCategoriesHidden) off.Add("категории модели — выключены целиком («Показывать категории модели» снято)");
            }
            catch (Exception) { /* у вида нет такой настройки */ }
            try
            {
                if (doc.IsWorkshared)
                {
                    var hiddenSets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                        .Where(w => rules.GetWorksetVisibility(w.Id) == WorksetVisibility.Hidden).Select(w => w.Name).ToList();
                    if (hiddenSets.Count > 0) off.Add($"рабочие наборы скрыты ({hiddenSets.Count}): " + string.Join(", ", hiddenSets.Take(10)) + (hiddenSets.Count > 10 ? ", …" : ""));
                }
            }
            catch (Exception) { /* без рабочих наборов */ }
            try
            {
                var hiddenFilters = rules.GetFilters().Where(f => !rules.GetFilterVisibility(f)).Select(f => doc.GetElement(f)?.Name).ToList();
                if (hiddenFilters.Count > 0) off.Add($"фильтры скрывают ({hiddenFilters.Count}): " + string.Join(", ", hiddenFilters.Take(10)) + (hiddenFilters.Count > 10 ? ", …" : ""));
            }
            catch (Exception) { /* фильтров нет */ }
            if (off.Count > 0) _j.Line("   в виде" + (rules != view ? $" (по шаблону «{rules.Name}»)" : "") + ": " + string.Join("; ", off));

            var visible = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements();
            entry.Elements = visible.Count;
            if (visible.Count == 0) throw new NwcError($"в виде «{view.Name}» ничего не видно — NWC была бы пустой");
            var byCategory = visible.GroupBy(e => e.Category?.Name ?? "без категории").OrderByDescending(g => g.Count()).ToList();
            _j.Line($"   видимых элементов: {visible.Count}; по категориям: " +
                    string.Join(", ", byCategory.Take(15).Select(g => g.Key + " " + g.Count())) + (byCategory.Count > 15 ? $", … ещё категорий: {byCategory.Count - 15}" : ""));
            // видны только камеры, рамки вида, сетка — экспортёр Navisworks останавливается окном «Не найдена подходящая геометрия» и
            // ждёт «ОК» (в фоновом Revit — некому; 07.11.2026, модель АР с шаблоном «*02_Navisworks») — до него не доходить
            var service = new[] { BuiltInCategory.OST_Cameras, BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_IOSSketchGrid, BuiltInCategory.OST_Viewers }
                .Select(c => new ElementId(c)).ToList();
            var model = visible.Count(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !service.Contains(e.Category.Id));
            if (model == 0) Probe(doc, view);
            if (model == 0)
                throw new NwcError($"в виде «{view.Name}» не видно ни одного элемента модели — только камеры и рамки" +
                                   (view.ViewTemplateId != ElementId.InvalidElementId ? $"; проверьте шаблон «{doc.GetElement(view.ViewTemplateId)?.Name}» (фильтры, рабочие наборы)" : "") +
                                   " — экспортёр Navisworks на таком виде останавливается");
            return view;
        }

        /// <summary>Параметры экспортёра Navisworks — из «Настроек NWC Exporter» строки.</summary>
        private static NavisworksExportOptions Options(NwcOptions n, View3D view)
        {
            var o = new NavisworksExportOptions
            {
                ExportScope = n.Scope == NwcScope.View ? NavisworksExportScope.View : NavisworksExportScope.Model,
                ExportElementIds = true,
                Parameters = n.Parameters == NwcParameters.None ? NavisworksParameters.None
                    : n.Parameters == NwcParameters.Elements ? NavisworksParameters.Elements : NavisworksParameters.All,
                Coordinates = n.SharedCoordinates ? NavisworksCoordinates.Shared : NavisworksCoordinates.Internal,
                FacetingFactor = n.FacetingFactor,
                ConvertElementProperties = n.ConvertElementProperties,
                ExportUrls = n.ConvertUrls,
                ConvertLights = n.ConvertLights,
                ExportRoomAsAttribute = n.RoomAsAttribute,
                ConvertLinkedCADFormats = n.ConvertLinkedCad,
                ExportLinks = n.ConvertLinkedFiles,
                ExportParts = n.ConvertParts,
                FindMissingMaterials = n.FindMissingMaterials,
                DivideFileIntoLevels = n.DivideIntoLevels,
                ExportRoomGeometry = n.RoomGeometry,
            };
            if (view != null) o.ViewId = view.Id;
            return o;
        }

        private static string OptionsText(NwcOptions n) =>
            $"параметры {n.Parameters}, координаты {(n.SharedCoordinates ? "общие" : "внутренние")}, фасетизация {n.FacetingFactor}, " +
            $"свойства {Yes(n.ConvertElementProperties)}, URL {Yes(n.ConvertUrls)}, свет {Yes(n.ConvertLights)}, помещение как атрибут {Yes(n.RoomAsAttribute)}, " +
            $"связ. CAD {Yes(n.ConvertLinkedCad)}, связ. файлы {Yes(n.ConvertLinkedFiles)}, конструкции {Yes(n.ConvertParts)}, материалы {Yes(n.FindMissingMaterials)}, " +
            $"уровни {Yes(n.DivideIntoLevels)}, геометрия помещений {Yes(n.RoomGeometry)}";

        /// <summary>Новая NWC — на место прежней: сначала рядом временным файлом, затем замена (прежняя не портится, если копия оборвётся).</summary>
        private void Replace(string exported, string target)
        {
            var tmp = target + ".ras-tmp";
            File.Copy(exported, tmp, true);
            var size = new FileInfo(exported).Length;
            if (new FileInfo(tmp).Length != size)
                throw new NwcError("NWC скопировалась не целиком в папку NWC — место на диске? сеть?");
            long before = File.Exists(target) ? new FileInfo(target).Length : -1;
            try
            {
                if (before >= 0) File.Replace(tmp, target, null);
                else File.Move(tmp, target);
            }
            catch (IOException) when (before >= 0)
            {
                // File.Replace на сетевой папке не всегда работает — удалить и переложить
                File.Delete(target);
                File.Move(tmp, target);
            }
            _j.Line($"   записана: {target}" + (before >= 0 ? $" (была {NwcHistory.SizeText(before)})" : " (новая)"));
        }

        // ── окна и предупреждения Revit ──

        /// <summary>Окно Revit во время выгрузки — ответить «ОК» (иначе выгрузка встанет) и записать в журнал: id окна — чтобы научить отвечать иначе.</summary>
        private void OnDialog(object sender, DialogBoxShowingEventArgs e)
        {
            _dialogs++;
            var text = e is TaskDialogShowingEventArgs t ? t.Message : e is MessageBoxShowingEventArgs m ? m.Message : "";
            bool accepted;
            try { accepted = e.OverrideResult(1); }
            catch (Exception) { accepted = false; }
            _j.Line($"   окно Revit «{e.DialogId}»: {OneLine(text)} → «ОК»{(accepted ? "" : " — не принято, окно ждёт человека")}");
        }

        /// <summary>Предупреждения — удалить, ошибки с решением — решить; всё — в журнал (первые RevitMessagesPerModel на модель).</summary>
        private void OnFailures(object sender, FailuresProcessingEventArgs e)
        {
            var fa = e.GetFailuresAccessor();
            var messages = fa.GetFailureMessages();
            if (messages.Count == 0) return;
            var resolved = false;
            foreach (var m in messages)
            {
                _revitMessages++;
                var warning = m.GetSeverity() == FailureSeverity.Warning;
                if (_revitMessages <= RevitMessagesPerModel)
                    _j.Line($"   Revit, {(warning ? "предупреждение" : "ошибка")}: {OneLine(m.GetDescriptionText())} (элементов: {m.GetFailingElementIds().Count})");
                if (warning) fa.DeleteWarning(m);
                else if (m.HasResolutions())
                {
                    fa.ResolveFailure(m);
                    resolved = true;
                }
            }
            e.SetProcessingResult(resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue);
        }

        // ── итог ──

        private void Summary(IList<NwcProgress> items)
        {
            _j.Line("════ ИТОГ");
            _j.Line($"выгружено {items.Count(p => p.State == NwcState.Done)}, с ошибкой {items.Count(p => p.State == NwcState.Failed)}, " +
                    $"пропущено {items.Count(p => p.State == NwcState.Skipped)} из {items.Count}; всего {NwcHistory.Duration(_j.Elapsed.TotalSeconds)}; " +
                    $"окон Revit отвечено: {_dialogs}");
            foreach (var p in items)
                _j.Line($"  {StateText(p.State),-12} {NwcHistory.Duration(p.Time.TotalSeconds),8}  {p.Job.ModelName}{(p.Note == null ? "" : " — " + p.Note)}");
            var slow = items.Where(p => p.State == NwcState.Done).OrderByDescending(p => p.Time).Take(3).ToList();
            if (slow.Count > 0) _j.Line("дольше всех: " + string.Join(", ", slow.Select(p => $"{p.Job.ModelName} {NwcHistory.Duration(p.Time.TotalSeconds)}")));
            _j.Line("время по шагам:");
            foreach (var line in _j.StepTotals().TrimEnd().Split('\n')) _j.Line(line.TrimEnd('\r'));
            _j.Line($"ошибок: {_j.Errors.Count}");
            foreach (var e in _j.Errors) _j.Line("  ✗ " + OneLine(e));
            _j.Line($"несостыковок: {_j.Warnings.Count}");
            foreach (var w in _j.Warnings) _j.Line("  ! " + OneLine(w));
            _j.Line($"память Revit в конце: {Memory()}; временная папка: {TempRoot}");
        }

        public static string StateText(NwcState s) =>
            s == NwcState.Done ? "выгружена" : s == NwcState.Failed ? "ошибка" : s == NwcState.Skipped ? "пропущена" : s == NwcState.Working ? "идёт" : "ждёт";

        /// <summary>Название запуска: строки дерева от задачи до запущенной — «Арман Кала 7 оч. › S1_КЖ».</summary>
        public static string Title(ExportTask task) => string.Join(" › ", TreePath(task));

        private static IEnumerable<string> TreePath(ExportTask task)
        {
            var names = new List<string>();
            for (var t = task; t != null; t = t.Parent) names.Insert(0, t.Name);
            return names;
        }

        private static string Memory()
        {
            using (var p = Process.GetCurrentProcess())
                return "память Revit " + NwcHistory.SizeText(p.PrivateMemorySize64);
        }

        private static string FreeSpace(string folder)
        {
            try { return NwcHistory.SizeText(new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))).AvailableFreeSpace); }
            catch (Exception) { return "не известно"; }
        }

        private static string Yes(bool b) => b ? "да" : "нет";
        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
