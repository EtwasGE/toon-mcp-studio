# От материалов до озвученного MP4 через MCP

Это общий процесс задач 12–13. Все изменения контента выполняются MCP-инструментами. Для начала вызовите `get_content_workflow(kind="character" | "environment" | "voice" | "production")`. Он возвращает последовательность инструментов, условия готовности, правила версий, отмены и повторного запуска. JSON доступен также по `toon://workflows/{kind}`, это руководство — по `toon://guides/content-workflow`.

## Подготовка установки

Для ежедневной работы используйте [единый запуск и короткий сценарий выпуска](DAILY-STUDIO.md): `get_episode_template` → `create_episode` → `start_studio_preview` → `open_studio_preview` → правки новой ревизией → `start_render`. Путь через полный DraftProject ниже также сохраняется.

Из корня репозитория:

```powershell
dotnet restore Toon.slnx --locked-mode
dotnet build Toon.slnx --no-restore
dotnet src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll
```

Worker запускается отдельным процессом. В другом терминале можно проверить `dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll doctor`. MCP запускает `dotnet src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll`; настройка Codex описана в README. `TOON_DATA_ROOT` и `TOON_IMPORT_ROOTS` у MCP и worker должны совпадать. Секрет ElevenLabs хранится в User Secrets или окружении и никогда не передаётся в tool-аргументах.

`studio_doctor` должен подтвердить `worker_alive`, зависимости и рендерер. Отсутствие ключа допустимо для записанной речи и уже проверенного кеша. Ключ сам по себе не разрешает новые облачные запросы.

## Персонаж и декорация

1. Вызовите `get_submission_template(kind)` и заполните `submission`. Для персонажа нужен `kind="character"`, для декорации — `"environment"`. Пример заполнения и роли возвращаются вместе с лимитами. Подтверждение `rights_confirmed` должно исходить от владельца материалов.
2. `create_library_item(submission)` возвращает постоянный `item_id` и `revision`.
3. `import_library_material(item_id, expected_revision, role, source_path, note)` импортирует оригинал. Персонажу обязательны роли `front`, `three_quarter`; декорации — `wide`. После каждого изменения берите новую возвращённую ревизию. Пути должны быть в настроенных разрешённых корнях.
4. Получите `get_character_recipe_template` или `get_environment_recipe_template`. Это разметка **примерных учебных изображений**, её нельзя автоматически применять к чужому эскизу. Разметьте фактически присланные пиксели, точки вращения/маски/позиции/камеры и сохраните через `set_character_recipe` или `set_environment_recipe`. Неизвестные детали остаются в `missing_details`; система не выдумывает скрытые части и не выполняет пользовательский код.
5. `prepare_library_item(item_id, expected_revision, idempotency_key)` ставит работу в очередь. Сохраните `job_id`, опрашивайте `get_job`. После `succeeded` перечитайте `get_library_item`; состояние `review` ещё не означает публикацию. Исследуйте errors/warnings и пакет `toon://library/{id}/revisions/{revision}/package`.
6. `get_library_materials(item_id, revision)` возвращает проверенные ссылки на исходники. Просмотрите изображения. При ошибке исправьте описание/разметку новой ревизией и повторите подготовку с новым idempotency key.
7. Импортируйте настоящую речевую запись через `register_asset(source_path, kind="audio")`. Для проверочной анимации нужна речь 4–12 секунд и точный transcript. SAPI-fixtures допустимы только в явно обозначенном тесте.
8. Запустите `start_character_test` или `start_environment_test` с `item_id`, `expected_revision`, `audio_asset:{asset_id,version}`, `transcript`, `idempotency_key`. После `get_job` → `succeeded` получите `get_artifacts` и просмотрите MP4/контрольные PNG. Проверяйте лицо, рот, маски, жесты, крупные планы, положение героев, перекрытия и звук.
9. Только после просмотра вызовите `publish_library_item(item_id, expected_revision, version="1.0.0", review_approved=true, review_note)`. Для render-compatible версии сервер требует успешный пробный MP4 для того же хеша пакета. Изменение рецепта или исходника требует новой пробы. Публикация локальная: она не отправляет файлы в GitHub или облако.

Опубликованный `item_id` персонажа используется как `character_id`, декорации — как `background.asset_id`. В обоих случаях указывается точная опубликованная версия. `get_catalog(kind="characters")` и `get_catalog(kind="backgrounds")` позволяют проверить доступность. Нельзя подставлять непроверенный исходный пакет вместо рига или декорации.

## Голос

Получите `get_submission_template(kind="voice")`, создайте запись и импортируйте `role="sample"`. После `prepare_library_item` проверьте `voice_analysis` и прослушайте оригинал. Помимо прав на материал нужны отдельные `cloning_consent` и `cloud_upload_consent`. Технический анализ не подтверждает личность, право на голос, отсутствие музыки или сходство.

Для `start_voice_clone` обязательны серверные `TOON_ALLOW_VOICE_CLONING=1`, явный накопительный `TOON_MAX_VOICE_CLONES`, ключ и аргументы `allow_cloud_upload=true`, `max_clone_requests=1`, `sample_reviewed=true`, содержательный `review_note`. Без отдельного согласования облачного режима этот шаг не выполняется. Загрузка делает один POST в ElevenLabs; TTS здесь не запускается.

После завершения прочитайте `result.profile` / `get_voice_profile`. При `verification_required` пройдите проверку в ElevenLabs и вызовите `refresh_voice_verification`; метаданные не заменяют подтверждение владельца. Пустой статус проверки блокирует использование.

