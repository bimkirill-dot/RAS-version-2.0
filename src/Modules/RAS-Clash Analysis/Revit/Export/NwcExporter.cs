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
    }

    /// <summary>Понятная ошибка модели — в журнал и окно без стека (стек — у непредвиденных, и они же — в ras.log).</summary>
    internal sealed class NwcError : Exception
    {
        public NwcError(string message) : base(message) { }
    }

    /// <summary>
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
        private NwcProgress _current;
        private Action<NwcProgress> _changed;
        private int _revitMessages;
        private int _dialogs;

        public NwcExporter(UIApplication ui, ExportJournal journal, int year)
        {
            _ui = ui;
            _app = ui.Application;
            _j = journal;
            _year = year;
        }

        /// <summary>Шапка журнала: где, кто, что и с какими настройками; план — что выгружается и что нет.</summary>
        public void Header(ExportTask start, ExportSettings common, List<NwcJob> jobs, List<string> notes)
        {
            _j.Line($"Выгрузка NWC — «{string.Join(" › ", TreePath(start))}»");
            _j.Line($"Revit {_app.VersionNumber} ({_app.SubVersionNumber}, сборка {_app.VersionBuild}), {Environment.UserName} на {Environment.MachineName}, " +
                    $"плагин {typeof(NwcExporter).Assembly.GetName().Version}, .NET {Environment.Version}");
            _j.Line($"Временная папка: {TempRoot}; свободно на диске: {FreeSpace(TempRoot)}");
            _j.Line($"Удалять перед выгрузкой: связи Revit {Yes(common.RemoveRevitLinks)}, DWG и CAD {Yes(common.RemoveCad)}, облака точек {Yes(common.RemovePointClouds)}, IFC {Yes(common.RemoveIfc)} (общие; строка может задать свои)");
            _j.Line($"NWC к выгрузке: {jobs.Count(x => x.Skip == null)}, пропускаются: {jobs.Count(x => x.Skip != null)}");
            var i = 0;
            foreach (var job in jobs)
            {
                i++;
                _j.Line($"  {i,3}. {job.ModelName}  →  {job.NwcPath}");
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
                for (var i = 0; i < items.Count; i++)
                {
                    var p = items[i];
                    if (cancelled())
                    {
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
                _ui.DialogBoxShowing -= OnDialog;
                _app.FailuresProcessing -= OnFailures;
                _current = null;
                try { _history.Save(HistoryPath); }
                catch (Exception ex) { _j.Line("история выгрузок не записана: " + ex.Message); }
                Summary(items);
            }
        }

        private void One(NwcProgress p)
        {
            var job = p.Job;
            if (job.Skip != null)
            {
                p.State = NwcState.Skipped;
                p.Step = p.Note = job.Skip;
                _j.Line("   пропущена: " + job.Skip);
                return;
            }
            p.State = NwcState.Working;
            var temp = System.IO.Path.Combine(TempRoot, DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var copy = System.IO.Path.Combine(temp, ExportNodes.FileName(job.Model.Path));
            var nwcName = ExportNodes.FileName(job.NwcPath);
            nwcName = nwcName.EndsWith(".nwc", StringComparison.OrdinalIgnoreCase) ? nwcName.Substring(0, nwcName.Length - 4) : nwcName;
            Document doc = null;
            var entry = new NwcHistory.Entry { Nwc = job.NwcPath, At = DateTime.Now };
            var clock = Stopwatch.StartNew();
            try
            {
                var skip = false;
                Step("Проверка до открытия", () => skip = Before(job));
                if (skip)
                {
                    p.State = NwcState.Skipped;
                    return;
                }

                Step("Копия модели", () =>
                {
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

                Step("Связи — не загружать", () => DoNotLoadLinks(copyPath));

                Step("Открытие", () =>
                {
                    var options = new OpenOptions { Audit = false };
                    if (workshared)
                    {
                        options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                        options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                    }
                    doc = _app.OpenDocumentFile(copyPath, options);
                    if (doc == null) throw new NwcError("Revit не открыл модель");
                    _j.Line($"   открыта: «{doc.Title}»; элементов: {new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount()}" +
                            (doc.IsWorkshared ? $"; рабочих наборов: {new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Count()}" : ""));
                });

                Step("Удаление внешнего", () => RemoveExternal(doc, job.Export));

                View3D view = null;
                Step("Вид", () => view = PrepareView(doc, job, entry));

                var exported = System.IO.Path.Combine(temp, nwcName + ".nwc");
                Step("Выгрузка NWC", () =>
                {
                    doc.Export(temp, nwcName, Options(job.Export.Nwc, view));
                    if (!File.Exists(exported)) throw new NwcError("Revit не создал NWC (выгрузка прошла без ошибки, а файла нет)");
                    entry.Size = new FileInfo(exported).Length;
                    if (entry.Size == 0) throw new NwcError("NWC пустая (0 байт)");
                    _j.Line($"   NWC: {NwcHistory.SizeText(entry.Size)}");
                });

                Step("Замена NWC", () => Replace(exported, job.NwcPath));

                entry.Seconds = clock.Elapsed.TotalSeconds;
                foreach (var note in NwcHistory.Compare(_history.Last(job.NwcPath), entry)) _j.Warn(job.ModelName + ": " + note);
                _history.Put(entry);
                p.State = NwcState.Done;
                p.Step = "выгружена";
                p.Note = NwcHistory.SizeText(entry.Size);
            }
            catch (Exception ex)
            {
                p.State = NwcState.Failed;
                p.Note = ex is NwcError ? ex.Message : $"{p.Step}: {ex.Message}";
                _j.Error(job.ModelName + " — " + p.Note, ex is NwcError ? null : ex);
                if (!(ex is NwcError)) RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: " + job.Model.Path + " — шаг «" + p.Step + "»", ex);
            }
            finally
            {
                if (doc != null)
                {
                    try { Step("Закрытие", () => doc.Close(false)); }
                    catch (Exception ex) { _j.Error(job.ModelName + " — модель не закрылась: " + ex.Message, ex); }
                }
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

        /// <summary>До копии: NWC не занята, папка есть и доступна, дата модели и отбор «отредактированные». true — пропустить.</summary>
        private bool Before(NwcJob job)
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
            var exists = File.Exists(job.NwcPath);
            DateTime? nwcDate = null;
            if (exists)
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
            else
                _j.Line("   прежней NWC нет");

            var modified = ModelDate(job.Model);
            _j.Line(modified == null ? "   дата изменения модели не известна" : $"   модель изменена: {modified:dd.MM.yyyy HH:mm}");
            if (exists && modified != null && nwcDate != null && nwcDate > modified)
                _j.Line("   прежняя NWC новее модели — модель с прошлой выгрузки не менялась");
            if (!NwcRunPlan.Due(job, modified, exists))
            {
                _current.Step = _current.Note = $"не изменялась в срок ({modified:dd.MM.yyyy}) — NWC актуальна";
                _j.Line("   пропущена по отбору «отредактированные»: " + _current.Note);
                return true;
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
        private void RemoveExternal(Document doc, ExportSettings s)
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
            using (var t = new Transaction(doc, "RAS: удалить внешнее перед выгрузкой NWC"))
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

            var visible = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements();
            entry.Elements = visible.Count;
            if (visible.Count == 0) throw new NwcError($"в виде «{view.Name}» ничего не видно — NWC была бы пустой");
            var byCategory = visible.GroupBy(e => e.Category?.Name ?? "без категории").OrderByDescending(g => g.Count()).ToList();
            _j.Line($"   видимых элементов: {visible.Count}; по категориям: " +
                    string.Join(", ", byCategory.Take(15).Select(g => g.Key + " " + g.Count())) + (byCategory.Count > 15 ? $", … ещё категорий: {byCategory.Count - 15}" : ""));
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
