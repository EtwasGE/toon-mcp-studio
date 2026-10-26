# Приёмка Toon MCP — 26 октября 2026

## Дополнение: библиотека контента, этапы 1–3

После реализации библиотеки выполнены:

- `./scripts/acceptance.ps1 -Library`: **46 проверок прошли**, отчёт `.data/acceptance-20261026-115720/library/library-acceptance.json`. Реальные stdio MCP-сессии и отдельный worker, миграция существующего v1-проекта, три шаблона, версии, CAS-конфликты, блокировка правок во время подготовки, импорт, локальная публикация/архив, отмена и восстановление после имитации истёкшей аренды. Проверены отказ при повреждении manifest/package/media и сохранность старых версий.
- `./scripts/acceptance.ps1`: **26 проверок прошли**, отчёт `.data/acceptance-20261026-115037/acceptance.json`. Созданы реальные preview/final MP4, проверены детерминизм контрольных кадров, отмена процесса и падение worker. Финальный job: `job_507d79fa256a4a8a9a53cd6f57c6a78a`. Этот прогон использует тот же итоговый runtime-код библиотеки; последующие правки затронули только тесты и документацию.
- Итоговая сборка: 0 ошибок/предупреждений; TypeScript check завершён. Первая сборка не смогла заменить DLL ранее запущенного idle worker; после проверки пустой очереди процесс остановлен и сборка повторена. Промежуточная nullable-ошибка в новом тесте исправлена до успешного итогового прогона.

Библиотечные материалы в тестах — явно обозначенные synthetic screen/tone fixtures. Они не доказывают качество настоящих эскизов, речь или клонирование. Новые source packages имеют `render_supported:false`. Универсальный риг, слои декорации, привязка/клонирование голоса и их художественная приёмка остаются следующими этапами. Живые платные запросы не выполнялись; прежние pending A20/A21, прослушивание и Linux-прогон остаются pending. Подробности API: [CONTENT-LIBRARY.md](CONTENT-LIBRARY.md).

## Исходная приёмка видеопайплайна

Это проверенный Windows vertical slice, а не заявление о закрытии всех ручных и облачных критериев. Оригинальный пакет ТЗ не изменён. Полный прогон с реальным SDK-клиентом, отдельным worker, preview/final, отменой и crash recovery завершился в `.data/acceptance-20261026-101818/acceptance.json`. После последних изменений лимитов/heartbeat выполнен focused lifecycle прогон `.data/acceptance-20261026-102806/acceptance.json`. Финальный быстрый прогон — `.data/acceptance-20261026-103441/acceptance.json`: 20 проверок прошли, затем проверены схемы и ZIP финального комплекта. Сборка: 0 ошибок и предупреждений; TypeScript check завершён. Первичная попытка rebuild при работающем Windows worker была заблокирована lock-файлом DLL; после остановки собственного idle worker повторная сборка завершилась успешно.

## Реальные результаты

- Основной проект: `project_48cf53d83fc0485ea1a12a602e0d8494`, revision 1.
- Job: `job_c4740d5ae1984e10858278b66d36efc2`.
- Финальный комплект: `.data/artifacts/job_c4740d5ae1984e10858278b66d36efc2/`.
- MP4: 1080×1920, 30 fps, 27,7 с, 831 кадр, H.264/yuv420p, AAC/48 кГц, faststart, 2 845 754 байта. Громкость −16,29 LUFS / −1,45 dBTP. SHA-256: `b45dc6da64f4c73d9aa4ad282b1fbbe33b5349e34f3ab57acde5a100ed628e28`.
- Последний финальный рендер: 123,89 s; preflight 0,67 s, speech 0,12 s, timing 0,27 s, render 103,65 s, encode 14,79 s, verify 4,39 s. 6 audio cache hits, 0 нового TTS. Peak памяти worker около 61,6 MB; это не суммарная память дочерних Chromium-процессов.
- В комплекте: MP4, обложка, сохранённый JSON проекта, таймлайн, captions JSON/SRT, проверочный отчёт, переносимый ZIP с media/manifest.
- Полный decode выполнен FFmpeg с `-xerror`. Длительность аудио определяется декодированными сэмплами; последний CTA исправлен после визуальной проверки и виден на последнем кадре.
- Голосовые fixtures — локальный синтез Microsoft Irina, шесть оригинальных русских фраз. В тестах нет внешнего TTS и оплачиваемых вызовов. Они не подменяют проверку двух выбранных живых ElevenLabs-голосов.

## A01–A21

