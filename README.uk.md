# SPOMover

[English version](README.md)

Контрольоване розділення великої бібліотеки SharePoint Online (1,5 млн+ файлів) на кілька бібліотек того самого сайту за таблицею мапінгу:

**інвентаризація → план (dry-run) → переміщення хвилями → перевірка.**

Переміщення виконує сам SharePoint (Copy/Move API, `MoveCopyUtil`), тож історія версій, метадані й ідентифікатори файлів лишаються з файлами. SPOMover відповідає за все навколо: навантаження в межах бюджету RU тенанта, вікна роботи, повтори, заблоковані файли й файли на check-out, продовження після збою чи перезавантаження.

## Можливості

- **Інвентаризація** джерела і цільових бібліотек через Microsoft Graph `delta` з продовженням після зупинки та оновленням лише змінами; підсумки по папках; файли на check-out — через SharePoint REST.
- **Мапінг** «шлях у джерелі → бібліотека і шлях у ній» з перевіркою під час введення; імпорт з CSV/XLSX.
- **Перевірка покриття** до переміщення: що куди, що лишається, конфлікти імен, задовгі шляхи.
- **План** пакетів, згрупованих у хвилі, з прогнозом часу; нічого не змінюється, доки план не застосовано.
- **Переміщення** засобами SharePoint з урахуванням throttling (`429`/`503`, `Retry-After`), бюджету RU, вікон роботи, пулу застосунків, з повторами і звіркою.
- **Після переміщення**: перевірка цільових бібліотек, звіт пілоту, видалення порожніх папок джерела, правила заміни посилань (CSV) для виправлення зв'язків між файлами Office.
- **Десктопний застосунок** (WPF + WebView2, Blazor/MudBlazor) і **CLI** для запуску за розкладом і скриптів; обидва працюють з тим самим рушієм і тією самою локальною базою стану.
- **Інтерфейс українською та англійською**, мова перемикається без перезапуску (кнопка мови в заголовку вікна; `--lang en|uk` у CLI).

## Вимоги

- Windows 10/11 з WebView2 Runtime.
- .NET SDK 10 для збірки (реліз самодостатній).
- Реєстрація застосунку в Entra ID з дозволом `Sites.Selected` (Microsoft Graph і SharePoint, application), `write` на сайт і сертифікатом у сховищі Windows. Секретів застосунок не зберігає. Покрокове налаштування — [інструкція, розділ 1.5](docs/USER_GUIDE.uk.md).

## Швидкий старт

```bash
dotnet build -c Release
dotnet test
```

Демо без SharePoint (емулятор Graph, бібліотека на 600 файлів і дві цільові бібліотеки):

```bash
dotnet run --project tools/SPOMover.DemoSeed -- D:\Temp\spo-mover-demo --open
```

`--large 1500000` додає синтетичну інвентаризацію на 1,5 млн файлів для перевірки швидкості GUI.

Приклади налаштувань: [samples/connection.example.json](samples/connection.example.json), [samples/split.profile.json](samples/split.profile.json).

Приклади CLI (застосунок має бути запущено; CLI знаходить його через папку даних):

```bash
spo-mover connections import samples/connection.example.json
spo-mover profiles import samples/split.profile.json
spo-mover inventory split --wait
spo-mover map check split
spo-mover plan build split
spo-mover apply split --plan <план> --wait
spo-mover verify <прогін> --update
```

Повний перелік — `spo-mover --help` або `spo-mover <команда> --help`.

## Мова

Мову інтерфейсу під час першого запуску визначає мова Windows (українська — на українській Windows, інакше англійська); змінити її можна в заголовку вікна, вибір зберігається в папці даних (`%LOCALAPPDATA%\SPOMover\language.txt`). Змінна середовища `SPO_MOVER_LANG` (`en` / `uk`) має пріоритет, CLI приймає `--lang`.

Переклади — у `src/SPOMover.Core/Localization/<мова>/*.json` у вигляді `"English text": "переклад"`; у коді вихідна мова — англійська (`L.T("…")`). `LocalizationTests` падають, якщо ключ не має перекладу або в коді лишився український текст.

## Документація

- [Інструкція користувача](docs/USER_GUIDE.uk.md) (вбудована в застосунок як довідка) — [англійською](docs/USER_GUIDE.md)
- Зміни між версіями (англійською) — [CHANGELOG.md](CHANGELOG.md)
- Вимоги — [docs/FEATURES.md](docs/FEATURES.md)
- План і етапи — [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md)
- Результати перевірки Copy/Move API — [docs/API_SPIKE.md](docs/API_SPIKE.md)

## Реліз

`installer\build.ps1` збирає самодостатній реліз, інсталятор MSI (WiX) і portable zip.

## Ліцензія

[MIT](LICENSE). Вбудована бібліотека [uPlot](https://github.com/leeoniya/uPlot) має ліцензію MIT (`src/SPOMover.Ui/wwwroot/lib/uplot/LICENSE`).
