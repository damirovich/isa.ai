# Сборка пакета поставки «ИнспекторAI» для сервера Linux в изолированном контуре (ДОК-15; ТИ-001/002, ТБ-051).
# Запускается ВНЕ контура, на машине разработки с интернетом: при первом запуске NuGet скачивает в свой кэш пакеты
# среды .NET для linux-x64 (Microsoft.NETCore.App.Runtime / AspNetCore.App.Runtime / NETCore.App.Host, MIT).
#
#   .\deploy\linux\build-inspector-package.ps1 [-Version 2026.10.28-1] [-OutputRoot <каталог>] [-NoArchive]
#
# Результат (по умолчанию в artifacts\deploy, каталог в git не попадает):
#   isc-ai-inspector-<версия>-linux-x64\           — пакет: app, tools, sql, tessdata, config, systemd, скрипты, docs
#   isc-ai-inspector-<версия>-linux-x64.tar.gz     — он же одним архивом для носителя
#   isc-ai-inspector-<версия>-linux-x64.tar.gz.sha256
# В пакете SHA256SUMS — хеш каждого файла; verify.sh сверяет их в контуре до установки (ТБ-051).
#
# Программа публикуется со средой .NET (self-contained): на сервер в контуре .NET ставить не нужно. Профиль задаётся
# здесь явно (IscProfile=inspector), а не берётся из окружения машины, и состав сборок проверяется: сборки другого
# профиля в поставке роняют хост при старте (обработчики «Следствия» без своих хранилищ).
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param(
    [string]$Version,
    [string]$OutputRoot,
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts\deploy' }
$hostProject = Join-Path $repoRoot 'src\core\ISC.AI.Web\ISC.AI.Web.csproj'

# Внешняя программа (dotnet, tar): успех — по коду возврата. Её поток ошибок выводится как обычный текст: Windows
# PowerShell 5.1 при ErrorActionPreference=Stop иначе превращает любое предупреждение в stderr (например, NU1902 от
# NuGet) в прерывающую ошибку, и сборка падает на ровном месте.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $saved
    }
    if ($LASTEXITCODE -ne 0) { throw "$What — завершилось с ошибкой (код $LASTEXITCODE), пакет не собран." }
}

# Текстовые файлы для Linux: UTF-8 без BOM и только LF. BOM ломает psql и первую строку «#!» скриптов, CR — bash и
# значения EnvironmentFile у systemd. Дублирует правило .gitattributes на случай рабочей копии без него.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-LinuxText([string]$Path, [string]$Text) {
    $Text = $Text.TrimStart([char]0xFEFF) -replace "`r`n", "`n" -replace "`r", "`n"
    [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}
