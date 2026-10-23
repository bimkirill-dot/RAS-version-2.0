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

## Установка, выпуск, обновление

- **Сервер:** `Releases\Navisworks\2023\<сборка>` (выпущенные сборки, не меняются), `test.txt` / `all.txt` (какая сборка у канала), `history.txt`; `Setup\Navisworks` — `install-navisworks.cmd` + пакет. Тестовая группа — та же, что у Revit (`Config\test-users.txt`).
- **Установка у человека** — `setup\install-navisworks.ps1` (запускает `install-navisworks.cmd`): есть Navisworks Manage или Simulate 2023 — пакет в `%AppData%\Autodesk\ApplicationPlugins` (копируется изменившееся; занят открытым Navisworks — закрыть и повторить), прежний `RasGroup.ClashViewFixer` (первая попытка, 09.2026, исходники — `D:\Code\Navisworks-viewer`) убирается. Права администратора не нужны.
- **Обновление** — само: при первом нажатии кнопки за сеанс загрузчик берёт сборку своего канала; новая — копирует к себе. Людям — перезапустить Navisworks. Переустановка — только когда изменился пакет (кнопки, иконки, загрузчик).
- **Разработчику** — `navisworks.cmd` (двойной щелчок — меню; `tools\Navisworks.ps1`): `build` — собрать, проверить (`tests\RASv2.Tests`, .NET Framework 4.8), сборка — в `out\Releases\Navisworks\2023` (канал «разработка»), пакет — себе тем же `install-navisworks.ps1 -Dev`, `%AppData%\RAS_v_2.0\navisworks-loader.ini` — загрузчик берёт сборки из папки исходников; `publish test|all`, `promote`, `rollback [test]`, `status`, `setup` — как у Revit (`revit.cmd`, те же пункты меню), в своих папках.
- **Загрузчик без файла отладки** (`DebugType=none`): иначе в сборку пишется номер `.pdb`, а он зависит от путей к исходникам (буква диска то строчная, то заглавная), и одинаковый код давал «новый» загрузчик — установка просила закрыть Navisworks. У загрузчика Revit — то же самое (поэтому `revit.cmd build` часто пишет «загрузчик не обновлён»); поправить — отдельным решением (изменение загрузчика Revit).

Основа — руководство разработчика «Плагин для Navisworks Manage 2023: полное руководство» (`Add-plugin-to-panel.md`, проверено на учебном плагине `HelloNavis`).
