using Microsoft.JSInterop;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// «Показать в Revit» — одна просьба для карточки и строки списка (DECISIONS.md, «Мост в Revit…»). Просит браузер
/// (wwwroot\site.js, ras.revit): сервер до компьютера пользователя не достаёт; ответ Revit браузер показывает сам.
/// </summary>
public static class RevitBridge
{
    /// <summary>Есть что показать: хотя бы у одного элемента есть номер.</summary>
    public static bool CanShow(IEnumerable<ElementView> elements) => elements.Any(e => e?.ElementId != null);

    /// <summary>Просьба по договорённости моста (версия 1): имя коллизии, файл модели, номер и категория элементов.</summary>
    public static Task Show(IJSRuntime js, string clash, IEnumerable<ElementView> elements) =>
        js.InvokeVoidAsync("ras.revit.show", new
        {
            version = 1,
            clash = clash ?? "",
            elements = elements.Where(e => e?.ElementId != null)
                .Select(e => new { model = e.ModelFile ?? "", id = e.ElementId.Value, category = e.Category ?? "" }).ToList(),
        }).AsTask();
}