| ID | Результат | Свидетельство и граница проверки |
|---|---|---|
| A01 | Windows pass; Linux pending | `scripts/setup.ps1` реально выполнил locked restore, чистый `npm ci`, загрузку Chromium, TypeScript, build и doctor. Новая ОС/Ubuntu не разворачивалась; обнаружен WSL2 docker-desktop. |
| A02 | Technical pass; listening pending | Реальный stdio MCP: import → create → render → artifacts. 27,7 с русской синтезированной речи, два оригинальных героя, две локации, разные планы, субтитры и CTA. Это WAV fixtures, не человеческие записи. |
| A03 | Pass | Неизвестный gesture/поле отклоняются до очереди; missing asset имеет JSON Pointer. Рекламный пример сохраняется только как draft с отсутствующими зависимостями. |
| A04 | Pass | Новая MCP-сессия читает revision 2 и resource revision 1. |
| A05 | Pass | Второй update с expected_revision=1 получает REVISION_CONFLICT после первого успешного update. |
| A06 | Pass | Одинаковый ключ/запрос возвращает один job; изменение quality с тем же ключом отклоняется. |
| A07 | Offline/fixture pass | Camera-only изменение не меняет audio entries/ключи; повторный provider fixture использует кеш. Live billing не проверен. |
| A08 | Cache/compile scope pass; live edit pending | Изменение текста меняет ровно один TTS cache key, независимый от соседних реплик. Реальный оплачиваемый edit/resynthesis не выполнялся. |
| A09 | Pass | MCP закрывается во время render, самостоятельный worker завершает job; новая сессия получает результат. |
| A10 | Pass on Windows | Убит только worker PID; реальный Node renderer PID завершён Job Object. После lease job failed/WORKER_INTERRUPTED; artifacts пусты. |
| A11 | Pass on Windows | Отмена уже запущенного renderer, подтверждение его PID termination; worker остаётся жив, job cancelled, нет опубликованного partial. |
| A12 | Pass | Измеренные интервалы, midpoint rounding, последняя пауза и ceiling; >60 s отклоняется, речь ради target не сокращается. |
| A13 | Pass | Word captions/anchors без alignment отклоняются; целая реплика разрешена. SRT даёт phrase timing. Иллюстративный sidecar отклоняется. |
| A14 | Pass | Server cap и меньший разрешённый бюджет блокируют новый TTS; allow_new_tts=false даёт TTS_REQUIRED. Unicode code-point estimate до очереди. |
| A15 | Controlled fixture pass | Injected HTTP disconnect: PROVIDER_OUTCOME_UNKNOWN, неизвестный usage в журнале, две попытки Get вызывают handler лишь один раз. Не live API. |
| A16 | Pass | Запрещённый корень, невалидная сигнатура PNG и реальный Windows junction отклонены; лимит импорта конфигурируется. |
| A17 | Pass | Все 11 tools с success/error output union, structuredContent и isError; реальные tools/call и resources/read. SDK разбирает stdio; subprocess stdin закрыт, логи в stderr. |
| A18 | Pass | Полный decode, видеопараметры, аудиопоток, громкость, длительность, последний кадр и экспортируемые файлы проверены. |
| A19 | Pass in pinned environment | Таймлайны preview/final и все 26 контрольных PNG совпадают. Бинарные MP4 между платформами не сравнивались. |
| A20 | Fixture pass; real interface pending | Независимый Chromium scaling исходного PNG совпал с экранной областью до H.264 пиксель в пиксель. Настоящего скриншота Новостройкина нет. |
| A21 | Pending | API key/два выбранных голоса/разрешённый платный лимит отсутствуют. Live timestamps, billing и произношение не проверены. |

## Дополнительные проверки

Синтетический AV sync probe — `.data/acceptance-20261026-102347/sync/sync-report.json`: onset аудио sample 48000, визуальной метки frame 30, смещение 0 кадров. Это технический импульс с явной маркировкой, не речевое демо и не доказательство артикуляции.

Контактный лист итогового ролика — `.data/contact-sheet.jpg`, screenshot comparison — `.data/visual-fixture-check.json`. Также сохранён лист полного прогона `.data/acceptance-20261026-101818/contact-sheet.jpg`. Визуально просмотрены общий план, крупные планы обоих героев, телефон, домашний кабинет, субтитры, границы жестов и последний CTA. Первоначально обнаружен пустой последний кадр; исправление включено в финальный прогон, отдельный regression check `final-frame` проходит.

`scripts/verify-artifacts.py` проверяет ZIP paths/hashes, совпадение проекта/отчёта с опубликованными файлами и сохранение CTA. Схемы сохранённого проекта/таймлайна проверяются командой:

```powershell
dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --verify-artifacts .data/artifacts/JOB_ID
dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --sync
dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --lifecycle
```

## Конкретно не подтверждено

1. Прослушивание человеком всего речевого ролика и произношения. Аудиофайл был подготовлен для воспроизведения, но доступный интерфейс не дал достоверного подтверждения слуховой проверки. Нельзя заменить её LUFS/ffprobe или утверждением о соответствии transcript.
2. Полный просмотр на физическом телефоне со звуком и без звука.
3. Реальный интерфейс Новостройкина, выбранные голоса и live ElevenLabs — A20/A21.
4. Отдельный Linux/WSL-run; POSIX process supervisor реализован, но здесь проверен Windows Job Object.
5. Музыкальный ducking реализован, но отдельный музыкальный пример на слух не принимался. Музыка в demo выключена. Точный phoneme lip-sync не заявлен.
6. Полный художественный контроль всех возможных пользовательских сцен/пересечений не автоматизирован. Текущие контрольные кадры проверены; новые сцены требуют preview review.

Платные запросы не выполнялись. Отсутствующие данные не заменены вымышленными скриншотами, word timing или mock-видео.
