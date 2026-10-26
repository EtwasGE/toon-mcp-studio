# Toon MCP Studio

Локальная студия коротких русскоязычных 2D-роликов: декларативный JSON → готовое аудио → таймлайн → Remotion → проверенный MP4. .NET 10, официальный C# MCP SDK, отдельный persistent worker, SQLite/WAL и файловое хранилище. Исходное ТЗ сохранено в [docs/spec](docs/spec/README.md); готовой реализации в нём не было.

Каждый подпроект находится в своей папке: `src/Toon.Core`, `src/Toon.Infrastructure`, `src/Toon.Worker`, `src/Toon.Mcp`, `src/Toon.Cli`, `renderer`, `tests/Toon.Acceptance`. React-приложение общее для всех роликов. Персонажи и SVG-фоны нарисованы специально для этого проекта.

## Быстрый запуск на проверенном Windows-окружении

Из PowerShell 7, в корне клонированного репозитория:

```powershell
./scripts/setup.ps1
# Один раз: создать локальные речевые fixtures (Windows, русский SAPI-голос)
powershell.exe -NoProfile -File ./scripts/prepare-fixtures.ps1
# Терминал 1: самостоятельный долгоживущий worker
dotnet src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll
# Терминал 2: реальный stdio MCP-клиент, импорт и финальный рендер
dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll demo final --wait
```

CLI выводит project/job ID и абсолютные пути результатов. Последний запуск записывается в `.data/latest-demo.json`. Результаты хранятся в `.data/artifacts/<job_id>/`: `video.mp4`, `cover.png`, `project.json`, `timeline.json`, `captions.json`, `captions.srt`, `validation-report.json`, `project.zip` (для final). `.partial` никогда не выдаётся через `get_artifacts`.

```powershell
dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll doctor
dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll status JOB_ID
dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll cancel JOB_ID
./scripts/acceptance.ps1        # включает реальные preview/final, отмену и crash recovery
./scripts/acceptance.ps1 -Quick # без видеорендера; HTTP только injected fixtures
./scripts/acceptance.ps1 -Library # библиотека, отдельный worker, без видео/TTS
```

Для Linux/WSL: .NET SDK 10.0.402, Node 22.20.0, системные библиотеки Chromium, затем `bash scripts/setup.sh`; команды `dotnet ...dll` те же. Речевые WAV для demo/acceptance нужно подготовить отдельно: генератор fixtures требует Windows и русский SAPI-голос. `TOON_IMPORT_ROOTS` разделяется `:` на POSIX и `;` на Windows. Linux/WSL не считается проверенным одним запуском на Windows. См. [приёмку](docs/ACCEPTANCE.md).

## Подключение Codex

Команда проверена по установленным `codex-cli 0.153.4`, `codex mcp --help`, `codex mcp add --help`. Выполните из корня репозитория для регистрации сервера `toon`:

```powershell
$toonRoot = (Get-Location).Path
codex mcp add toon `
  --env "TOON_APP_ROOT=$toonRoot" `
  --env "TOON_DATA_ROOT=$(Join-Path $toonRoot '.data')" `
  --env "TOON_IMPORT_ROOTS=$(Join-Path $toonRoot 'examples')" `
  -- dotnet (Join-Path $toonRoot 'src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll')
codex mcp get toon
```

Откройте новую сессию клиента, если он уже загрузил список MCP. Worker запускается отдельной командой выше, независимо от Codex. Закрытие MCP не останавливает очередь. `studio_doctor` и ответ `start_render` явно показывают отсутствие живого worker.

Вызовите `get_catalog(kind="styles")`, прочитайте guide resource, затем `register_asset`, `create_project`, `validate_project`, `start_render` и `get_job`/`get_artifacts`. У `start_render` обязательны `project_id`, `revision`, `quality=preview|final`, `idempotency_key`. Новый TTS выключен по умолчанию. Одинаковый нормализованный запрос с одним ключом возвращает один job; другой запрос с тем же ключом даёт `IDEMPOTENCY_CONFLICT`.

## Аудио и честные ограничения

