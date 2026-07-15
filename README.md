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

**Сервер (прод):** вместо user-secrets — переменная окружения `Database__Password=<пароль>`
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
