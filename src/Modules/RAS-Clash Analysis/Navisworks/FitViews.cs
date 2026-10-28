using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using RASv2.ClashAnalysis.Navisworks.Logic;
using NwApp = Autodesk.Navisworks.Api.Application;

namespace RASv2.ClashAnalysis.Navisworks
{
    /// <summary>
    /// Вход сборки — его вызывает загрузчик для Navisworks (src\Navisworks\Loader) по имени: Execute(команда, сборка, папка, канал).
    /// Сигнатуру менять только вместе с загрузчиком.
    /// </summary>
    public static class Entry
    {
        public static int Execute(string command, string buildId, string buildDir, string channel)
        {
            Log.Write("Команда «" + command + "», сборка " + buildId + ", канал «" + channel + "»");
            if (command == "ClashAnalysis.FitViews") return FitViews.Run(channel == "dev");
            MessageBox.Show("Неизвестная команда: " + command, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 0;
        }

        public const string Title = "RAS v. 2.0 — подстроить виды";
    }

    /// <summary>
    /// «Подстроить виды» (DECISIONS.md модуля, «Navisworks: подстроить виды коллизий»): во всех проверках Clash Detective файла
    /// каждой коллизии — вид, где оба элемента видны и не закрывают друг друга (Logic\ViewPick: перебор направлений, оценка по
    /// маленьким картинкам). Вид записывается в саму коллизию — через COM (InwOclTestResult.ViewPoint): в .NET API у коллизии
    /// вид только читается (прежняя попытка, ClashViewFixer v6, 09.2026, на этом остановилась). Прототип: проверить, что вид
    /// записывается и показывается Clash Detective, и сколько занимает.
    /// </summary>
    internal static class FitViews
    {
        private const int ImageWidth = 160, ImageHeight = 120;
        /// <summary>Сборка разработчика — картинки, по которым выбран вид, сохраняются для разбора (первые коллизии).</summary>
        private const int DebugClashes = 5;

