using System;
using System.Collections.Generic;
using System.Text;
using RASv2.ClashAnalysis.Bridge;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis, мост в Revit: какая открытая модель или связь — модель элемента коллизии (ModelNames).</summary>
    internal static class ClashBridgeTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly string[] Open = { "3479_01_07_S01_AR01_01_M3.rvt", "3479_01_07_S01_OV01_01_M3", "3479_01_07_S01_KJ01_01_M3.rvt" };

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("имя без папки и расширения", () =>
            {
                Check(ModelNames.Bare(@"D:\Модели\Дом_AR01.nwc") == "Дом_AR01", "папка и .nwc");
                Check(ModelNames.Bare("Дом_AR01.rvt") == "Дом_AR01", ".rvt");
                Check(ModelNames.Bare("Дом_AR01.v2") == "Дом_AR01.v2", "чужое расширение — часть имени");
            });
            yield return Case("точное совпадение — без расширения и регистра", () =>
            {
                Check(ModelNames.Match("3479_01_07_S01_AR01_01_M3.nwc", Open) == 0, "nwc ↔ rvt");
                Check(ModelNames.Match("3479_01_07_s01_ov01_01_m3.NWC", Open) == 1, "регистр, связь без расширения");
                Check(ModelNames.Match("3479_01_07_S01_VK01_01_M3.nwc", Open) == -1, "такой модели нет");
                Check(ModelNames.Match("", Open) == -1, "пусто");
            });
            yield return Case("русские буквы, похожие на латинские, — одно и то же", () =>
            {
                Check(ModelNames.Match("3479_01_07_S01_KJ01_01_М3.nwc", Open) == 2, "«М3» кириллицей");
                Check(ModelNames.Key("Ёлка") == ModelNames.Key("елка"), "ё = е");
            });
            yield return Case("приписка к NWC после разделителя — та же модель", () =>
            {
                Check(ModelNames.Match("3479_01_07_S01_KJ01_01_M3_с лестницей_BIM_Alma.nwc", Open) == 2, "«_с лестницей_BIM_Alma»");
                Check(ModelNames.Match("3479_01_07_S01_AR01_01_M3_отсоединено.nwc", Open) == 0, "«_отсоединено»");
                Check(ModelNames.Match("3479_01_07_S01_AR01_01_M35.nwc", Open) == -1, "«M35» — не «M3»: после «M3» нет разделителя");
            });
            yield return Case("из нескольких подходящих — самая длинная", () =>
            {
                var open = new[] { "Дом_AR01", "Дом_AR01_01" };
                Check(ModelNames.Match("Дом_AR01_01_отсоединено.nwc", open) == 1, "«Дом_AR01_01», а не «Дом_AR01»");
            });
            yield return Case("просьба сайта (JSON браузера) читается, ответ — по договорённости", () =>
            {
                var json = "{\"version\":1,\"clash\":\"Конфликт14\",\"elements\":[{\"model\":\"Дом_AR01.nwc\",\"id\":11634563,\"category\":\"Стены\"}," +
                           "{\"model\":\"Дом_OV01.nwc\",\"id\":10184727,\"extra\":\"незнакомое поле\"}]}";
                var r = BridgeProtocol.Read<ShowRequest>(Encoding.UTF8.GetBytes(json));
                Check(r.Version == 1 && r.Clash == "Конфликт14" && r.Elements.Count == 2, "просьба целиком");
                Check(r.Elements[0].Model == "Дом_AR01.nwc" && r.Elements[0].Id == 11634563 && r.Elements[0].Category == "Стены", "первый элемент");
                Check(r.Elements[1].Id == 10184727 && r.Elements[1].Category == null, "второй: незнакомое поле не мешает, нет категории — пусто");
                var reply = BridgeProtocol.Text(BridgeProtocol.Write(new ShowReply { Ok = true, Warnings = { "Связь «Дом_OV01» выгружена" } }));
                Check(reply.Contains("\"ok\":true") && reply.Contains("Дом_OV01") && reply.Contains("\"version\":1"), "ответ: " + reply);
            });
            yield return Case("открыта отсоединённая копия — тоже находится", () =>
            {
                var open = new[] { "Дом_AR01_отсоединено" };
                Check(ModelNames.Match("Дом_AR01.nwc", open) == 0, "в отчёте без приписки");
            });
        }
    }
}
