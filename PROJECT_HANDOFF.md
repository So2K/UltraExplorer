# UltraExplorer — контекст и точка продолжения

Дата остановки: 2026-08-28. Пользователь попросил остановить работу и сохранить весь контекст. После создания этого файла дальнейшая реализация в текущем заходе не продолжалась.

## 1. Что в итоге нужно сделать

UltraExplorer — нативный сверхбыстрый файловый менеджер Windows с привычной оболочкой Windows 11 File Explorer и бесконечным ZUI-холстом вместо обычного одномерного списка.

Жёсткие требования пользователя:

- Только C# или C++; никакого Electron и веб-обёрток.
- Высокая скорость, аппаратный рендеринг/GPU, мгновенный pan/zoom.
- Максимально использовать зрелые библиотеки, готовые алгоритмы и проверенный production-код.
- Верхняя панель, navigation/address/search, command bar, левая навигация и status bar должны выглядеть практически как Windows 11 File Explorer с приложенного скриншота.
- Синяя область на скриншоте Explorer означает место ZUI-холста; синюю рамку рисовать не нужно.
- Нужны два режима: `View all` и `View select`.
- Сейчас полностью определяется и реализуется только `View all`. Поведение `View select` пользователь объяснит позже. Не придумывать его механику самостоятельно; пока достаточно видимого неактивного переключателя.

### Главная поправка пользователя

Предыдущая реализация с большими карточками-окнами папок и вложенными списками признана неправильной. Её не следует полировать или возвращать.

Правильный `View all`:

- каждый доступный диск — отдельный корень дерева;
- одна вершина графа соответствует ровно одному объекту файловой системы: диску, папке или файлу;
- папка раскрывается в непосредственных детей;
- дети соединяются с родителем тонкими ветвями;
- раскрытие ленивое: нельзя заранее рекурсивно сканировать весь диск;
- несколько ветвей и несколько дисков могут быть раскрыты одновременно;
- визуальный язык нужно адаптировать из Cryo: компактные плашки, тонкие связи, свободное перемещение и цветовые метки;
- оболочка вокруг холста при этом остаётся Explorer-подобной, а не похожей на Houdini/Discord/dev-tool.

## 2. Референс Cryo

Главный референс: <https://cryonet.io/> и <https://cryonet.io/docs/>.

Проверено по официальному сайту, документации и официальному бинарнику:

- Cryo использует компактные chip/pill-узлы примерно 34 px высотой; важные папки и устройства немного крупнее.
- Каждый узел представляет один путь/файл, а не окно со списком.
- Связи — тонкие спокойные кривые, всегда позади узлов.
- Узлы можно свободно раскладывать, окрашивать и соединять в смысловые группы.
- Устройства/компьютеры являются верхними корнями.
- Оригинальный Cryo показывает вручную курируемые visual bookmarks. Наш `View all` должен взять его визуальную механику, но заменить семантику на ленивое дерево реальной файловой системы.
- Cryo — закрытый коммерческий продукт, исходников нет. Официальная страница прямо говорит, что он не open source.
- Официальный Windows-бинарник указывает на C++/Qt 5, Qt Widgets + QQuick/QML, QtQuickShapes/Controls2 и GPU-рендеринг через ANGLE/D3D. Копировать исходники невозможно; можно адаптировать идею и поведение.

Полезные официальные изображения:

- <https://cryonet.io/img/screen_hero0.webp>
- <https://cryonet.io/img/screen_views1.png>
- <https://cryonet.io/docs/img/node_view_dnd1.png>

## 3. Выбранный нативный стек

- `.NET 10`, Windows x64, WPF.
- `Nodify 7.3.0` — зрелый MIT ZUI/node canvas с pan, zoom, selection, connections и minimap.
- `Vanara.Windows.Shell 5.0.7` — Shell API, системные меню и Win32 interop.
- WPF использует аппаратный DirectX rendering pipeline; для текущего масштаба это самый быстрый и предсказуемый путь к рабочему продукту.
- Если поздний нагрузочный тест покажет, что WPF/Nodify не держит нужный frame time при тысячах видимых узлов, менять только canvas renderer на Win2D/custom Direct2D, сохраняя модель и Shell-слой.

Проект: `E:\AiControl\UltraExplorer\UltraExplorer.sln`.

Основной проект: `E:\AiControl\UltraExplorer\src\UltraExplorer\UltraExplorer.csproj`.

## 4. Уже существовавшая рабочая база

