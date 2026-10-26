# Исходная спецификация

[SPEC.md](SPEC.md) и [ANIMATION-AND-QA.md](ANIMATION-AND-QA.md) сохранены без изменений. [CODEX-START.md](CODEX-START.md), [PACKAGE-README.md](PACKAGE-README.md) и [VALIDATION.md](VALIDATION.md) — исторические документы исходного пакета; их описание состояния разработки и пути относятся к моменту передачи задания. Текущий статус реализации находится в [приёмке](../ACCEPTANCE.md).

Файлы пакета распределены по рабочим каталогам. Совпадение всех исходных файлов с сохранёнными копиями проверено по SHA256 перед удалением распакованного пакета; у валидатора и в описании контрактов затем обновлены пути.

| Путь в исходном пакете | Текущее расположение |
| --- | --- |
| contracts/project.schema.json | [Схема проекта](../../contracts/project.schema.json) |
| contracts/audio-alignment.schema.json | [Схема разметки](../../contracts/audio-alignment.schema.json) |
| contracts/README-contracts.md | [Описание контрактов](../../contracts/README-contracts.md) |
| examples/novostroykin.project.json | [Проект Новостройкина](../../examples/novostroykin/project.json) |
| examples/recorded-line.alignment.json | [Иллюстративная разметка](../../tests/fixtures/recorded-line.alignment.json) |
| validate_contracts.py | [Валидатор](../../scripts/validate_contracts.py) |

Проверка контрактов из корня репозитория: `python scripts/validate_contracts.py` (требуется установленный `jsonschema`). Скрипт определяет корень по своему расположению и не зависит от текущего рабочего каталога. Это структурная проверка, без платных запросов и видеорендера. Времена в иллюстративной разметке вымышлены; файл не пригоден для production alignment.
