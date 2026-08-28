# UltraExplorer как файловый диалог

Другая программа может попросить UltraExplorer выбрать за неё файл или папку.
Открывается обычное окно — тот же холст, боковая панель, хлебные крошки, поиск —
плюс подвал диалога: **File name**, **Files of type**, кнопка действия и Cancel.

Реализован набор возможностей `IFileDialog` из `shobjidl_core.h`, плюс перевод
двух легаси-наборов флагов: `OPENFILENAME.Flags` (`commdlg.h`) и
`BROWSEINFO.ulFlags` (`shlobj_core.h`) — чтобы вызывающему коду не пришлось
переписывать маску, которая у него уже есть.

## 1. Два способа позвать

| Способ | Кому |
|---|---|
| `UltraExplorer.exe --pick …` | любой язык, любой скрипт; ответ — код возврата, JSON-файл и stdout |
| COM: `CoCreateInstance` своего CLSID → `IFileOpenDialog` / `IFileSaveDialog` | программе, которая уже работает со стандартным диалогом: меняется один GUID |

Системный `CLSID_FileOpenDialog` **не** подменяется. Он зарегистрирован как
`InProcServer32` на `shell32.dll`, и COM при `CLSCTX_ALL` всегда предпочтёт
in-proc сервер записи `LocalServer32` — подмена потребовала бы собственной
нативной DLL, загружаемой в каждый процесс. Это не сделано намеренно.

## 2. Командная строка

```powershell
UltraExplorer.exe --pick --folder --title "Куда импортировать" --result out.json
```

Код возврата: `0` выбрано, `1` отменено, `2` ошибка.

### Что выбираем

| Ключ | Смысл |
|---|---|
| `--mode open\|save\|folder` | по умолчанию `open` |
| `--open`, `--save`, `--folder` | то же короче |
| `--multiselect` | разрешить несколько результатов |
| `--show-files` | показывать файлы и при выборе папки (`BIF_BROWSEINCLUDEFILES`) |
| `--no-new-folder` | убрать команду New folder (`BIF_NONEWFOLDERBUTTON`) |

### Типы файлов

| Ключ | Смысл |
|---|---|
| `--filter "Имя\|*.a;*.b\|Другое\|*.c"` | весь список одной строкой; принимается и форма `OPENFILENAME` с `\0` |
| `--type "Имя\|*.a;*.b"` | одна запись, можно повторять |
| `--filter-index N` | стартовая запись, нумерация с 1 |
| `--ext txt` | расширение, дописываемое к имени без него |

### Тексты и стартовая точка

`--title`, `--ok-label`, `--name-label`, `--file-name`,
`--start <путь>`, `--default-folder <путь>`, `--save-as-item <путь>`,
`--place <путь>` (и `--place-top`) — закрепить папку в боковой панели.

### Опции

| Ключ | Смысл |
|---|---|
| `--options 0x1000` | сырая маска `FOS_` |
| `--flag FileMustExist` | флаг по имени, можно повторять; `FOS_FILE_MUST_EXIST` тоже понимается |
| `--ofn 0x1000` | флаги `OPENFILENAME`, транслируются |
| `--bif 0x41` | флаги `BROWSEINFO`, транслируются |
| `--client-guid <guid>` | запоминать папку и тип файла отдельно для этого вызывающего |
| `--owner 0x00040A12` | окно-владелец; диалог держится над ним |

### Ответ

| Ключ | Смысл |
|---|---|
| `--result <путь>` | записать результат в JSON |
| `--json` | напечатать результат целиком как JSON |
| `--print` | печатать по одному пути на строку (поведение по умолчанию) |
| `--request <путь>` | прочитать все настройки из JSON-файла |

`--request` существует потому, что реальный фильтр полон кавычек, точек с
запятой и скобок, и командная строка для него — плохой транспорт:

```json
{
  "mode": "open",
  "flags": ["AllowMultiSelect", "FileMustExist"],
  "filters": [
    { "name": "Component", "pattern": "*.component;*.json" },
    { "name": "All Files", "pattern": "*.*" }
  ],
  "title": "Импорт компонента",
  "folder": "C:\\Projects",
  "clientGuid": "2b3d0f56-4f1e-4a3a-9c2c-9d1a5f6e7a10"
}
```

Результат:

```json
{
  "accepted": true,
  "paths": ["C:\\Projects\\thing.component"],
  "path": "C:\\Projects\\thing.component",
  "fileTypeIndex": 1
}
```

## 3. COM

Регистрация только для текущего пользователя, без прав администратора:

```powershell
UltraExplorer.exe --register-picker
```

Появляются два класса в `HKCU\Software\Classes\CLSID`:

| CLSID | Интерфейс |
|---|---|
| `{A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B31}` | `IFileOpenDialog` |
| `{A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B32}` | `IFileSaveDialog` |

Отменить: `UltraExplorer.exe --unregister-picker`.

Дальше код вызывающего не отличается от кода для системного диалога:

```csharp
var clsid = new Guid("A1D3B6E4-59C7-4E1B-9F2A-7C61D8E04B31");
var dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid))!;

dialog.SetTitle("Импорт компонента");
dialog.SetOptions(FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_ALLOWMULTISELECT);
dialog.SetFileTypes(2, filters);
dialog.SetFolder(startItem);
dialog.Advise(events, out var cookie);

if (dialog.Show(ownerHwnd) == 0)
{
    dialog.GetResults(out var items);   // IShellItemArray
}
```