function Copy-LinuxText([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    Write-LinuxText $Destination ([System.IO.File]::ReadAllText($Source, [System.Text.Encoding]::UTF8))
}

# --- Версия пакета: дата + коммит; незакоммиченные правки помечаются «-dirty» (такой пакет в контур не везут).
$commit = (git -C $repoRoot rev-parse HEAD).Trim()
$dirty = [bool](git -C $repoRoot status --porcelain)
if (-not $Version) {
    $Version = (Get-Date -Format 'yyyy.MM.dd') + '-' + $commit.Substring(0, 7)
    if ($dirty) { $Version += '-dirty' }
}
if ($Version -notmatch '^[0-9A-Za-z._-]+$') { throw "Версия «$Version»: допустимы латиница, цифры, точка, дефис, подчёркивание." }

$name = "isc-ai-inspector-$Version-linux-x64"
$pkg = Join-Path $OutputRoot $name
Write-Host "Пакет: $name"
if ($dirty) { Write-Warning 'В рабочей копии есть незакоммиченные правки — версия помечена «-dirty»; для контура соберите пакет из коммита.' }

Invoke-Native 'Нет инструмента dotnet-ef для SQL-скриптов миграций (dotnet tool install -g dotnet-ef --version 10.*)' {
    dotnet ef --version } | Out-Null

$tessSource = Join-Path $repoRoot 'deploy\offline\tessdata'
if (-not (Test-Path -LiteralPath (Join-Path $tessSource 'tessdata.sha256'))) {
    throw 'Нет языковых файлов OCR в deploy\offline\tessdata — сначала .\deploy\offline\export-tessdata.ps1'
}

if (Test-Path -LiteralPath $pkg) { Remove-Item -LiteralPath $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null

try {
    Write-Host '== Программа: хост + профиль «ИнспекторAI», linux-x64, со средой .NET'
    $app = Join-Path $pkg 'app'
    Invoke-Native 'Публикация хоста' {
        dotnet publish $hostProject -c Release -r linux-x64 --self-contained true -p:IscProfile=inspector -o $app -nologo }

    # Страж состава поставки: профиль Инспектора на месте, сборок другого профиля и его пакетов нет.
    if (-not (Test-Path -LiteralPath (Join-Path $app 'ISC.AI.Profile.Inspector.dll'))) {
        throw 'В публикации нет ISC.AI.Profile.Inspector.dll — хост собран не тем профилем.'
    }
    $foreign = @(Get-ChildItem $app -Filter '*.dll' |
        Where-Object { $_.Name -match '^ISC\.AI\.(Profile\.Investigation|Modules\.Media|Vision|Speech)' })
    if ($foreign.Count) {
        throw ('В поставку попали сборки другого профиля: ' + (($foreign | ForEach-Object Name) -join ', ') +
            '. Выполните dotnet clean src\core\ISC.AI.Web и повторите.')
    }
    # Дев-настройки серверу не нужны (в них пути и строки дев-машины). Библиотеки OCR из пакета NuGet — только для
    # Windows; на Linux install.sh подставляет системные под ожидаемыми именами.
    Get-ChildItem $app -Filter 'appsettings.Development*.json' | Remove-Item -Force
    foreach ($dir in 'x64', 'x86') {
        $native = Join-Path $app $dir
        if (Test-Path -LiteralPath $native) { Remove-Item -LiteralPath $native -Recurse -Force }
    }

    Write-Host '== Программа восстановления из резервной копии (ADR-0063)'
    Invoke-Native 'Публикация ISC.AI.Backup.Restore' {
        dotnet publish (Join-Path $repoRoot 'src\tools\ISC.AI.Backup.Restore\ISC.AI.Backup.Restore.csproj') `
            -c Release -r linux-x64 --self-contained true -o (Join-Path $pkg 'tools\backup-restore') -nologo }

    Write-Host '== Идемпотентные SQL-скрипты миграций: core → docflow → inspector (ТО-прог-08)'
    $sqlDir = Join-Path $pkg 'sql'
    New-Item -ItemType Directory -Force $sqlDir | Out-Null
    $migrations = @(
        @{ File = '10-core.sql'; Context = 'CoreDbContext'; Project = 'src\core\ISC.AI.Persistence' },
        @{ File = '20-docflow.sql'; Context = 'DocFlowDbContext'; Project = 'src\modules\docflow\ISC.AI.Modules.DocFlow.Data' },
        @{ File = '30-inspector.sql'; Context = 'InspectorDbContext'; Project = 'src\profiles\inspector\ISC.AI.Profile.Inspector.Data' }
    )
    foreach ($m in $migrations) {
        $project = Join-Path $repoRoot $m.Project
        $out = Join-Path $sqlDir $m.File
        Invoke-Native "Скрипт миграций $($m.Context)" {
            dotnet ef migrations script --idempotent --project $project --startup-project $project --context $m.Context --output $out }
        Write-LinuxText $out ([System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8))
    }

    Write-Host '== Языковые файлы OCR (rus, kir) — с проверкой пинов SHA256'
    $tessTarget = Join-Path $pkg 'tessdata'
    New-Item -ItemType Directory -Force $tessTarget | Out-Null
    foreach ($line in (Get-Content -LiteralPath (Join-Path $tessSource 'tessdata.sha256') -Encoding UTF8)) {
        $line = $line.Trim([char]0xFEFF, ' ')
        if (-not $line) { continue }
        $expected, $file = $line -split '\s+', 2
        $source = Join-Path $tessSource $file
        $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ($actual -ne $expected.ToUpperInvariant()) { throw "Языковой файл $file не совпал с пином SHA256 — пакет не собран." }
        Copy-Item -LiteralPath $source (Join-Path $tessTarget $file)
    }

    Write-Host '== Скрипты установки, служба, шаблоны настроек, руководство'
    $packageSource = Join-Path $PSScriptRoot 'package'
    Get-ChildItem $packageSource -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($packageSource.Length + 1)
        Copy-LinuxText $_.FullName (Join-Path $pkg $relative)
    }
    # Руководство кладётся под латинским именем: имена файлов внутри архива — только ASCII (tar и sha256sum на любом
    # сервере читают их одинаково).
    Copy-LinuxText (Join-Path $repoRoot 'docs\15_Развёртывание_ИнспекторAI_Linux.md') (Join-Path $pkg 'docs\INSTALL.ru.md')

    Write-LinuxText (Join-Path $pkg 'VERSION') (
        "$Version`nprofile=inspector`ncommit=$commit`nbuilt=$((Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz'))`n" +
        "runtime=linux-x64 self-contained`n")

    Write-Host '== Манифест SHA256SUMS (ТБ-051)'
    $root = (Resolve-Path -LiteralPath $pkg).Path
    $lines = Get-ChildItem $root -Recurse -File |
        ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($relative -ne 'SHA256SUMS') {
                if ($relative -notmatch '^[\x21-\x7E]+$') { throw "Имя файла вне ASCII или с пробелом: $relative" }
                '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
            }
        } | Sort-Object { $_.Substring(66) } -CaseSensitive
    Write-LinuxText (Join-Path $root 'SHA256SUMS') (($lines -join "`n") + "`n")
    Write-Host "   файлов: $($lines.Count)"

    if (-not $NoArchive) {
        Write-Host '== Архив для носителя'
        $archive = Join-Path $OutputRoot "$name.tar.gz"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Invoke-Native 'Упаковка tar.gz' { & "$env:SystemRoot\System32\tar.exe" -czf $archive -C $OutputRoot $name }
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-LinuxText "$archive.sha256" "$hash  $name.tar.gz`n"
        Write-Host "   $archive"
        Write-Host "   SHA256 $hash"
    }

    Write-Host ''
    Write-Host "Готово: $pkg"
    Write-Host 'Дальше: перенос на носителе → в контуре «sha256sum -c *.tar.gz.sha256», распаковка, «bash verify.sh», «sudo bash install.sh» (ДОК-15).'
}
finally {
    # Публикация переписала общий obj хоста под профиль Инспектора. Возвращаем восстановление пакетов к профилю этой
    # машины — иначе следующая сборка в VS смешает профили. Профиль машины — переменная IscProfile процесса, а если её
    # нет — пользовательская переменная Windows (процесс, запущенный до её установки, её не видит); нет и её — профиль
    # по умолчанию из csproj («Следствие»).
    $machineProfile = $env:IscProfile
    if (-not $machineProfile) { $machineProfile = [Environment]::GetEnvironmentVariable('IscProfile', 'User') }
    if ($machineProfile) {
        Invoke-Native "Восстановление пакетов хоста под профиль машины ($machineProfile)" {
            dotnet restore $hostProject -nologo "-p:IscProfile=$machineProfile" } | Out-Null
    }
    else {
        Invoke-Native 'Восстановление пакетов хоста под профиль по умолчанию' { dotnet restore $hostProject -nologo } | Out-Null
    }
}
