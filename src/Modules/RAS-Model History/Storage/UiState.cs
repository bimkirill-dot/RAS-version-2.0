using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>Столбец таблицы журнала: виден ли, ширина, место.</summary>
    public sealed class ColumnState
    {
        public string Key { get; set; }
        public bool Visible { get; set; }
        /// <summary>Ширина в пикселях; 0 — по умолчанию (в том числе «на всё свободное место»).</summary>
        public double Width { get; set; }
        public int Index { get; set; }
    }

    /// <summary>Уровень сортировки: поле, направление, заголовок группы.</summary>
    public sealed class SortLevelState
    {
        public string Field { get; set; }
        public bool Descending { get; set; }
        public bool Header { get; set; }
    }

    /// <summary>
    /// Вид панели, который запоминается между сеансами: столбцы, размер подробностей, группировка и т. п.
    /// Хранится отдельно от настроек: %AppData%\RAS_v_2.0\RAS-Model History\ui.json.
    /// </summary>
    public sealed class UiState
    {
        /// <summary>Версия формата: 2 — время «ДД.ММ.ГГГГ ЧЧ:ММ», статус значком и текстом, сортировка как в Revit.</summary>
        public const int CurrentVersion = 2;
        public int Version { get; set; }
        public List<ColumnState> Columns { get; set; } = new List<ColumnState>();
        /// <summary>«Сортировка и группировка»; пусто — по времени, новые сверху.</summary>
        public List<SortLevelState> Sort { get; set; } = new List<SortLevelState>();
        public double DetailsWidth { get; set; } = 380;
        public double DetailsHeight { get; set; } = 240;
        /// <summary>Записи одной операции Revit — одной строкой.</summary>
        public bool GroupByOperation { get; set; } = true;
        /// <summary>Список сам показывает историю того, что выделено в Revit.</summary>
        public bool FollowSelection { get; set; }
        /// <summary>Открытая вкладка подробностей: 0 — «Запись», 1 — «Было → стало», 2 — «Все параметры», 3 — «История».</summary>
        public int DetailsTab { get; set; } = 1;

        /// <summary>Журнал открывается в отдельном окне (по умолчанию) или в панели Revit — кнопка в окне / панели.</summary>
        public bool OpenInWindow { get; set; } = true;

        /// <summary>Отдельное окно журнала: положение и размер (ширина 0 — ещё не открывали), развёрнуто ли на весь экран.</summary>
        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }

        /// <summary>
        /// 3D-вид изменений: цвета, остальная модель, тени (null — по умолчанию). Одним объектом: прежние отдельные
        /// поля (ChangesCreatedColor…) больше не читаются — их значения давали слишком бледный вид.
        /// </summary>
        public ChangesViewStyle ChangesStyle { get; set; }

        public static string FilePath => Path.Combine(DataFolders.UserDataDir, "ui.json");

        public static UiState Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<UiState>(File.ReadAllText(FilePath), SpyJson.Pretty);
                    if (s != null)
                    {
                        s.Columns ??= new List<ColumnState>();
                        s.Sort ??= new List<SortLevelState>();
                        s.DetailsWidth = Clamp(s.DetailsWidth, 260, 3000, 380);
                        s.DetailsHeight = Clamp(s.DetailsHeight, 80, 3000, 240);
                        if (s.DetailsTab < 0 || s.DetailsTab > 3) s.DetailsTab = 1;
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Log("UI state load failed", ex);
            }
            return new UiState();
        }

        public void Save()
        {
            try
            {
                Version = CurrentVersion;
                Directory.CreateDirectory(DataFolders.UserDataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SpyJson.Pretty));
            }
            catch (Exception ex)
            {
                Diag.Log("UI state save failed", ex);
            }
        }

        private static double Clamp(double v, double min, double max, double fallback) =>
            double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? fallback : Math.Max(min, Math.Min(max, v));
    }
}