До последней корректировки была собрана нативная WPF-версия, которая умела:

- бесконечный Nodify canvas, pan/zoom, minimap;
- большие folder-card nodes со списками — это теперь устаревший и неверный UI;
- открытие файла двойным кликом в стандартной программе;
- файловые операции, rename/new folder/text file/duplicate/delete/recycle;
- clipboard, внутренний и внешний WPF FileDrop;
- выбор ближайшей folder-card при drop;
- favorites, notes, colors, search и сохранение workspace;
- FileSystemWatcher для обновлений;
- нативные Shell icons;
- попытка настоящего Windows Shell context menu.

Старые файлы `FolderNodeView.xaml/.cs`, `FolderNodeViewModel.cs`, `FolderConnectionViewModel.cs` можно оставить как временный компилируемый legacy-код, но новый `View all` не должен их отображать.

## 5. Новый изолированный слой View All уже добавлен

Агент успел добавить новый независимый слой, не меняя `MainWindow`, `FolderNodeView` и `App.xaml`:

- `src/UltraExplorer/Models/ViewAllGraphModels.cs`
- `src/UltraExplorer/Models/ViewAllNodeViewModel.cs`
- `src/UltraExplorer/Models/ViewAllEdgeViewModel.cs`
- `src/UltraExplorer/Services/ViewAllFileSystemService.cs`
- `src/UltraExplorer/Services/ViewAllLayoutService.cs`
- `src/UltraExplorer/Services/ViewAllGraphService.cs`
- `src/UltraExplorer/Services/ViewAllViewportService.cs`
- `src/UltraExplorer/Services/ViewAllWorkspaceStore.cs`

Что в нём уже задумано/реализовано:

- `ViewAllNodeViewModel` — компактный объект `Drive | Folder | File`, один path на одну ноду, базовый размер `184 × 44`.
- `ViewAllGraphService.InitializeAsync()` создаёт только готовые диски-корни.
- `ExpandAsync()` читает только один уровень каталога.
- `Collapse()` скрывает потомков, не удаляя уже загруженную ветку.
- `RefreshBranchAsync()` перечитывает только выбранную папку.
- `LoadMoreAsync()` явно увеличивает лимит для огромной папки.
- Защитный лимит по умолчанию — 750 непосредственных детей; не рекурсивный.
- Reparse point по умолчанию не раскрывается, чтобы не получить циклы.
- `ViewAllLayoutService` делает стабильную инкрементальную left-to-right раскладку и не двигает вручную размещённые узлы.
- `ViewAllViewportService` делает collection-level viewport culling и semantic LOD (`Dot`, `Glyph`, `Compact`, `Detailed`).
- `ViewAllWorkspaceStore` сохраняет отдельный файл `%LOCALAPPDATA%\UltraExplorer\view-all.workspace.json` атомарно.
- Этот слой, по сообщению агента, собирался отдельно без предупреждений. После остановки общий проект заново не проверялся.

Перед подключением следует быстро проверить эти файлы, потому что агент был остановлен во время финального аудита отмены загрузок/API.

## 6. Что изменено в последнем заходе

### Theme

`src/UltraExplorer/Themes/UltraTheme.xaml` уже переведён с синевато-чёрной dev-tool палитры на нейтральную Windows 11 палитру:

- shell/window `#202020`;
- controls `#2B2B2B`;
- hover `#2D2D2D`;
- strokes `#303030` / `#3A3A3A`;
- canvas `#111315`;
- text `#FFFFFF`, `#C8C8C8`, `#9A9A9A`;
- accent fallback `#60CDFF`;
- controls получили радиус 4, 32 px sizing и нейтральный pressed state.

Это изменение внесено, но ещё не просмотрено в новом UI.

### Native Shell context menu

В `src/UltraExplorer/Services/NativeShellService.cs` для одиночного элемента применено:

```csharp
using var item = new ShellItem(existingPaths[0]);
```

Это важно: `ShellItem.Open(folder)` возвращал `ShellFolder`, и его `ContextMenu` был меню фона папки (`New`, view commands), а не полным меню выбранной папки. Явное создание базового `ShellItem` должно вернуть правильное item-menu и корректно владеть PIDL/menu lifetime.

Также `Preferred DropEffect` для Copy исправлен с неверного байта `5` на `1`; Move остаётся `2`.

Контекстное меню после этой последней правки ещё не успели перепроверить визуально.