        public static int Run(bool debug)
        {
            var doc = NwApp.ActiveDocument;
            if (doc == null || doc.IsClear)
            {
                MessageBox.Show("Откройте файл Navisworks с проверками Clash Detective.", Entry.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var clash = doc.GetClash();
            var results = new List<(ClashTest Test, ClashResult Result)>();
            foreach (var test in clash.TestsData.Tests.OfType<ClashTest>()) Collect(test, test.Children, results);
            if (results.Count == 0)
            {
                MessageBox.Show("В файле нет коллизий в проверках Clash Detective — сначала запустите проверки.", Entry.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var tests = results.Select(r => r.Test.DisplayName).Distinct().Count();
            var all = new Dictionary<string, ClashResult>();
            foreach (var r in results) all[Key(r.Test.DisplayName, r.Result.DisplayName)] = r.Result; // одинаковые имена — последняя
            var com = ComResults(out var comProblem);
            if (com == null) Log.Write("Виды в коллизии не записываются: " + comProblem);
            // прежние виды — чтобы вернуть выбранной коллизии, если её не подбирали (её вид затрёт движение камеры)
            var before = new Dictionary<string, InwNvViewPoint>();
            InwNvViewPoint onScreen = null; // что на экране при запуске — вид выбранной коллизии, если у неё не было своего
            try { onScreen = (InwNvViewPoint)ComApiBridge.State.CurrentView.ViewPoint.Copy(); }
            catch (Exception) { /* нет вида */ }
            if (com != null)
                foreach (var kv in com)
                    try { if (kv.Value.ViewPoint != null) before[kv.Key] = (InwNvViewPoint)kv.Value.ViewPoint.Copy(); }
                    catch (Exception) { /* у коллизии нет вида */ }

            // в Clash Detective выбрана коллизия (вкладка «Результаты») — её вид он перезапишет движением камеры: не начинать
            // (разработчик, 23.10.2026: «если выйти из всех видов — всё работает хорошо»; запускать — с вкладки «Правила»)
            if (com != null)
            {
                var selected = FindSelected(doc, com, before, onScreen);
                if (selected != null)
                {
                    WarningDialog.Show("Нельзя запускать на вкладке «Результаты»",
                        $"Сейчас в Clash Detective выбрана коллизия «{selected.Replace("\u001f", " / ")}». " +
                        "Перейдите на вкладку «Правила» и запустите снова. Ничего не изменено.",
                        "Понятно");
                    return 0;
                }
            }
            // уже подобранные (и подправленные вручную) — с сохранённым видом: при повторном запуске их не трогать (разработчик, 23.10.2026)
            var fresh = results.Where(r => !r.Result.HasSavedViewpoint).ToList();
            // предупреждение — заметное, красное (разработчик, 23.10.2026: «чтобы пользователь точно не пропустил»)
            var answer = WarningDialog.Show("Нельзя запускать на вкладке «Результаты»",
                $"Запускайте с вкладки «Правила» Clash Detective.{Environment.NewLine}{Environment.NewLine}" +
                $"Коллизий: {results.Count} в {tests} {(tests % 10 == 1 && tests % 100 != 11 ? "проверке" : "проверках")}, из них без сохранённого вида — {fresh.Count}. Уже подобранные и подправленные " +
                $"вручную виды можно не трогать.{Environment.NewLine}{Environment.NewLine}Сохраните файл до этого — так можно вернуть прежние виды.",
                $"Только без вида ({fresh.Count})", $"Все заново ({results.Count})", "Отмена");
            if (answer != 0 && answer != 1) return 0;
            if (answer == 0) results = fresh;
            if (results.Count == 0)
            {
                MessageBox.Show("У всех коллизий вид уже есть — подбирать нечего.", Entry.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var debugDir = debug ? Path.Combine(RasPaths.Local, "Navisworks", "Разбор", DateTime.Now.ToString("yyyy-MM-dd HHmmss")) : null;
            // направление на экране при запуске — так Clash Detective покажет коллизию без сохранённого вида
            var launch = doc.CurrentViewpoint.CreateCopy();
            _stats = new Stats();

            doc.CurrentSelection.Clear();
            // подобранные виды — ещё раз в конце: пока камера ходит по коллизиям, Clash Detective («Автообновление» вида) переписывает
            // выбранную в нём коллизию каждым её движением (разработчик, 23.10.2026: «не обрабатывает вид, на котором сейчас сидишь»)
            var chosen = new Dictionary<string, (InwOclTestResult Target, InwNvViewPoint View, ModelItem First, ModelItem Second)>();
            var candidates = ViewPick.Candidates();
            int fitted = 0, written = 0, skipped = 0, kept = 0;
            var watch = Stopwatch.StartNew();
            var progress = NwApp.BeginProgress(Entry.Title, "Подбираю виды коллизий…");
            try
            {
                for (var i = 0; i < results.Count; i++)
                {
                    if (!progress.Update(i / (double)results.Count)) break; // «Отмена»
                    var (test, result) = results[i];
                    try
                    {
                        InwOclTestResult target = null;
                        if (com != null && !com.TryGetValue(Key(test.DisplayName, result.DisplayName), out target))
                            Log.Write($"Коллизия не найдена через COM: «{test.DisplayName}» / «{result.DisplayName}»");
                        var name = $"«{test.DisplayName}» / «{result.DisplayName}»";
                        var dump = debugDir != null && i < DebugClashes ? Path.Combine(debugDir, $"{i + 1:00} {Safe(result.DisplayName)}") : null;
                        // свой вид — сохранённый у коллизии (выбрано «Все заново»)
                        var own = result.HasSavedViewpoint && before.TryGetValue(Key(test.DisplayName, result.DisplayName), out var saved) ? saved : null;
                        if (!FitOne(doc, name, result, candidates, target, own, launch, dump, out var view, out var first, out var second, out var how)) { skipped++; continue; }
                        fitted++;
                        _stats.Count(how);
                        if (how == HowOwn) kept++;
                        if (view != null) { written++; chosen[Key(test.DisplayName, result.DisplayName)] = (target, view, first, second); }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Log.Write($"Коллизия «{test.DisplayName}» / «{result.DisplayName}» пропущена", ex);
                    }
                }
            }
            finally
            {
                NwApp.EndProgress();
                if (com != null)
                {
                    _stats.Finish.Start();
                    Finish(doc, com, all, chosen, before, onScreen);
                    _stats.Finish.Stop();
                }
            }
            watch.Stop();
            var perClash = fitted > 0 ? watch.Elapsed.TotalSeconds / fitted : 0;
            Log.Write($"Подстроено {fitted} из {results.Count}, записано в коллизии {written}, пропущено {skipped}; " +
                      $"{watch.Elapsed.TotalSeconds:0.0} с, {perClash:0.00} с на коллизию, направлений {candidates.Count}, картинка {ImageWidth}×{ImageHeight}");
            Log.Write("Время: " + _stats.Text(watch.Elapsed));
            var text = new StringBuilder();
            text.AppendLine($"Подстроено видов: {fitted} из {results.Count}.");
            if (kept > 0) text.AppendLine($"Вид уже был хороший — оставлен: {kept}.");
            if (com == null) text.AppendLine("Записать виды в коллизии не удалось: " + comProblem);
            else text.AppendLine($"Записано в коллизии: {written}.");
            if (skipped > 0) text.AppendLine($"Пропущено: {skipped} (подробности — в журнале).");
            if (debugDir != null) Log.Write("Картинки для разбора: " + debugDir);
            text.AppendLine($"Время: {watch.Elapsed.TotalSeconds:0} с ({perClash:0.0} с на коллизию).");
            MessageBox.Show(text.ToString(), Entry.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static void Collect(ClashTest test, SavedItemCollection items, List<(ClashTest, ClashResult)> output)
        {
            foreach (var item in items)
            {
                if (item is ClashResultGroup group) Collect(test, group.Children, output);
                else if (item is ClashResult result) output.Add((test, result));
            }
        }

        /// <summary>
        /// Завершение — из-за «Автообновления» вида Clash Detective: в коллизию, выбранную в нём, он записывает каждое изменение
        /// камеры и видимости (проверки разработчика 23.10.2026: «не обрабатывает вид, на котором сидишь», «где-то скрывает остальное,
        /// где-то нет»). Поэтому: (1) найти выбранную — сдвинуть камеру и посмотреть, в какую коллизию сдвиг записан; нет такой —
        /// всё (обычно так: запуск — с вкладки «Правила»); (2) всем
        /// остальным — вид ещё раз, со «скрыть остальное» каждой (запись через COM камеру не двигает; при записи сохраняется и
        /// скрытое); (3) выбранную — последней: её «скрыть остальное», её вид и камера — и так и оставить на экране. Её не подбирали —
        /// вернуть ей прежний вид.
        /// </summary>
        private static void Finish(Document doc, Dictionary<string, InwOclTestResult> com, Dictionary<string, ClashResult> all,
            Dictionary<string, (InwOclTestResult Target, InwNvViewPoint View, ModelItem First, ModelItem Second)> chosen,
            Dictionary<string, InwNvViewPoint> before, InwNvViewPoint onScreen)
        {
            var selected = FindSelected(doc, com, null, null);
            // никакая не выбрана — «Автообновление» ничего не переписывало: виды уже записаны при подборе, второй раз не нужно
            // (ускорение, 28.10.2026: «скрыть остальное» на каждую коллизию — второй раз)
            if (selected == null) return;
            Log.Write("Выбранная в Clash Detective коллизия (в конце): " + selected.Replace("\u001f", " / "));

            foreach (var kv in chosen)
            {
                if (kv.Key == selected) continue;
                try
                {
                    var isolation = Isolate(doc, kv.Value.First, kv.Value.Second);
                    try { kv.Value.Target.ViewPoint = kv.Value.View; }
                    finally { isolation.Undo(doc); }
                }
                catch (Exception ex) { Log.Write("Вид не записан повторно", ex); }
            }

            try
            {
                var state = ComApiBridge.State;
                if (chosen.TryGetValue(selected, out var own))
                {
                    Isolate(doc, own.First, own.Second); // и оставить: на экране — выбранная коллизия, как её покажет Clash Detective
                    state.CurrentView.ViewPoint = (InwNvViewPoint)own.View.Copy();
                    own.Target.ViewPoint = own.View;
                }
                else if (before.TryGetValue(selected, out var old) || (old = onScreen) != null)
                {
                    var r = all.TryGetValue(selected, out var result) ? result : null;
                    var first = r == null ? null : ItemOf(r.Item1, r.CompositeItem1, r.Selection1);
                    var second = r == null ? null : ItemOf(r.Item2, r.CompositeItem2, r.Selection2);
                    if (first != null && second != null) Isolate(doc, first, second);
                    state.CurrentView.ViewPoint = (InwNvViewPoint)old.Copy();
                    com[selected].ViewPoint = old;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Выбранная коллизия не восстановлена", ex);
            }
        }

        /// <summary>
        /// Какая коллизия выбрана в Clash Detective (его «Автообновление» вида пишет в неё движение камеры): сдвинуть камеру и
        /// посмотреть, в какую коллизию сдвиг записан; нет такой — null. restoreView / restoreCamera — вернуть выбранной её вид и
        /// камеру (проверка перед началом не должна ничего менять); null — не возвращать (в конце работы).
        /// </summary>
        private static string FindSelected(Document doc, Dictionary<string, InwOclTestResult> com, Dictionary<string, InwNvViewPoint> restoreView,
            InwNvViewPoint restoreCamera)
        {
            string selected = null;
            try
            {
                var probe = doc.CurrentViewpoint.CreateCopy();
                var p = probe.Position;
                var mark = new Point3D(p.X + 1234.5, p.Y - 987.25, p.Z + 321.75);
                probe.Position = mark;
                doc.CurrentViewpoint.CopyFrom(probe);
                foreach (var kv in com)
                {
                    try
                    {
                        var at = kv.Value.ViewPoint?.Camera?.Position;
                        if (at != null && Math.Abs(at.data1 - mark.X) < 1e-3 && Math.Abs(at.data2 - mark.Y) < 1e-3 && Math.Abs(at.data3 - mark.Z) < 1e-3)
                        {
                            selected = kv.Key;
                            break;
                        }
                    }
                    catch (Exception) { /* вида нет */ }
                }
                if (restoreCamera != null)
                {
                    ComApiBridge.State.CurrentView.ViewPoint = (InwNvViewPoint)restoreCamera.Copy();
                    if (selected != null)
                        com[selected].ViewPoint = restoreView != null && restoreView.TryGetValue(selected, out var old) ? old : restoreCamera;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Выбранная коллизия не определена", ex);
            }
            return selected;
        }

        private static string Key(string test, string result) => (test ?? "") + "\u001f" + (result ?? "");

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "").Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
        }

        /// <summary>Коллизии через COM — для записи вида: «проверка / коллизия» → результат. Нет Clash Detective в COM — null.</summary>
        private static Dictionary<string, InwOclTestResult> ComResults(out string problem)
        {
            problem = null;
            try
            {
                var state = ComApiBridge.State;
                InwOpClashElement element = null;
                foreach (var plugin in state.Plugins()) if (plugin is InwOpClashElement e) { element = e; break; }
                if (element == null) { problem = "нет Clash Detective в COM"; return null; }
                var map = new Dictionary<string, InwOclTestResult>();
                foreach (InwOclClashTest test in element.Tests())
                    foreach (InwOclTestResult result in test.results())
                        map[Key(test.name, result.name)] = result;
                return map;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                Log.Write("COM Clash Detective недоступен", ex);
                return null;
            }
        }

        private const string HowOwn = "свой вид", HowLaunch = "вид с экрана", HowFirst = "привычные ракурсы", HowAll = "все направления";

        /// <summary>
        /// Подобрать вид одной коллизии и записать его в коллизию (target, через COM) — пока остальное скрыто: Clash Detective
        /// сохраняет в виде и видимость (без этого на видах была вся модель — проверка разработчика 23.10.2026). Быстрее
        /// (разработчик, 28.10.2026: «изначально анализировать вид; проходит проверку — пропускать»): сначала — вид, который у
        /// коллизии уже есть (own — сохранённый; нет его — как покажет Clash Detective: направление с экрана при запуске, место
        /// коллизии); оба элемента и так видны (ViewPick.Good) — направления не перебираются, сохранённый вид не трогается. Иначе —
        /// 8 привычных ракурсов; лучший годится — остальные не смотрятся. На каждый вид — четыре картинки: оба элемента, только
        /// первый, только второй, ни одного (ViewPick.Visible); без перекраски: подсветка Clash Detective и оси модели её
        /// перебивали. how — чем кончилось (HowOwn — вид оставлен как был, не записан). Нет элементов — false.
        /// </summary>
        private static bool FitOne(Document doc, string name, ClashResult result, List<ViewCandidate> candidates, InwOclTestResult target,
            InwNvViewPoint own, Viewpoint launch, string dump, out InwNvViewPoint saved, out ModelItem first, out ModelItem second, out string how)
        {
            saved = null;
            how = null;
            // элемент коллизии: Item — сам элемент; нет его — составной элемент или первый из выбора
            first = ItemOf(result.Item1, result.CompositeItem1, result.Selection1);
            second = ItemOf(result.Item2, result.CompositeItem2, result.Selection2);
            if (first == null || second == null)
            {
                Log.Write($"{name}: пропущена — нет {(first == null ? "первого" : "второго")} элемента (элемент коллизии не найден в модели: " +
                          $"Item {(first == null ? result.Item1 : result.Item2) != null}, Composite {(first == null ? result.CompositeItem1 : result.CompositeItem2) != null}, " +
                          $"Selection {(first == null ? result.Selection1 : result.Selection2)?.Count ?? 0})");
                return false;
            }
            if (dump != null) Directory.CreateDirectory(dump);
            var zone = ViewPick.Zone(BoxOf(first.BoundingBox()), BoxOf(second.BoundingBox()));
            var zoom = new BoundingBox3D(new Point3D(zone.MinX, zone.MinY, zone.MinZ), new Point3D(zone.MaxX, zone.MaxY, zone.MaxZ));
            var hide = new[]
            {
                new ModelItemCollection { second },        // скрыть, чтобы остался первый
                new ModelItemCollection { first },
                new ModelItemCollection { first, second },
            };
            _stats.Isolate.Start();
            var isolation = Isolate(doc, first, second);
            _stats.Isolate.Stop();
            try
            {
                // свой вид
                if (own != null) ComApiBridge.State.CurrentView.ViewPoint = (InwNvViewPoint)own.Copy();
                else
                {
                    var start = launch.CreateCopy();
                    start.ZoomBox(zoom);
                    doc.CurrentViewpoint.CopyFrom(start);
                }
                var look = Measure(doc, hide, dump == null ? null : Path.Combine(dump, "00 свой"));
                if (look.Good)
                {
                    how = own != null ? HowOwn : HowLaunch;
                    Log.Write($"{name}: {how} годится — {look}");
                    if (own == null) Save(target, out saved);
                    return true;
                }

                Viewpoint best = null;
                var bestScore = double.NegativeInfinity;
                var bestIndex = -1;
                var bestGood = false;
                var scores = new StringBuilder($" свой:{look}");
                for (var k = 0; k < candidates.Count; k++)
                {
                    // привычные ракурсы посмотрены, лучший годится — остальные не нужны
                    if (k == ViewPick.FirstRound && bestGood) { how = HowFirst; break; }
                    var c = candidates[k];
                    var vp = doc.CurrentViewpoint.CreateCopy();
                    vp.AlignDirection(new Vector3D(c.X, c.Y, c.Z));
                    vp.AlignUp(c.Elevation > 80 ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1));
                    vp.ZoomBox(zoom);
                    doc.CurrentViewpoint.CopyFrom(vp);
                    var m = Measure(doc, hide, dump == null ? null : Path.Combine(dump, $"{k + 1:00} {c.Elevation:0}°"));
                    var score = ViewPick.Score(m.FirstSeen, m.FirstAll, m.SecondSeen, m.SecondAll, m.Total, c.Preference);
                    scores.Append($" {k + 1}:{m}={score:0.00}");
                    if (score > bestScore) { bestScore = score; best = vp; bestIndex = k; bestGood = m.Good; }
                }
                how = how ?? HowAll;
                Log.Write($"{name}: лучший — {bestIndex + 1} ({candidates[bestIndex].Elevation:0}°), {how}; видно из своих точек первого·второго = оценка:{scores}");
                doc.CurrentViewpoint.CopyFrom(best);
                Save(target, out saved);
                return true;
            }
            finally
            {
                _stats.Isolate.Start();
                isolation.Undo(doc);
                _stats.Isolate.Stop();
            }
        }

        /// <summary>Записать вид с экрана в коллизию (через COM; нет её — ничего).</summary>
        private static void Save(InwOclTestResult target, out InwNvViewPoint saved)
        {
            saved = null;
            if (target == null) return;
            _stats.Write.Start();
            try
            {
                saved = (InwNvViewPoint)ComApiBridge.State.CurrentView.ViewPoint.Copy();
                target.ViewPoint = saved;
            }
            finally
            {
                _stats.Write.Stop();
            }
        }

        /// <summary>Сколько видно каждого элемента на виде с экрана (ViewPick.Visible).</summary>
        private sealed class Look
        {
            public int FirstSeen, FirstAll, SecondSeen, SecondAll, Total;
            public bool Good => ViewPick.Good(FirstSeen, FirstAll, SecondSeen, SecondAll, Total);
            public override string ToString() => $"{FirstSeen}/{FirstAll}·{SecondSeen}/{SecondAll}";
        }

        /// <summary>Четыре картинки вида с экрана — оба, только первый, только второй, ни одного (hide — что скрыть) — и что видно.</summary>
        private static Look Measure(Document doc, ModelItemCollection[] hide, string dump)
        {
            var both = Render(doc, null, dump == null ? null : dump + " оба.png");
            var a = Render(doc, hide[0], dump == null ? null : dump + " первый.png");
            var b = Render(doc, hide[1], dump == null ? null : dump + " второй.png");
            var empty = Render(doc, hide[2], dump == null ? null : dump + " пусто.png");
            ViewPick.Visible(both, a, b, empty, out var firstSeen, out var secondSeen, out var firstAll, out var secondAll);
            return new Look { FirstSeen = firstSeen, FirstAll = firstAll, SecondSeen = secondSeen, SecondAll = secondAll, Total = both.Length };
        }

        /// <summary>Картинка вида (на время — скрыть hide); пиксели — 0xRRGGBB. Сборка разработчика — сохранить для разбора (file).</summary>
        private static int[] Render(Document doc, ModelItemCollection hide, string file)
        {
            _stats.Render.Start();
            _stats.Images++;
            if (hide != null) doc.Models.SetHidden(hide, true);
            try
            {
                using (var image = doc.ActiveView.GenerateImage(ImageGenerationStyle.Scene, ImageWidth, ImageHeight))
                {
                    if (file != null) image.Save(file, ImageFormat.Png);
                    return Pixels(image);
                }
            }
            finally
            {
                if (hide != null) doc.Models.SetHidden(hide, false);
                _stats.Render.Stop();
            }
        }

        /// <summary>
        /// На что ушло время (замер, разработчик 28.10.2026) — в журнал: «скрыть остальное», картинки, запись видов, завершение;
        /// чем кончился подбор (свой вид, вид с экрана, привычные ракурсы, все направления).
        /// </summary>
        private sealed class Stats
        {
            public readonly Stopwatch Isolate = new Stopwatch(), Render = new Stopwatch(), Write = new Stopwatch(), Finish = new Stopwatch();
            public int Images;
            private readonly Dictionary<string, int> _how = new Dictionary<string, int>();

            public void Count(string how)
            {
                if (how == null) return;
                _how[how] = _how.TryGetValue(how, out var n) ? n + 1 : 1;
            }

            public string Text(TimeSpan total)
            {
                var other = total - Isolate.Elapsed - Render.Elapsed - Write.Elapsed - Finish.Elapsed;
                return $"скрыть остальное {Isolate.Elapsed.TotalSeconds:0.0} с, картинки {Render.Elapsed.TotalSeconds:0.0} с ({Images} шт., " +
                       $"{(Images > 0 ? Render.Elapsed.TotalMilliseconds / Images : 0):0} мс), запись видов {Write.Elapsed.TotalSeconds:0.0} с, " +
                       $"завершение {Finish.Elapsed.TotalSeconds:0.0} с, прочее (камера и др.) {other.TotalSeconds:0.0} с; " +
                       string.Join(", ", _how.Select(kv => kv.Key + " — " + kv.Value));
            }
        }

        private static Stats _stats = new Stats();

        private static ModelItem ItemOf(ModelItem item, ModelItem composite, ModelItemCollection selection) =>
            item ?? composite ?? selection?.FirstOrDefault();

        /// <summary>Что изменено «скрыть остальное» — чтобы вернуть как было.</summary>
        private sealed class Isolation
        {
            public ModelItemCollection Hidden = new ModelItemCollection();
            public ModelItemCollection Shown = new ModelItemCollection();

            public void Undo(Document doc)
            {
                if (Hidden.Count > 0) doc.Models.SetHidden(Hidden, false);
                if (Shown.Count > 0) doc.Models.SetHidden(Shown, true);
            }
        }

        /// <summary>
        /// «Скрыть остальное»: скрыть всё, кроме двух элементов и их родителей, а их самих показать, если они скрыты (например,
        /// «скрыть остальные» Clash Detective для другой, выбранной коллизии).
        /// </summary>
        private static Isolation Isolate(Document doc, ModelItem first, ModelItem second)
        {
            var result = new Isolation();
            var keep = new HashSet<ModelItem>();
            foreach (var x in first.AncestorsAndSelf) keep.Add(x);
            foreach (var x in second.AncestorsAndSelf) keep.Add(x);
            foreach (var x in keep) if (x.IsHidden) result.Shown.Add(x);
            void Walk(ModelItem node)
            {
                foreach (var child in node.Children)
                {
                    if (child.Equals(first) || child.Equals(second)) continue;
                    if (keep.Contains(child)) Walk(child);
                    else if (!child.IsHidden) result.Hidden.Add(child);
                }
            }
            foreach (var root in doc.Models.RootItems)
            {
                if (keep.Contains(root)) Walk(root);
                else if (!root.IsHidden) result.Hidden.Add(root);
            }
            if (result.Shown.Count > 0) doc.Models.SetHidden(result.Shown, false);
            if (result.Hidden.Count > 0) doc.Models.SetHidden(result.Hidden, true);
            return result;
        }

        private static Box3 BoxOf(BoundingBox3D b) => new Box3(b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);

        /// <summary>Пиксели картинки — 0xRRGGBB построчно.</summary>
        private static int[] Pixels(Bitmap image)
        {
            var result = new int[image.Width * image.Height];
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (var y = 0; y < data.Height; y++)
                {
                    var row = y * data.Stride;
                    for (var x = 0; x < data.Width; x++)
                    {
                        var p = row + x * 3; // BGR
                        result[y * data.Width + x] = (bytes[p + 2] << 16) | (bytes[p + 1] << 8) | bytes[p];
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
            return result;
        }
    }

    /// <summary>Журнал — общий с загрузчиком: %LocalAppData%\RAS_v_2.0\Logs\navisworks.log.</summary>
    internal static class Log
    {
        public static string Path => System.IO.Path.Combine(RasPaths.Logs, "navisworks.log");

        public static void Write(string text, Exception ex = null)
        {
            try
            {
                Directory.CreateDirectory(RasPaths.Logs);
                File.AppendAllText(Path, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " [Виды коллизий] " + text + Environment.NewLine +
                                         (ex != null ? ex + Environment.NewLine : ""), new UTF8Encoding(false));
            }
            catch (Exception) { /* журнал не пишется — не повод ломать Navisworks */ }
        }
    }
}
