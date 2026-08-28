# UltraExplorer — состояние проекта

Обновлено: 2026-08-28. Предыдущая версия этого файла описывала точку остановки
перед подключением `View all`; теперь режим подключён и работает.

## 1. Что это

Нативный файловый менеджер Windows: оболочка Windows 11 File Explorer вокруг
бесконечного ZUI-холста вместо одномерного списка.

- `.NET 10`, WPF, Windows x64.
- `Nodify 7.3.0` — ZUI/node canvas (pan, zoom, selection, connections, minimap).
- `Vanara.Windows.Shell 5.0.7` — Shell API, нативные меню, Win32 interop.

Решение: `UltraExplorer.sln`. Приложение: `src/UltraExplorer/UltraExplorer.csproj`.
Сборка, запуск, тесты и publish — в [BUILD.md](BUILD.md).

## 2. Режим View all

Реализовано ровно то, что было описано в спецификации:

- каждый готовый диск — отдельный корень; WSL-дистрибутивы и UNC добавляются
  как дополнительные корни и восстанавливаются из workspace;
- одна вершина графа = один объект файловой системы (диск, папка, файл);
- раскрытие ленивое, ровно на один уровень, потомки никогда не сканируются
  заранее; защитный лимит 750 непосредственных детей, дальше `…` Load more;
- reparse point не раскрывается автоматически (значок-бейдж на узле);
- одновременно может быть раскрыто сколько угодно ветвей и дисков;
- раскладка инкрементальная: раскрытие ветви не двигает уже размещённые узлы,
  вручную перетащенный узел остаётся на месте даже после refresh;
- semantic LOD `Dot → Glyph → Compact → Detailed` плюс culling на уровне
  коллекции, поэтому далёкий зум остаётся плавным, а текст не превращается
  в кашу;
- узлы — компактные плашки 188 × 38 (диск 188 × 44) в духе Cryo: тонкая
  цветовая полоска, disclosure chevron, настоящая Shell-иконка, имя, метаданные
  только на высоком LOD, бейджи заметки/reparse/ошибки/Load more.

### Взаимодействие

| Действие | Результат |
|---|---|
| клик | selection, обновление address bar и status bar |
| `Ctrl`/`Shift` + клик, рамка | multiselect (штатный Nodify) |
| двойной клик по папке или клик по chevron | lazy expand/collapse |
| двойной клик по файлу | открытие в программе по умолчанию |
| drag за тело узла | перемещение узла по холсту (только раскладка) |
| drag за иконку узла | настоящая файловая drag-операция |
| правый клик по узлу | нативное Shell item context menu |
| правый клик по пустому холсту | New folder / New text file / Paste / Fit all / Reset zoom / Collapse all / Minimap |
| `Shift+F10` | то же меню с клавиатуры |
| внешний drop | папка под курсором, иначе ближайшая в радиусе ~96 DIP; цель мягко пульсирует |
| `Ctrl` / `Shift` при drop | Copy / Move; другой том по умолчанию Copy |

Горячие клавиши: `Ctrl+L`, `Ctrl+F`, `Ctrl+C/X/V`, `Ctrl+Shift+C` (copy as path),
`Ctrl+Shift+N`, `Ctrl+Shift+D`, `F2`, `F5`, `Del`, `Shift+Del`, `Enter`, `←`/`→`
(collapse/expand), `Alt+←/→/↑`, `Alt+Enter`, `Ctrl+0`, `Shift+1`, `Esc`.

## 3. Оболочка

Сетка окна ровно по спецификации: title/tab 40, navigation 48, command bar 44,
content `*`, status bar 24. Верхние три строки на всю ширину.

- Title/tab: одна вкладка 34 высотой с shell-иконкой, именем текущей локации и
  close-областью 28; рядом `+`; caption buttons 46 × 40, close hover `#C42B1C`.
  `WM_NCHITTEST` возвращает `HTMAXBUTTON` над кнопкой Maximize, поэтому Windows 11
  показывает Snap Layouts.
- Navigation: Back/Forward/Up 32 × 32, breadcrumb-сегменты с переходом в
  editable path по `Ctrl+L` или клику по свободному месту адресной строки,
  Refresh внутри справа, поиск шириной 300 (min 220 / max 360).
- Command bar: `Organize ▾`, `Give access to ▾`, `New folder`, разделитель,
  segmented toggle `View all | View select`, справа collapse-all, canvas
  options, pane toggle, overflow.