Ранее неправильное ручное владение `ShellContextMenu.CreateFromItems` приводило к heap corruption `0xc0000374`. Одиночный путь теперь должен идти только через владеющий `ShellItem.ContextMenu`. Multi-select ветка всё ещё требует осторожного теста и при нестабильности должна временно использовать безопасное собственное меню, а не ронять процесс.

## 7. MainWindow пока неправильный и не подключён к View All

`src/UltraExplorer/MainWindow.xaml` и `MainWindow.xaml.cs` всё ещё отображают старые большие `FolderNodeView` карточки.

`src/UltraExplorer/ViewModels/MainViewModel.cs` всё ещё в основном управляет `FolderNodeViewModel`/`FolderConnectionViewModel`.

То есть визуальная переделка ещё не подключена. Именно это должно быть первым крупным действием после возврата.

## 8. Точная Explorer-like спецификация оболочки

Общая сетка окна:

| Область | Высота |
|---|---:|
| title/tab strip | 40 DIP |
| navigation/address/search | 48 DIP |
| command bar | 44 DIP |
| sidebar + ZUI canvas | `*` |
| status bar | 24 DIP |

Верхние три строки должны занимать всю ширину. Только content row делится на navigation pane и ZUI canvas.

### Title/tab row

- Нейтральный фон `#202020`.
- Одна активная вкладка высотой около 34, шириной 220–260, фон `#2B2B2B`, `CornerRadius=7,7,0,0`.
- Внутри: shell/folder icon 16, имя текущей локации, close hit area 28.
- Рядом `+` 32×32.
- Удалить текст `UltraExplorer`, бейдж `ZUI` и дублирующий центрированный заголовок.
- Caption buttons 46×40; close hover `#C42B1C`.
- Для Snap Layout нужен корректный `HTMAXBUTTON` в `WM_NCHITTEST` или нативные caption buttons.

### Navigation row

- Back, Forward, Up — hit area 32×32.
- Home из этой строки убрать; он в sidebar.
- Address area 32 px, радиус 4, фон `#2B2B2B`, border `#3A3A3A`.
- В идеале breadcrumb-сегменты с переходом в editable path по `Ctrl+L`.
- Refresh справа внутри address area.
- Search width примерно 300, min 220/max 360, icon справа, placeholder `Search {active name}`.

### Command row

Следовать именно пользовательскому скриншоту:

- слева: `Organize ▾`, `Give access to ▾`, `New folder`;
- после разделителя компактный segmented toggle `View all | View select`, где `View all` активен;
- справа: details/layout, dropdown, pane toggle, `+`;
- Cut/Copy/Paste/Rename/Delete остаются shortcuts/context actions или overflow, но не должны визуально забивать эту строку;
- `Fit all` и zoom относятся к canvas controls, не к системным Explorer-командам.

### Sidebar

- Width 240, min 190, max 340.
- Удалить яркую CTA `Add folder to canvas`, uppercase headings и карточку `Canvas controls`.
- Порядок: Home; divider; Desktop, Downloads, Documents, Pictures, pinned folders; divider; This PC; диски; Network; Linux при наличии.
- Item height 32, margin 8, padding 8, radius 4, shell icon 16.
- Selected state — слабый neutral fill и accent pill слева 3×16.

### Status bar

- Height 24, фон `#202020`, border сверху.
- Слева количество/selection, по центру путь выбранного объекта, справа две маленькие кнопки вида.
- Удалить зелёную точку и `Live`.

## 9. Спецификация компактного graph node

Целевой node, вдохновлённый Cryo, но в тёмной Windows-теме:

- обычная высота 36–40 DIP; drive/root 44–48 DIP;
- auto width примерно 120–220, максимум с ellipsis;
- radius 4–6, border `#454545`, surface `#292929`;
- shell icon 16–18 слева;
- disclosure chevron/маленький connector для папки;
- имя 12 px; metadata показывается только на высоком LOD;
- file — нейтральный, folder — едва тёплый оттенок/иконка, drive — немного более выраженная root-плашка;
- selected — accent outline, keyboard focus отдельно;
- drop target — 2 px accent outline + мягкий fill/pulse;
- loading — маленький progress ring;
- reparse point — badge и без автоматического следования;
- error — маленький warning badge/tooltip;
- цветовая метка — тонкая полоска/точка, не заливка всего узла;
- note — отдельный badge/popover;
- связи 1–1.5 px, спокойные cubic/step curves, всегда позади узлов, без ярких стрелок.