Библиотека контента: [шаблоны, импорт, ревизии и локальная публикация](docs/CONTENT-LIBRARY.md). Шаблоны для заполнения: [персонаж](templates/library/character.json), [декорация](templates/library/environment.json), [голос](templates/library/voice.json). [Задачи 4–6: универсальный риг, подготовка эскизов по разметке и проверочный речевой MP4](docs/CHARACTERS.md). Персонажи с подготовленным и проверенным ригом доступны обычным проектам; исходные пакеты без рига остаются `render_supported:false`. Слоистые декорации, автоматическая AI-дорисовка и клонирование голосов пока не реализованы.

Проверенное локальное offline demo длится 27,7 с. Оно содержит настоящую синтезированную русскую речь, сохранённую в WAV, и после подготовки fixtures не обращается к сети. Это **synthetic speech fixtures**, созданные локальным Windows SAPI Microsoft Irina с двумя явно заданными pitch/rate presets. Это не записи актёров и не проверка двух ElevenLabs-голосов. Текст и происхождение находятся в `examples/offline-dialogue/lines.json` и `provenance.json`. WAV исключены из публичного Git: права на их распространение не подтверждены. После клонирования перед demo/acceptance выполните `scripts/prepare-fixtures.ps1` через Windows PowerShell с установленным русским SAPI-голосом. Длительность новой генерации определяется фактическим аудио. Не распространяйте установленное ПО голосов под лицензией проекта.

Таймлайн строится по числу декодированных PCM-сэмплов 48 кГц; речь не ускоряется и не обрезается ради target. Визуальные границы округляются отдельно от sample positions; итоговая длина округляется вверх. Цель 20–45 с, максимум 60 с. Preview 540×960 и final 1080×1920 используют одинаковый таймлайн. H.264/yuv420p, AAC 48 кГц, faststart. Речь измеряется после кодирования: −16 LUFS ±1, true peak ≤−1 dBTP.

Рот — `approximate/amplitude`, четыре состояния со сглаживанием и гистерезисом; это не фонемная артикуляция. Без alignment `auto` показывает целую реплику. Word anchors и `mode=word` без проверенной разметки отклоняются. SRT даёт фразы, не слова. Длинные титры не делятся на выдуманные интервалы: возвращается `CAPTION_OVERFLOW`. Используется закреплённый локальный Noto Sans Bold, 52px, максимум две строки.

Телефон в demo показывает специально созданный и помеченный учебный PNG. Это **не** интерфейс Новостройкина. Реальный сценарий `examples/novostroykin/project.json` намеренно остаётся draft: нужны настоящий скриншот и выбранные пользователем голоса/записи. Изображения вставляются с `contain` без перерисовки и обрезки. Отдельные вопросы визуальной читаемости и произношения требуют просмотра на телефоне и прослушивания; автоматические метрики этого не доказывают.

## ElevenLabs и лимиты

Ключ читается только из `ELEVENLABS_API_KEY` в окружении worker. `.env.example` — перечень имён, автоматической загрузки `.env` нет. Не передавайте ключ в проект, tool-аргументы, CLI-команду, конфиг Codex или Git.

В окружении MCP **и** worker задайте `TOON_VOICE_ALIASES` как JSON с вашими выбранными ID:

```json
{
  "buyer_ru": {
    "provider": "elevenlabs",
    "voice_id": "YOUR_SELECTED_VOICE_ID",
    "model_id": "YOUR_AVAILABLE_MODEL_ID",
    "voice_settings": { "stability": 0.5, "similarity_boost": 0.75 },
    "dictionary_version": "none",
    "pronunciation_dictionary_locators": []
  }
}
```

Адаптер использует `/v1/text-to-speech/{voice_id}/with-timestamps`, независимый запрос на реплику, `mp3_44100_128`. Художественный delivery пока отображается как neutral provider delivery и явное предупреждение; эмоции персонажей сохраняются. Нормализация, изменившая слова/числа относительно transcript, требует явного mapping и не получает угаданных индексов.

Оплачиваемый режим: `allow_new_tts=true` **и** `max_new_tts_characters` >0 в пределах `TOON_MAX_TTS_CHARACTERS` (по умолчанию 4000). До первого запроса проверяется суммарный объём уникальных незакешированных реплик в Unicode code points. Денежная оценка и provider billing units остаются `null`, когда неизвестны. Подтверждённый ответ и неизвестный исход журналируются отдельно; `requested_characters` не выдаётся за фактическое списание.