- Navigation pane: Home; divider; Desktop, Downloads, Documents, Pictures,
  Music, Videos, закреплённые папки; divider; This PC и диски; divider;
  Network и WSL. Высота элемента 32, radius 4, accent pill 3 × 16 у активного.
- Status bar: слева количество/выделение, по центру путь, справа две кнопки вида.

Поиск рекурсивный, обходом в ширину, результаты приходят потоком по мере
нахождения (до 200), reparse points пропускаются.

Цветовые метки и заметки хранятся по пути, а не по узлу, поэтому переживают
закрытие ветви и перезапуск.

## 4. View select

Переключатель виден и выключен. Механику пользователь объяснит отдельно —
она намеренно не придумана.

## 5. Проверено

- `dotnet build UltraExplorer.sln` в Debug и Release: `0 Warning(s), 0 Error(s)`.
- `ViewAllSmoke`: 75/75 проверок (ленивое раскрытие, collapse/re-expand, refresh
  с восстановлением раскрытых потомков, truncation и load more, стабильность
  раскладки, viewport culling и LOD, выбор drop-цели, правила Copy/Move,
  persistence, folder marks).
- UI прогнан через UI Automation: старт, диски-корни, reveal по sidebar и
  breadcrumb, раскрытие 3 уровней, все четыре LOD, Fit all, collapse all,
  потоковый поиск, `Alt+↑/←/→`.
- Нативное Shell context menu открывается по `Shift+F10` и закрывается по `Esc`,
  процесс жив (проверено по появлению окна класса `#32768`).
- Release single-file exe запускается и восстанавливает workspace.

## 6. Что исправлено по ходу

Найдено аудитом или тестами и починено:

- `Key.System`: WPF отдаёт `F10` и все `Alt`-сочетания как `Key.System`, поэтому
  `Alt+←/→/↑`, `Alt+Enter` и `Shift+F10` молча не работали.
- Приоритет значений в XAML: локально заданные `Visibility`, `Height`,
  `Background` перебивали Style-триггеры, из-за чего Dot-уровень не рисовался,
  диск не был выше папки, а выделение не подсвечивало плашку.
- Drag показывал курсор Move, а выполнял Copy: теперь курсор и операция берут
  решение из одного `MainViewModel.ShouldMove`.
- Буфер обмена больше не пишет собственный сериализованный формат (на .NET 9+
  такая сериализация удалена) — только `CF_HDROP` и `Preferred DropEffect`, как
  у Explorer, плюс повторные попытки при занятом буфере.
- Shell-операции идут на foreground STA-потоке: background-поток убивался при
  выходе и мог оборвать копирование на середине.
- Отмена диалога копирования/удаления больше не рапортуется как успех.
- `Delete` без выделения больше не отправляет в корзину папку, на которую просто
  смотрят: деструктивные команды работают строго по выделению.
- Дополнительные корни (WSL/UNC) теперь сохраняются и восстанавливаются.
- Reset zoom масштабирует относительно центра вьюпорта, а не начала координат.
- Поиск: обход в ширину и потоковая выдача вместо ожидания полного обхода.
- Доступность: `AutomationProperties.Name` на всех иконочных кнопках и узлах,
  focus visual вместо `FocusVisualStyle = null`, тёмный текст на светлом акценте,
  собственный шаблон ScrollBar, Cancel — кнопка по умолчанию в диалоге удаления.

## 7. Известные ограничения

- Multi-select context menu — собственное меню приложения, а не нативное.
  Нативное multi-item меню требует отдельного STA-хоста с `IContextMenu3`;
  до этого одиночный элемент (95% случаев) идёт через настоящий Shell menu,
  а multi-select не рискует уронить процесс.
- Живое обновление подписано только на активную папку (один watcher).
  Переименование родителя открытой ветви снаружи требует `F5`.
- Cross-volume drop всегда Copy; чтобы получить Move, нужен `Shift`.
- `View select` не реализован.

## 8. Ссылки

- Nodify: <https://github.com/miroiu/nodify>
- Vanara: <https://github.com/dahall/Vanara>
- Cryo (визуальный референс): <https://cryonet.io/>
- Windows `IContextMenu`: <https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-icontextmenu>
- Files (open-source Shell-реализация): <https://github.com/files-community/Files>
