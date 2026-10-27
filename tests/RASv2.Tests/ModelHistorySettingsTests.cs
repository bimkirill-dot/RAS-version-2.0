using System;
using System.Collections.Generic;
using RASv2.ModelHistory.Storage;

namespace RASv2.Tests
{
    /// <summary>RAS-Model History: настройки — исходные из сборки, общие с сервера, личная тема (SpySettings.Compose).</summary>
    internal static class ModelHistorySettingsTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Built = "{ \"TrackParameters\": true, \"MaxRecordsPerTransaction\": 5000, \"DefaultPeriodDays\": 7, \"RecordNoise\": false }";

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("общие поверх исходных; чего нет в общих — из сборки", () =>
            {
                var s = SpySettings.Compose(Built, "{ \"recordnoise\": true, \"DefaultPeriodDays\": 30, \"JournalFolder\": \"\\\\\\\\srv\\\\Журналы\" }", null);
                Check(s.RecordNoise && s.DefaultPeriodDays == 30, "из общих (имена без учёта регистра)");
                Check(s.TrackParameters && s.MaxRecordsPerTransaction == 5000, "остальное — из сборки");
                Check(s.JournalFolder == @"\\srv\Журналы", "папка журналов: " + s.JournalFolder);
            });
            yield return Case("личное — только тема: прежний личный файл со всеми полями общих не перебивает", () =>
            {
                var oldPersonal = "{ \"RecordNoise\": false, \"DefaultPeriodDays\": 1, \"TrackParameters\": false, \"UiTheme\": \"Dark\", \"LogFolder\": \"\\\\server\\BIM\" }";
                var s = SpySettings.Compose(Built, "{ \"RecordNoise\": true }", oldPersonal);
                Check(s.UiTheme == "Dark", "тема — личная");
                Check(s.RecordNoise && s.TrackParameters && s.DefaultPeriodDays == 7, "общие и исходные — на месте");
                var t = SpySettings.Compose(Built, "{ \"UiTheme\": \"Light\" }", null);
                Check(t.UiTheme == "Auto", "тема в общем файле не действует");
            });
            yield return Case("испорченная часть пропускается, остальное работает", () =>
            {
                var s = SpySettings.Compose(Built, "{ \"RecordNoise\": tru", "{ \"UiTheme\": \"Light\" }");
                Check(!s.RecordNoise && s.UiTheme == "Light" && s.MaxRecordsPerTransaction == 5000, "общие испорчены — исходные и тема");
                Check(!SpySettings.IsValid("{ \"RecordNoise\": tru") && SpySettings.IsValid("{ /* комментарий */ \"RecordNoise\": true, }"), "IsValid");
                Check(SpySettings.Compose(null, null, null).MaxRecordsPerTransaction == 5000, "ничего нет — значения по умолчанию");
            });
            yield return Case("общий файл: всё, кроме темы, — и читается обратно", () =>
            {
                var s = new SpySettings { RecordNoise = true, DefaultPeriodDays = 14, JournalFolder = @"\\srv\Журналы", UiTheme = "Dark", ChangedBy = "Полетаев К. (Poletayev_BIM)", ChangedAt = "27.10.2026 10:00" };
                var json = s.ToTeamJson();
                Check(!json.Contains("UiTheme") && json.Contains("Полетаев"), "без темы, имя читаемо: " + json);
                var back = SpySettings.Compose(Built, json, s.ToPersonalJson());
                Check(back.RecordNoise && back.DefaultPeriodDays == 14 && back.JournalFolder == @"\\srv\Журналы" && back.UiTheme == "Dark" &&
                      back.ChangedBy == s.ChangedBy && back.ChangedAt == s.ChangedAt, "туда и обратно");
                Check(!s.ToPersonalJson().Contains("RecordNoise"), "личный файл — только тема");
            });
            yield return Case("границы и папка — чистятся", () =>
            {
                var s = SpySettings.Compose(null, "{ \"MaxRecordsPerTransaction\": 1, \"DefaultPeriodDays\": -5, \"JournalFolder\": \"  \\\"D:\\\\Журналы\\\" \" }", null);
                Check(s.MaxRecordsPerTransaction == 50 && s.DefaultPeriodDays == 0, "границы");
                Check(s.JournalFolder == @"D:\Журналы", "кавычки и пробелы вокруг пути: " + s.JournalFolder);
            });
        }
    }
}
