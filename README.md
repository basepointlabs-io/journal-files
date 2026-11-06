# Revit Journal Analyzer

Офлайн-програма, яка розшифровує journal-файли Autodesk Revit простою мовою: що сталося, чому і що робити.
Без AI і без передачі даних: файл аналізується лише на комп'ютері користувача за базою правил і обчисленими метриками.

## Структура

```
knowledge-base/
  schema.json            формат правила
  rules/
    autodesk-help.json   правила з офіційної довідки Autodesk (Revit Troubleshooting)
    community.json       сигнатури й рішення з форумів Autodesk та BIM-блогів
    metrics.json         правила, що потребують обчислень (пам'ять, падіння, add-in'и, синхронізація)
  notes/
    journal-format.md    опис формату journal-файлу для парсера
tools/
  validate_kb.py         перевірка бази правил
```

## Правило

Кожне правило містить:
- `match`: рядки (`any`) або regex (`regex`) для пошуку в journal; правила з `metrics.json` мають блок `metric`;
- `title`, `cause`, `fix`: пояснення і кроки виправлення українською (`uk`) та англійською (`en`);
- `severity`, `category`;
- `confidence`: `official` (документація Autodesk), `community-verified` (підтверджене рішення на форумі), `community` (не підтверджено);
- `related`: правила з інших джерел, що спрацьовують на той самий текст. Аналізатор показує їх разом.

## Перевірка

```bash
python3 tools/validate_kb.py
```

## Приватність

Справжні journal-файли містять імена користувачів, шляхи, назви серверів і проєктів.
Їх **не можна комітити** в цей репозиторій: `.gitignore` блокує `*.txt`, `samples/` і `journals/`.