Взаимодействие:

- один клик — selection и обновление address/status;
- `Ctrl+click` — multiselect;
- double click folder или chevron — lazy expand/collapse;
- double click file — открыть в default app;
- drag самой вершины по canvas меняет только layout;
- drag file/folder за отдельную drag-handle/с задержкой запускает реальную filesystem drag operation;
- right click/`Shift+F10` — настоящее Shell item context menu;
- внешний drop выбирает папку под курсором, иначе ближайшую папку в радиусе примерно 96 DIP;
- `Ctrl` Copy, `Shift` Move, другой том по умолчанию Copy;
- target подсвечивается мягко, без резкого мигания.

## 10. Что нужно сделать следующим заходом

1. Закрыть старый запущенный Debug-экземпляр UltraExplorer перед сборкой. На момент остановки мог оставаться процесс `UltraExplorer` со старым UI.
2. Проверить новые `ViewAll*` файлы и выполнить `dotnet build UltraExplorer.sln -c Debug --no-restore`.
3. Создать `Controls/ViewAllNodeView.xaml/.cs` с компактной плашкой и реальными Shell icons.
4. Подключить `ViewAllGraphService` к `MainViewModel` либо выделить `ViewAllViewModel`:
   - bind nodes/edges/selected nodes;
   - active graph node;
   - expand/collapse/load more/refresh;
   - address/status/navigation;
   - view render set из `ViewAllViewportService`;
   - сохранение viewport/layout через `ViewAllWorkspaceStore`.
5. Полностью перестроить `MainWindow.xaml` по Explorer-like сетке из раздела 8 и заменить `FolderNodeView` template на `ViewAllNodeView`.
6. Перенести existing Shell operations на selection компактных nodes:
   - open, context menu, copy/cut/paste, rename, delete, properties;
   - native/external drag-drop;
   - nearest folder target;
   - favorites, notes, colors.
7. Сделать `View all` активным режимом по умолчанию. `View select` показать рядом, но не определять его поведение до следующего объяснения пользователя.
8. Выполнить безопасные UI-тесты через Computer Use: startup, диски-корни, раскрытие 2–3 уровней, collapse, selection, pan/zoom/LOD, right-click+Escape, search, notes/colors, drag target на специально созданной тестовой папке.
9. Проверить multi-select native context menu на отдельном безопасном fixture; при любом native crash отключить multi-item native menu до отдельной реализации через STA worker/IContextMenu3.
10. После визуальной проверки собрать Release:

```powershell
dotnet publish src/UltraExplorer/UltraExplorer.csproj -c Release -o artifacts/win-x64
```

## 11. Критерий готовности текущего этапа

Этап `View all` считается готовым только если:

- на старте видны доступные диски как независимые корни;
- раскрытие диска/папки создаёт компактные дочерние узлы и не сканирует потомков;
- нет ни одной большой карточки со встроенным файловым списком;
- оболочка с первого взгляда воспринимается как Windows 11 File Explorer;
- pan/zoom остаются плавными, текст не превращается в нечитаемую кашу благодаря LOD;
- double click, context menu и стандартные файловые операции работают как в Explorer;
- drag/drop выбирает очевидную ближайшую папку и показывает точную цель;
- workspace восстанавливает раскрытые ветви, позиции и viewport;
- Debug и Release собираются без warnings/errors;
- приложение не падает на Shell context menu.

## 12. Важные технические ссылки

- Nodify: <https://github.com/miroiu/nodify>
- Nodify 7.3.0: <https://www.nuget.org/packages/Nodify/7.3.0>
- Vanara: <https://github.com/dahall/Vanara>
- WPF hardware rendering: <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-taking-advantage-of-hardware>
- Windows `IFileOperation`: <https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation>
- Windows `IContextMenu`: <https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-icontextmenu>
- Files open-source Shell implementation: <https://github.com/files-community/Files>

## 13. Последнее известное состояние сборки

До добавления нового View All слоя и смены темы команда:

```powershell
dotnet build UltraExplorer.sln -c Debug --no-restore
```

завершалась с `0 Warning(s), 0 Error(s)`.

После последних изменений общий build и визуальная проверка не выполнялись, потому что пользователь попросил немедленно остановиться.

Проект не является Git-репозиторием. Нельзя рассчитывать на `git diff`/rollback; перед крупными переписываниями полезно сначала инициализировать Git или создать локальный checkpoint.

