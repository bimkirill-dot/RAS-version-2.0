# RAS v. 2.0 для Navisworks

Часть плагина для Navisworks Manage 2023 (у всех в компании — эта версия). Отдельно от Revit: свой загрузчик, своя установка, свои выпуски и обновления (решение разработчика, 23.10.2026). Что делают инструменты — в модулях (сейчас один — «Подстроить виды» модуля RAS-Clash Analysis: `src\Modules\RAS-Clash Analysis\Navisworks\`, решения — в его `DECISIONS.md`).

## Как устроено

- **Пакет** `RASv2.Navisworks.bundle` в `%AppData%\Autodesk\ApplicationPlugins\` — Navisworks сам загружает отсюда (не из `Program Files\…\Plugins`), без прав администратора:
  ```
  RASv2.Navisworks.bundle\
    PackageContents.xml          Nw20 = Navisworks 2023; Name = имя папки без .bundle
    Contents\
      RASv2.Navisworks.Loader.dll
      en-US\RASv2Ribbon.xaml     разметка ленты — обязательно в подпапке языка (без неё вкладки нет, и ошибок нет)
      ru-RU\RASv2Ribbon.xaml
      icons\FitViews16.png, FitViews32.png
  ```
- **Загрузчик** (`Loader\`): `CommandHandlerPlugin` с вкладкой «RAS v. 2.0» и кнопками (атрибуты `[Plugin]`, `[RibbonLayout]`, `[RibbonTab]`, `[Command]`; id кнопки в разметке = имя команды). Нажали кнопку — один раз за сеанс берёт сборку канала тем же кодом, что загрузчик Revit (`src\Loader\BuildResolver.cs`, константа `NAVISWORKS`): `Releases\Navisworks\2023\<канал>.txt` → копия в `%LocalAppData%\RAS_v_2.0\Navisworks\Builds\2023\<сборка>` → `RASv2.ClashAnalysis.Navisworks.dll`, `Entry.Execute(команда, сборка, папка, канал)`. Своей логики нет; меняется редко — кнопки ленты живут в пакете, новая кнопка = новый загрузчик = переустановка.
- **Иконки** — путь от папки сборки в коде (`LoaderIcons`, `{x:Static}` в разметке): `pack://siteOfOrigin` ведёт в папку Navisworks, а не пакета. PNG — 96 DPI (наши иконки из `icons.cmd` — такие).
- **Сборка** — .NET Framework 4.8, x64; API Navisworks — из папки Navisworks, без копирования (`Private=false`); без `UseWPF` (иначе разметка ленты собиралась бы как страница WPF).
- **Журнал** — `%LocalAppData%\RAS_v_2.0\Logs\navisworks.log` (загрузчик и сборка). Не появилась вкладка — журнал запуска Navisworks: `& "C:\Program Files\Autodesk\Navisworks Manage 2023\Roamer.exe" -log "<файл>"`.

## Разработчику

- `navisworks.cmd` — собрать загрузчик и сборку, проверки (`tests\RASv2.Tests`, .NET Framework 4.8), сборка — в `out\Releases\Navisworks\2023` (канал «разработка»), пакет — себе (если изменился; занят открытым Navisworks — закрыть и повторить), `%AppData%\RAS_v_2.0\navisworks-loader.ini` — загрузчик берёт сборки из папки исходников. Перезапустить Navisworks.
- Прежний пакет `RasGroup.ClashViewFixer` (первая попытка, 09.2026, исходники — `D:\Code\Navisworks-viewer`) `navisworks.cmd` убирает.
- Выпуск для коллег (сервер, каналы, установка) — после проверки прототипа.

Основа — руководство разработчика «Плагин для Navisworks Manage 2023: полное руководство» (`Add-plugin-to-panel.md`, проверено на учебном плагине `HelloNavis`).
