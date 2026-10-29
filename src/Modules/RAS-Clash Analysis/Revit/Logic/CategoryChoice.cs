using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Категория модели или подкатегория для окна «Категории элементов» — из открытой модели.</summary>
    public sealed class CategoryItem
    {
        public string Key { get; set; }
        public string Name { get; set; }
        /// <summary>Ключ категории у подкатегории; null — сама категория.</summary>
        public string Parent { get; set; }
    }

    /// <summary>
    /// «Категории элементов» (разработчик, 29.10.2026): какие категории модели не видны в виде коллизии — в исходном файле
    /// (открытая модель) и в связанном (связи). Ключ категории — имя BuiltInCategory («OST_Walls»): одинаков в любой модели и
    /// на любом языке Revit; подкатегории — «ключ категории|имя подкатегории» (BuiltInCategory у них часто нет). Хранятся только
    /// скрытые — по умолчанию видно всё. Подкатегорию Revit скрывает сразу и в модели, и в связях: скрыта хоть в одном столбце —
    /// скрыта. Без Revit API — проверяется тестами.
    /// </summary>
    public static class CategoryChoice
    {
        public const char SubSeparator = '|';

        public static string SubKey(string parent, string name) => parent + SubSeparator + name;

        public static bool IsSub(string key) => key != null && key.IndexOf(SubSeparator) >= 0;

        public static bool SubHidden(ViewSettings s, string key) => s.HiddenHost.Contains(key) || s.HiddenLink.Contains(key);

        /// <summary>Подпись у кнопки «Категории элементов…».</summary>
        public static string Summary(ViewSettings s) =>
            s.HiddenHost.Count == 0 && s.HiddenLink.Count == 0
                ? "Все категории видны"
                : $"Скрыто: в исходном файле — {s.HiddenHost.Count}, в связанном — {s.HiddenLink.Count}";

        /// <summary>Пустые и повторы — прочь, по порядку (файл настроек читается и сравнивается глазами).</summary>
        public static List<string> Clean(IEnumerable<string> keys) =>
            (keys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())
                .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Файл «Категорий элементов» — передать настроенное коллеге (разработчик, 29.10.2026: «импорт и экспорт, формат любой
    /// удобный»): JSON, только скрытые категории, с пометкой, что это за файл.
    /// </summary>
    [DataContract]
    public sealed class CategoryFile
    {
        public const string Mark = "RAS-Clash Analysis: категории вида";

        [DataMember(Name = "kind", Order = 0)] public string Kind { get; set; }
        [DataMember(Name = "version", Order = 1)] public int Version { get; set; }
        [DataMember(Name = "hiddenHost", Order = 2)] public List<string> HiddenHost { get; set; }
        [DataMember(Name = "hiddenLink", Order = 3)] public List<string> HiddenLink { get; set; }

        public static void Save(string path, IEnumerable<string> host, IEnumerable<string> link)
        {
            var file = new CategoryFile { Kind = Mark, Version = 1, HiddenHost = CategoryChoice.Clean(host), HiddenLink = CategoryChoice.Clean(link) };
            using (var stream = File.Create(path))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, System.Text.Encoding.UTF8, true, true))
                new DataContractJsonSerializer(typeof(CategoryFile)).WriteObject(writer, file);
        }

        /// <summary>Прочитать; не наш файл или испорчен — InvalidDataException с причиной по-русски.</summary>
        public static CategoryFile Load(string path)
        {
            CategoryFile file;
            try
            {
                using (var stream = File.OpenRead(path))
                    file = (CategoryFile)new DataContractJsonSerializer(typeof(CategoryFile)).ReadObject(stream);
            }
            catch (SerializationException ex)
            {
                throw new InvalidDataException("Файл не читается — это не файл категорий вида.", ex);
            }
            if (file == null || file.Kind != Mark) throw new InvalidDataException("Это не файл категорий вида (его делает «Экспорт…» в окне «Категории элементов»).");
            file.HiddenHost = CategoryChoice.Clean(file.HiddenHost);
            file.HiddenLink = CategoryChoice.Clean(file.HiddenLink);
            return file;
        }
    }
}
