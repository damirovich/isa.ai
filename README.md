# ISC.AI — «ИнспекторAI»

Платформа внутреннего контроля с ИИ, on-premise в изолированном контуре на локальных моделях.
Нейтральное ядро + сменный профиль «Инспектор».

- **Документация:** [docs/README.md](docs/README.md) (индекс), [docs/02_Архитектура.md](docs/02_Архитектура.md), [ТЗ](docs/ТЗ_ISC.AI.md)
- **Правила проекта:** [CLAUDE.md](CLAUDE.md)
- **Бэклог задач:** [docs/tasks/README.md](docs/tasks/README.md)

---

## Локальный запуск

### 1. Секрет БД (пароль — вне репозитория, Э4-10)

Пароль от БД **не хранится** в коде/конфиге. В `appsettings.json` — только несекретная топология
(хост/база/пользователь), пароль подставляется отдельно.

**Dev (user-secrets):**
```powershell
dotnet user-secrets set "Database:Password" "<пароль>" --project src/core/ISC.AI.Web
```
Проверить: `dotnet user-secrets list --project src/core/ISC.AI.Web`

**Пароль БД СКИД (Э3-08, вход по учёткам СКИД):** отдельный per-name секрет — общий пароль на неё
**никогда** не подмешивается, даже молча (без секрета — явный отказ на старте, а не утечка пароля
`ISC_AI` на сервер СКИД):
```powershell
dotnet user-secrets set "Database:Passwords:Skid" "<пароль read-only учётки СКИД>" --project src/core/ISC.AI.Web
```
Учётная запись подключения к БД СКИД (`ConnectionStrings:Skid`, по умолчанию `iscai_ro`) должна иметь
**только** `GRANT SELECT` на `public.users`/`public.departments` — выдаёт администратор СКИД; ISC.AI
никогда не пишет в эту БД (проверено на уровне кода: контекст переопределяет `SaveChanges` отказом).

Чтобы запустить каркас БЕЗ входа и БД СКИД (dev-заглушка доступа) — в
`appsettings.Development.json` поставить `"Auth": { "Mode": "Dev" }`.

**За обратным прокси контура** (если он есть) — заполнить `ForwardedHeaders:KnownProxies` реальными
IP прокси в `appsettings.json`, иначе троттлинг входа увидит IP прокси у всех запросов, а не клиента.
Без прокси — ничего настраивать не нужно, поведение не меняется.

**Сервер (прод):** вместо user-secrets — переменные окружения `Database__Password=<пароль>` и
`Database__Passwords__Skid=<пароль БД СКИД>`
(либо полная строка в `ConnectionStrings__Core` / `ConnectionStrings__Inspector`).

### 2. Применение миграций БД

Порядок **строгий**: сначала `core`, затем `inspector` (§5.1.4.4). Миграции идут через дизайн-фабрику,
которая берёт строку подключения из переменной окружения (пароль в коде не хранится).

**Package Manager Console (Visual Studio):**
```powershell
$env:ISCAI_CORE_CONNECTION="Server=10.10.0.115;Database=ISC_AI;Username=postgres;Password=<пароль>"
Update-Database -Context CoreDbContext -Project ISC.AI.Persistence -StartupProject ISC.AI.Persistence

$env:ISCAI_INSPECTOR_CONNECTION="Server=10.10.0.115;Database=ISC_AI;Username=postgres;Password=<пароль>"
Update-Database -Context InspectorDbContext -Project ISC.AI.Profile.Inspector.Data -StartupProject ISC.AI.Profile.Inspector.Data
```

**dotnet CLI (эквивалент, из корня решения):**
```powershell
$env:ISCAI_CORE_CONNECTION="Server=10.10.0.115;Database=ISC_AI;Username=postgres;Password=<пароль>"
dotnet ef database update --context CoreDbContext --project src/core/ISC.AI.Persistence --startup-project src/core/ISC.AI.Persistence

$env:ISCAI_INSPECTOR_CONNECTION="Server=10.10.0.115;Database=ISC_AI;Username=postgres;Password=<пароль>"
dotnet ef database update --context InspectorDbContext --project src/profiles/inspector/ISC.AI.Profile.Inspector.Data --startup-project src/profiles/inspector/ISC.AI.Profile.Inspector.Data
```

> `$env:...` кладёт значение только в память **текущего окна** терминала — в файл не пишется, при закрытии
> окна исчезает. `<пароль>` — реальный пароль БД (тот же, что в п.1); в репозиторий его **не коммитить**.

### 3. Сборка и тесты
```powershell
dotnet build ISC.AI.slnx
dotnet test tests/ISC.AI.UnitTests/ISC.AI.UnitTests.csproj
```
Интеграционные тесты (`tests/ISC.AI.IntegrationTests`) требуют запущенного **Docker** (Testcontainers + Postgres/pgvector).
