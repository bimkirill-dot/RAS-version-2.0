using System;
using System.Collections.Generic;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: понимание папок с отчётами (ReportPath).</summary>
    internal static class ClashPathTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Root = @"\\10.10.1.77\Shared\Коллизии\Арман Кала";

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("нынешняя структура: проект \\ дата \\ критичность \\ формат \\ блок \\ отчёт", () =>
            {
                var p = ReportPath.Parse(Root, Root + @"\08.10.2026\Высокая критичность\XML\S1\АР Стены_МЕР Трубы.xml");
                Check(p.Criticality == "Высокая" && p.Block == "S1", "критичность / блок: " + p.Criticality + " / " + p.Block);
                Check(p.RunDate == new DateTime(2026, 10, 8), "дата: " + p.RunDate);
            });
            yield return Case("другой порядок папок и опечатка «Срдняя» — как в образце S08", () =>
            {
                var p = ReportPath.Parse(@"C:\Aport", @"C:\Aport\Высокая\S08\XML\отчёт.xml");
                Check(p.Criticality == "Высокая" && p.Block == "S8" && p.RunDate == null, "образец: " + p.Criticality + " / " + p.Block);
                var q = ReportPath.Parse(Root, Root + @"\2026-10-09\XML\Средняя критичность\С 02\отчёт.xml");
                Check(q.Criticality == "Средняя" && q.Block == "S2" && q.RunDate == new DateTime(2026, 10, 9), "русская С, дата ГГГГ-ММ-ДД");
                Check(ReportPath.Criticality("Срдняя критичность") == "Средняя", "опечатка «Срдняя» — тоже средняя");
            });
            yield return Case("название проекта не участвует: «Средний дом» — не критичность", () =>
            {
                var p = ReportPath.Parse(@"D:\Коллизии\Средний дом", @"D:\Коллизии\Средний дом\08.10.2026\XML\S3\отчёт.xml");
                Check(p.Criticality == "" && p.Block == "S3", "критичность из названия проекта: " + p.Criticality);
            });
            yield return Case("блоки: S, «Блок», «Секция», «Корпус»; не блок — пусто", () =>
            {
                Check(ReportPath.Block("S08") == "S8" && ReportPath.Block("s 3") == "S3", "S");
                Check(ReportPath.Block("Блок 02") == "Блок 2" && ReportPath.Block("секция-4") == "Секция 4" && ReportPath.Block("Корпус №1") == "Корпус 1", "слова");
                Check(ReportPath.Block("XML") == null && ReportPath.Block("S08_старое") == null && ReportPath.Block("Stage 1") == null, "не блоки");
            });
            yield return Case("даты: 08.10.2026, 8.10.26, текст вокруг; неверная дата — нет", () =>
            {
                Check(ReportPath.Date("8.10.26") == new DateTime(2026, 10, 8), "короткий год");
                Check(ReportPath.Date("Прогон 08.10.2026 повторный") == new DateTime(2026, 10, 8), "с текстом");
                Check(ReportPath.Date("31.02.2026") == null && ReportPath.Date("S08") == null, "неверная / не дата");
                // папки отчётов «Задач выгрузки»: {Дата} — со временем записи (08.11.2026)
                Check(ReportPath.Date("08.11.2026 18-05") == new DateTime(2026, 11, 8) && ReportPath.Date("2026-11-08 18-05") == new DateTime(2026, 11, 8) &&
                      ReportPath.Date("2026.11.08 18-05") == new DateTime(2026, 11, 8), "дата со временем «ЧЧ-мм» — дата");
            });
            yield return Case("дата отчёта: из отчёта → из папки (с временем файла) → время файла", () =>
            {
                var fileTime = new DateTime(2026, 10, 12, 15, 30, 0);
                var withDates = new ClashReport { Date = new DateTime(2026, 10, 7, 9, 0, 0), DateFromReport = true };
                Check(ReportPath.ReportDate(withDates, fileTime, new DateTime(2026, 10, 8), out var s1) == withDates.Date && s1 == ReportPath.DateFromXml, "из отчёта");
                var noDates = new ClashReport { Date = fileTime };
                Check(ReportPath.ReportDate(noDates, fileTime, new DateTime(2026, 10, 8), out var s2) == new DateTime(2026, 10, 8, 15, 30, 0)
                      && s2 == ReportPath.DateFromFolder, "из папки");
                Check(ReportPath.ReportDate(noDates, fileTime, null, out var s3) == fileTime && s3 == ReportPath.DateFromFile, "из файла");
            });
            yield return Case("порядок: критичности высокая → средняя → без; блоки по номеру", () =>
            {
                Check(ReportPath.CriticalityOrder("Высокая") < ReportPath.CriticalityOrder("Средняя") &&
                      ReportPath.CriticalityOrder("Средняя") < ReportPath.CriticalityOrder(""), "критичности");
                Check(ReportPath.BlockOrder("S2") < ReportPath.BlockOrder("S10") && ReportPath.BlockOrder("") == int.MaxValue, "блоки");
            });
        }
    }
}