`start_voice_preview` создаёт короткую WAV-пробу. Для нового TTS нужны `allow_new_tts=true` и ограничение `max_new_tts_characters`; для кеша — `false`, `0`. Получите `get_artifacts`, прослушайте и сравните с оригиналом, затем `review_voice_profile(...approved=true, review_note)`. Технический успех не означает похожий голос.

`bind_character_voice(character_id, version, expected_binding_revision, profile_id, profile_revision)` связывает одобренную ревизию с опубликованной версией персонажа. Первая привязка использует `expected_binding_revision=0`; последующие — значение из `get_character_voice`. Снимок профиля доступен по `toon://voices/{id}/revisions/{revision}`. Полные ограничения и recovery: [VOICES.md](VOICES.md).

## Проект и рендер

1. Соберите DraftProject по `contracts/project.schema.json`, используя ID/версии опубликованных персонажей и декораций. Для привязанного голоса у реплики задайте `audio:{mode:"tts",voice_alias:"character",delivery:"neutral"}`. При работе без облака можно использовать `mode:"recording"` и проверенный аудио-asset.
2. `create_project(project)` фиксирует конкретные `characters[].voice_profile`. `get_project(project_id, revision)` возвращает сохранённую версию; `validate_project` показывает готовность и количество новых символов TTS.
3. `start_render(project_id, revision, quality="preview"|"final", idempotency_key, allow_new_tts, max_new_tts_characters)` возвращает job. Для записи/кеша используйте `allow_new_tts=false`, `max_new_tts_characters=0`.
4. Закрытие MCP не отменяет рендер. Опросите `get_job` из новой сессии. `get_artifacts` выдаёт только опубликованный проверенный комплект. `.partial` и промежуточные файлы не являются готовым видео.
5. Получите MP4, cover, проект, timeline, субтитры, отчёт и ZIP. Контрольные PNG финального рендера сохраняются локально в `<TOON_DATA_ROOT>/jobs/<job_id>/frame-*.png`; в отличие от проверочных роликов персонажа/декорации, они не входят в список опубликованных артефактов. Проверьте картинку и прослушайте речь. Финальное время вычисляется из фактического аудио; word timing не выдумывается, рот остаётся приблизительным движением по амплитуде.

Настоящий интерфейс телефона импортируется отдельным изображением и сохраняется без перерисовки. Учебный экран из примера не является настоящим рекламным интерфейсом.

## Ревизии, отмена и восстановление

- Каждое изменение использует `expected_revision`; устаревшее значение даёт `REVISION_CONFLICT`, после которого нужно перечитать запись и осознанно повторить правку.
- Повтор одного запроса с тем же idempotency key возвращает прежний job. Другие аргументы с тем же ключом дают `IDEMPOTENCY_CONFLICT`; повтор не является командой перезапуска.
- `cancel_job` отменяет очередь/процессы. После отправки облачного запроса отмена не гарантирует возврат расходов или удаление облачного результата.
- При аварии worker просроченная аренда даёт `WORKER_INTERRUPTED`, а промежуточные файлы не публикуются. После исправления причины нужен новый явный запуск; кеш повторно используется. `PROVIDER_OUTCOME_UNKNOWN` не допускает автоматического повтора платной отправки. Для клона есть `reconcile_voice_clone` с проверкой точного маркера операции в описании голоса; старые метки `labels.toon_request` также поддерживаются.
- Выпуск `2.0.0` не заменяет `1.0.0`. Существующий проект продолжает ссылаться на старый риг, фон и голос. Для изменения проекта нужен `update_project` с актуальной ревизией. Отзыв согласия или отклонение голосового профиля блокирует дальнейшее использование даже старых ссылок.

## Сквозной воспроизводимый тест

```powershell
./scripts/acceptance.ps1 -Workflow
```

Или после сборки: `dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --workflow`. На POSIX: `bash scripts/acceptance.sh --workflow`; Linux/WSL остаётся отдельной непроверенной средой.

Тест создаёт **новые библиотечные ID** персонажа «Вася Пупкин — учебный макет», мастерской и голоса. Все пользовательские изменения выполняются настоящим stdio MCP-клиентом; пробы/рендер выполняет отдельный worker. Клонирование и синтез пробы используют только внедрённый HTTP fixture в тестовом worker, с локальной речью Windows SAPI. Это не живой ElevenLabs-клон и не пользовательский эскиз. User Secrets отключены; постоянный процесс тестового renderer-worker не получает API-ключа. Облачных запросов нет.

Проверяются целостность материалов, завершение после отключения MCP, ошибки подготовки и исправление, запрет публикации без пробы, одобрение/привязка голоса, идемпотентность, отмена, кеш, выпуск второй версии персонажа, фактический перезапуск worker после прерывания рендера и побайтовое совпадение контрольных PNG старого проекта после обновления. Результат: `.data/acceptance-*/workflow-acceptance.json`, финальный MP4/проект/ZIP в `artifacts/<final_job>/`.

Дополнительная независимая проверка экранной области и контактный лист на Windows: `python scripts/visual-qa.py .data/acceptance-RUN workflow-acceptance.json` (нужен Pillow). Целостность переносимого комплекта: `python scripts/verify-artifacts.py .data/acceptance-RUN/artifacts/JOB_ID`. Контракты: `dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --verify-artifacts .data/acceptance-RUN/artifacts/JOB_ID`.

Записи review в автоматическом тесте помечены как инфраструктурные fixtures. Художественное сходство настоящих материалов и живое клонирование требуют отдельных исходников и ручной приёмки; автоматический тест не объявляет их пройденными.