Сервер запускается по требованию (`LocalServer32`), обслуживает сколько угодно
диалогов и завершается сам, когда последний клиент отпустил объект.

### Что реализовано и чего нет

| Метод | Состояние |
|---|---|
| `IModalWindow::Show` | да; `S_OK` при выборе, `HRESULT_FROM_WIN32(ERROR_CANCELLED)` при отмене |
| `SetFileTypes` / `SetFileTypeIndex` / `GetFileTypeIndex` | да |
| `Advise` / `Unadvise` | да |
| `SetOptions` / `GetOptions` | да, весь набор `FOS_` |
| `SetDefaultFolder` / `SetFolder` / `GetFolder` | да; `SetFolder` работает и во время показа |
| `GetCurrentSelection` | да |
| `SetFileName` / `GetFileName` | да |
| `SetTitle` / `SetOkButtonLabel` / `SetFileNameLabel` | да |
| `GetResult`, `GetResults`, `GetSelectedItems` | да |
| `AddPlace` | да, `FDAP_TOP` идёт первым в боковой панели |
| `SetDefaultExtension` | да |
| `Close` | да |
| `SetClientGuid` / `ClearClientData` | да |
| `SetFilter` | `E_NOTIMPL` — устарел в самом SDK |
| `IFileSaveDialog::SetSaveAsItem` | да |
| `SetProperties`, `GetProperties`, `ApplyProperties`, `SetCollectedProperties` | `E_NOTIMPL` — метаданные этот диалог не редактирует |
| `IFileDialogCustomize` | не реализован, `QueryInterface` вернёт `E_NOINTERFACE` |

События `IFileDialogEvents`: `OnFileOk` (можно запретить выбор, вернув не
`S_OK`), `OnFolderChange`, `OnSelectionChange`, `OnTypeChange`, `OnOverwrite`
(можно ответить за пользователя). `OnFolderChanging` и `OnShareViolation` не
шлются.

## 4. Что именно делают опции

Все `FOS_` разобраны, а не приняты и забыты:

| Флаг | Что происходит |
|---|---|
| `PICKFOLDERS` | список файлов выключен, кнопка «Select Folder», пустое имя = папка на экране |
| `ALLOWMULTISELECT` | несколько узлов, имена в кавычках в поле имени |
| `FILEMUSTEXIST` / `PATHMUSTEXIST` | проверяются перед возвратом |
| `OVERWRITEPROMPT` | вопрос перед заменой |
| `CREATEPROMPT` | вопрос перед созданием нового файла |
| `STRICTFILETYPES` | имя обязано подходить под выбранный тип |
| `NOVALIDATE` | ничего не проверяется, возвращается как есть |
| `NOREADONLYRETURN` | файл только для чтения отклоняется |
| `NOTESTFILECREATE` | отключает проверку записи в папку |
| `SHAREAWARE` | занятый другой программой файл — вопрос, а не отказ |
| `NODEREFERENCELINKS` | возвращается сам ярлык, иначе — его цель |
| `FORCESHOWHIDDEN` | скрытые и системные объекты видны |
| `HIDEPINNEDPLACES` | закреплённые папки скрыты из боковой панели |
| `OKBUTTONNEEDSINTERACTION` | кнопка выключена, пока пользователь ничего не тронул |
| `DONTADDTORECENT` | ничего не запоминается для этого вызывающего |
| `FORCEFILESYSTEM` | выполняется сам собой: возвращаются только пути файловой системы |

`ALLNONSTORAGEITEMS`, `DEFAULTNOMINIMODE`, `FORCEPREVIEWPANEON`,
`SUPPORTSTREAMABLEITEMS`, `NOCHANGEDIR`, `HIDEMRUPLACES` принимаются и не имеют
эффекта: в этом диалоге нет ни виртуальных объектов, ни mini-режима, ни панели
предпросмотра, ни списка недавних мест, а рабочий каталог он и так не меняет.

## 5. Поведение поля имени

Так же, как в стандартном диалоге:

- `*.log` — не имя файла, а фильтр: вид перестраивается;
- `C:\logs\*.txt` — переход в папку и фильтр одновременно;
- имя папки — переход внутрь, а не выбор (кроме режима выбора папки);
- `"a.txt" "b.txt"` — несколько файлов;
- `%WINDIR%` разворачивается;
- в режиме сохранения имя без расширения получает `--ext`, а если его нет —
  первое расширение выбранного типа; точка в конце означает «без расширения»;
- смена типа файла в режиме сохранения переименовывает `report.png` в
  `report.jpg`, а не приписывает второе расширение.

Двойной клик по файлу возвращает его; по папке — открывает.

## 6. Где хранится состояние

| Файл | Содержимое |
|---|---|
| `%LOCALAPPDATA%\UltraExplorer\picker.workspace.json` | раскладка холста сессий-диалогов, отдельно от рабочего пространства пользователя |
| `%LOCALAPPDATA%\UltraExplorer\picker-clients.json` | по `ClientGuid`: последняя папка, тип файла, недавние имена |
| `%LOCALAPPDATA%\UltraExplorer\com-server.log` | что делал COM-сервер; у него нет ни консоли, ни окна, чтобы пожаловаться |

## 7. Примеры

Готовые вызовы — в [samples/](samples/): PowerShell, C#, Python.