Ключ кеша включает нормализованный текст, provider/model/resolved voice, применённый delivery, настройки, словарь, формат и версию адаптера. Camera-only правка кеш не меняет. Есть OS single-flight lock и durable журнал перед отправкой. При неоднозначном timeout/5xx нет скрытого повтора: `PROVIDER_OUTCOME_UNKNOWN`. Если request ID не получен, автоматическое восстановление результата по истории невозможно; требуется сверить историю аккаунта. Повтор отклонённого 429 учитывает сохранённый Retry-After; автоматического цикла платных повторов нет. Live acceptance не запускался.

## Хранение и конфигурация

`TOON_APP_ROOT` — установка; `TOON_DATA_ROOT` — всё состояние (по умолчанию `.data`); `TOON_IMPORT_ROOTS` — разрешённые оператором корни (по умолчанию `examples`). Пути импорта проверяются, reparse points/symlinks запрещены; PNG/JPEG/WAV/MP3 копируются и хешируются. Лимиты MVP: 25 MiB media, 1 MiB sidecar, 12 MP image, 120 s audio. Проект содержит только ID/версии; импорт не даёт менять allowlist. Внешний TSX/JS/SVG/HTML не выполняется.

Можно переопределить `TOON_NODE`, `TOON_FFMPEG`, `TOON_FFPROBE`; совместимость другой версии требует проверки. `TOON_LEASE_SECONDS`: 10–300, default 30; `TOON_HEARTBEAT_SECONDS`: default 5, максимум треть lease. Настраиваются `TOON_MAX_MEDIA_BYTES`, `TOON_MAX_ALIGNMENT_BYTES`, `TOON_MAX_IMAGE_PIXELS`, `TOON_MAX_AUDIO_SECONDS`; значения по умолчанию указаны выше, абсолютные серверные пределы — 512 MiB / 8 MiB / 64 MP / 300 s. Один worker на data root, один активный job. После crash истёкшая аренда даёт `WORKER_INTERRUPTED`, а не ложный успех. Windows Job Object закрывает дочерние процессы при смерти worker; POSIX supervisor контролирует родителя и process group. Контролируемая отмена убивает дерево процессов. Статус `succeeded` и публикация metadata синхронизированы транзакцией с отменой.

Схемы входа и sidecar — исходные контракты. [Архитектура и форматы](docs/ARCHITECTURE.md), [приёмка](docs/ACCEPTANCE.md), [лицензии](THIRD_PARTY_NOTICES.md). Переносимый final ZIP содержит исходную ревизию, таймлайн, prepared audio, изображения, alignment, manifest с хешами. Voice aliases на другом компьютере требуют его конфигурации. ZIP не содержит ключей или самого установленного Node/.NET/Remotion.

## Закреплённое окружение

| Компонент | Проверенная версия |
|---|---|
| .NET SDK / runtime | 10.0.402 / 10.0.13 |
| Node / npm | 22.20.0 / 10.9.3 |
| Official ModelContextProtocol SDK | 2.3.0 |
| Microsoft.Data.Sqlite | 10.0.13 |
| JsonSchema.Net | 7.4.0 |
| React / React DOM | 19.3.1 |
| Все пакеты Remotion | 4.0.538 |
| TypeScript | 5.9.3 |
| Noto Sans / fontkit | 5.3.0 / 2.0.4 |
| Chrome Headless Shell | 157.0.8080.0 |
| ffmpeg-static / Windows binary | 5.3.0 / 6.1.1 |
| ffprobe-static / Windows binary | 3.1.0 / 4.0.2 |
| Codex CLI | 0.153.4 |

NuGet закреплён `packages.lock.json`, npm — `renderer/package-lock.json`. Номера проверены по установленным пакетам/официальным реестрам, а не взяты из ТЗ. Приёмка не означает подтверждённую совместимость произвольного обновления runtime или браузера.

Официальные источники, проверенные при разработке: [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), [Remotion renderMedia](https://www.remotion.dev/docs/renderer/render-media), [ElevenLabs timestamps](https://elevenlabs.io/docs/api-reference/text-to-speech/convert-with-timestamps/), [Codex MCP](https://learn.chatgpt.com/docs/extend/mcp), [лицензия Remotion](https://www.remotion.dev/docs/license/pricing). Remotion имеет собственные условия; применимость бесплатной лицензии зависит от пользователя/организации, весь стек не объявляется безусловно бесплатным.
