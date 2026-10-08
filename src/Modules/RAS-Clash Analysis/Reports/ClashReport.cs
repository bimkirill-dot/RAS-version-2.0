using System;
using System.Collections.Generic;

namespace RASv2.ClashAnalysis.Reports
{
    /// <summary>
    /// Отчёт Navisworks Clash Detective (XML), как он есть: проверки, коллизии, элементы. Без Revit и без базы —
    /// общий для сайта и моста в Revit. Читает <see cref="ClashReportReader"/>.
    /// </summary>
    public sealed class ClashReport
    {
        /// <summary>Файл отчёта (полный путь); снимки коллизий — относительно его папки.</summary>
        public string FilePath { get; set; }
        /// <summary>Единицы координат и допуска: «m», «cm», «mm», «ft», «in».</summary>
        public string Units { get; set; }
        /// <summary>Дата отчёта: самая поздняя дата обнаружения коллизий, если Navisworks её записал, иначе — время файла.</summary>
        public DateTime Date { get; set; }
        /// <summary>Дата взята из самих коллизий (иначе — время изменения файла).</summary>
        public bool DateFromReport { get; set; }
        public List<ClashTest> Tests { get; } = new List<ClashTest>();
    }

    /// <summary>Проверка: пара наборов («КЖ Вертикальные конструкции/МЕР Воздуховоды»).</summary>
    public sealed class ClashTest
    {
        public string Name { get; set; }
        /// <summary>«hard», «hard_conservative», «clearance», «duplicate».</summary>
        public string TestType { get; set; }
        /// <summary>Допуск — в единицах отчёта.</summary>
        public double Tolerance { get; set; }
        /// <summary>Наборы, которые сравнивались (без служебного «lcop_selection_set_tree/»).</summary>
        public string LeftSet { get; set; }
        public string RightSet { get; set; }
        /// <summary>Итог Navisworks: всего, новые, активные, проверенные, подтверждённые, исправленные.</summary>
        public ClashSummary Summary { get; set; } = new ClashSummary();
        public List<ClashResult> Results { get; } = new List<ClashResult>();
    }

    public sealed class ClashSummary
    {
        public int Total { get; set; }
        public int New { get; set; }
        public int Active { get; set; }
        public int Reviewed { get; set; }
        public int Approved { get; set; }
        public int Resolved { get; set; }
    }

    /// <summary>Статус коллизии в Navisworks.</summary>
    public enum NavisworksStatus { Unknown, New, Active, Reviewed, Approved, Resolved }

    /// <summary>Коллизия — одно появление в отчёте.</summary>
    public sealed class ClashResult
    {
        /// <summary>«Конфликт1».</summary>
        public string Name { get; set; }
        /// <summary>Постоянный номер Navisworks: тот же у той же коллизии в следующих прогонах той же проверки.</summary>
        public string Guid { get; set; }
        public NavisworksStatus Status { get; set; }
        /// <summary>Снимок — путь относительно папки отчёта, как в XML («…_files\cd040001.jpg»); null — без снимка.</summary>
        public string ImageHref { get; set; }
        /// <summary>Группа коллизий в Navisworks, если коллизия в группе.</summary>
        public string GroupName { get; set; }
        /// <summary>Точка пересечения — в единицах и координатах Navisworks; null — нет в отчёте.</summary>
        public ClashPoint Point { get; set; }
        /// <summary>Расстояние (глубина пересечения со знаком «−») — в единицах отчёта; null — не включено в отчёт.</summary>
        public double? Distance { get; set; }
        /// <summary>Дата обнаружения; null — не включена в отчёт.</summary>
        public DateTime? Created { get; set; }
        /// <summary>Расположение по осям («Г-4 : 02 Этаж»); null — не включено в отчёт.</summary>
        public string GridLocation { get; set; }
        public string Description { get; set; }
        public string AssignedTo { get; set; }
        public List<ClashElement> Elements { get; } = new List<ClashElement>();
    }

    public sealed class ClashPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    /// <summary>Элемент коллизии — из пути в дереве Navisworks и свойств («Объект Id»).</summary>
    public sealed class ClashElement
    {
        /// <summary>Файл модели в Navisworks («3457_01_01_S8_AR01_01_M3.nwc») — по нему находится модель Revit или её связь.</summary>
        public string ModelFile { get; set; }
        public string Level { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        /// <summary>«Элемент Имя».</summary>
        public string Name { get; set; }
        /// <summary>Номер элемента в Revit (ElementId); уникален только внутри своего файла; null — нет в отчёте.</summary>
        public long? ElementId { get; set; }
        /// <summary>Весь путь в дереве Navisworks.</summary>
        public List<string> Path { get; } = new List<string>();
    }
}
