# Сборка пакета поставки «Следствие AI» для сервера Linux в изолированном контуре (ТИ-001/002, ТБ-051; инструкция —
# docs/следствие/Развёртывание_Следствие_Linux.md). Устроен как пакет «ИнспекторAI» (build-inspector-package.ps1) и ставится
# теми же скриптами package/ (install.sh, migrate.sh, verify.sh, account.sh): своё у «Следствия» — состав и шаблоны настроек.
#
# Запускается ВНЕ контура, на машине разработки с интернетом. Сначала — выгрузки офлайн-составных частей (каждая со своей
# проверкой лицензии и пинами SHA-256), пакет берёт их готовыми и сверяет по манифестам:
#   .\deploy\offline\export-tessdata.ps1                                  — OCR: rus, kir
#   .\deploy\offline\export-vision-models.ps1                             — лица: YuNet, SFace
#   .\deploy\offline\export-speech-models.ps1 [-Speakers]                 — речь: GigaAM, Silero VAD, дикторы
#   .\deploy\offline\export-ffmpeg.ps1 -Platform linux-x64                — ffmpeg LGPL для Linux
#   .\deploy\offline\export-sherpa-no-tts.ps1                             — sherpa-onnx без синтеза речи (GPL-чистая)
#   .\deploy\offline\publish-speech-worker.ps1 -Runtime linux-x64 -SelfContained   — распознаватель речи со своей средой .NET
#
#   .\deploy\linux\build-investigation-package.ps1 [-Version 2026.11.03-1] [-OutputRoot <каталог>] [-NoArchive]
#                                                  [-SpeechModel 600m|220m]
#
# Результат (по умолчанию в artifacts\deploy, каталог в git не попадает):
#   isc-ai-investigation-<версия>-linux-x64\           — пакет: app, tools, speech-worker, models, ffmpeg, sql, tessdata,
#                                                        config, systemd, скрипты, docs
#   isc-ai-investigation-<версия>-linux-x64.tar.gz     — он же одним архивом для носителя
#   isc-ai-investigation-<версия>-linux-x64.tar.gz.sha256
# В пакете SHA256SUMS — хеш каждого файла; verify.sh сверяет их в контуре до установки (ТБ-051).
#
# Программа и распознаватель речи публикуются со средой .NET (self-contained): на сервер .NET ставить не нужно. Профиль
# задаётся явно (IscProfile=investigation), состав проверяется: сборки другого профиля роняют хост при старте, а без
# нативного Skia для Linux не обрабатывается ни одно изображение.
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param(
    [string]$Version,
    [string]$OutputRoot,
    [switch]$NoArchive,

    # Модель распознавания речи: 600m — основная (точнее, ADR-0026 п. 13), 220m — вдвое быстрее, для слабого сервера.
    [ValidateSet('600m', '220m')]
    [string]$SpeechModel = '600m'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts\deploy' }
$hostProject = Join-Path $repoRoot 'src\core\ISC.AI.Web\ISC.AI.Web.csproj'
$offline = Join-Path $repoRoot 'deploy\offline'

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

# Манифест выгрузки «SHA256  путь» → словарь путь → хеш (пути через «/», хеши — заглавными). Пустые строки и BOM пропускаются.
function Read-Manifest([string]$Path) {
    $map = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)) {
        $line = $line.Trim([char]0xFEFF, ' ')
        if (-not $line) { continue }
        $hash, $file = $line -split '\s+', 2
        $map[$file.TrimStart('*').Replace('\', '/')] = $hash.ToUpperInvariant()
    }
    $map
}

# Папка выгрузки копируется в пакет только целиком и только совпав со своим манифестом: ни подменённого, ни лишнего, ни
# недостающего файла. Сам манифест едет вместе с папкой — по нему же сверяется перенос на сервере.
function Copy-Verified([string]$Source, [string]$ManifestName, [string]$Destination, [string]$What) {
    $manifestPath = Join-Path $Source $ManifestName
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "$What`: нет $manifestPath — сначала выгрузка (см. шапку скрипта)." }
    $expected = Read-Manifest $manifestPath
    $root = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $actual = @{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relative -eq $ManifestName) { continue }
        $actual[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) })
    $extra = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) })
    $changed = @($expected.Keys | Where-Object { $actual.ContainsKey($_) -and $actual[$_] -ne $expected[$_] })
    if ($missing.Count -or $extra.Count -or $changed.Count) {
        throw "$What не совпадает с манифестом $ManifestName — нет: $($missing -join ', '); лишние: $($extra -join ', '); изменены: $($changed -join ', '). Пакет не собран."
    }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Copy-Item -Path (Join-Path $root '*') -Destination $Destination -Recurse -Force
    Write-Host "   $What`: файлов $($actual.Count), совпали с $ManifestName"
}

# Отдельный файл — с пином SHA-256 (модели распознавания лиц: пины в appsettings.json программы).
function Copy-Pinned([string]$Source, [string]$Pin, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Нет $Source — сначала выгрузка (см. шапку скрипта)." }
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $Pin.ToUpperInvariant()) {
        throw "$(Split-Path $Source -Leaf) не совпал с пином SHA-256 из appsettings.json — пакет не собран."
    }
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    Copy-Item -LiteralPath $Source $Destination
}

# Архив с правами Linux. С диска Windows tar берёт для всех файлов 0644, и программы в распакованном архиве не
# запустились бы, а ключи резервных копий создают программой прямо из него, до установки. Поэтому записи архива
# перечисляются спецификацией mtree с явными правами и владельцем root: 0755 — каталоги, скрипты .sh и программы
# (файлы ELF, кроме библиотек .so), остальное — 0644.
function New-LinuxArchive([string]$Root, [string]$Name, [string]$Archive) {
    $epoch = New-Object DateTime 1970, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc)
    $base = Join-Path $Root $Name
    $spec = New-Object System.Collections.Generic.List[string]
    $spec.Add('#mtree')
    foreach ($item in @(Get-Item -LiteralPath $base) + @(Get-ChildItem -LiteralPath $base -Recurse)) {
        $path = $Name + $item.FullName.Substring($base.Length).Replace('\', '/')
        $owner = 'uid=0 gid=0 uname=root gname=root time={0}.0' -f [long][Math]::Floor(($item.LastWriteTimeUtc - $epoch).TotalSeconds)
        if ($item.PSIsContainer) {
            $spec.Add("./$path type=dir mode=0755 $owner")
            continue
        }
        $mode = '0644'
        if ($item.Extension -eq '.sh') { $mode = '0755' }
        elseif ($item.Name -notmatch '\.so(\.|$)' -and $item.Length -ge 4) {
            $magic = New-Object byte[] 4
            $stream = [System.IO.File]::OpenRead($item.FullName)
            try { [void]$stream.Read($magic, 0, 4) } finally { $stream.Dispose() }
            if ($magic[0] -eq 0x7F -and $magic[1] -eq 0x45 -and $magic[2] -eq 0x4C -and $magic[3] -eq 0x46) { $mode = '0755' }
        }
        $spec.Add("./$path type=file mode=$mode $owner contents=$path")
    }
    # Спецификация лежит рядом с каталогом пакета, не в нём: tar читает её и файлы по путям от -C.
    $specPath = Join-Path $Root "$Name.mtree"
    Write-LinuxText $specPath (($spec -join "`n") + "`n")
    try {
        Invoke-Native 'Упаковка tar.gz' { & "$env:SystemRoot\System32\tar.exe" -czf $Archive -C $Root "@$Name.mtree" }
    }
    finally {
        Remove-Item -LiteralPath $specPath -ErrorAction SilentlyContinue
    }
}

# --- Версия пакета: дата + коммит; незакоммиченные правки помечаются «-dirty» (такой пакет в контур не везут).
$commit = (git -C $repoRoot rev-parse HEAD).Trim()
$dirty = [bool](git -C $repoRoot status --porcelain)
if (-not $Version) {
    $Version = (Get-Date -Format 'yyyy.MM.dd') + '-' + $commit.Substring(0, 7)
    if ($dirty) { $Version += '-dirty' }
}
if ($Version -notmatch '^[0-9A-Za-z._-]+$') { throw "Версия «$Version»: допустимы латиница, цифры, точка, дефис, подчёркивание." }

$name = "isc-ai-investigation-$Version-linux-x64"
$pkg = Join-Path $OutputRoot $name
Write-Host "Пакет: $name"
if ($dirty) { Write-Warning 'В рабочей копии есть незакоммиченные правки — версия помечена «-dirty»; для контура соберите пакет из коммита.' }

# --- Готовность выгрузок — до долгой публикации, чтобы не ждать её ради отказа в конце.
Invoke-Native 'Нет инструмента dotnet-ef для SQL-скриптов миграций (dotnet tool install -g dotnet-ef --version 10.*)' {
    dotnet ef --version } | Out-Null
foreach ($required in @(
        @{ Path = 'tessdata\tessdata.sha256'; Script = 'export-tessdata.ps1' },
        @{ Path = 'models\speech\speech-models.sha256'; Script = 'export-speech-models.ps1' },
        @{ Path = 'ffmpeg\linux-x64\ffmpeg.sha256'; Script = 'export-ffmpeg.ps1 -Platform linux-x64' },
        @{ Path = 'speech-worker\linux-x64\worker.sha256'; Script = 'publish-speech-worker.ps1 -Runtime linux-x64 -SelfContained' })) {
    if (-not (Test-Path -LiteralPath (Join-Path $offline $required.Path))) {
        throw "Нет deploy\offline\$($required.Path) — сначала .\deploy\offline\$($required.Script)"
    }
}
# Распознаватель речи — только со своей средой .NET: на сервере dotnet нет, а сборка без неё там не запустится.
if (-not (Test-Path -LiteralPath (Join-Path $offline 'speech-worker\linux-x64\libcoreclr.so'))) {
    throw 'Распознаватель речи для linux-x64 собран без своей среды .NET — пересоберите: .\deploy\offline\publish-speech-worker.ps1 -Runtime linux-x64 -SelfContained'
}

# Пины моделей лиц — те же, что проверяет программа (appsettings.json, ADR-0020): пакет не повезёт файл, который она отвергнет.
$hostSettings = Get-Content -LiteralPath (Join-Path $repoRoot 'src\core\ISC.AI.Web\appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$visionPins = @{
    'face_detection_yunet_2023mar.onnx'   = $hostSettings.Vision.Detector.Sha256
    'face_recognition_sface_2021dec.onnx' = $hostSettings.Vision.Embedder.Sha256
}

# Модель речи: файл и пин — из манифеста выгрузки; в шаблон настроек пакета подставляется выбранная.
$speechManifest = Read-Manifest (Join-Path $offline 'models\speech\speech-models.sha256')
$speechFiles = @("gigaam-multilingual-ctc-$SpeechModel.int8.onnx", 'tokens.txt', 'silero_vad.onnx',
    'LICENSE-GigaAM-Multilingual.txt', 'LICENSE-silero-vad.txt')
$speakersModel = '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
if ($speechManifest.ContainsKey($speakersModel)) { $speechFiles += $speakersModel }
else { Write-Warning 'Модели разделения говорящих нет в выгрузке (export-speech-models.ps1 -Speakers) — расшифровки будут без дикторов.' }

if (Test-Path -LiteralPath $pkg) { Remove-Item -LiteralPath $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null

try {
    Write-Host '== Программа: хост + профиль «Следствие», linux-x64, со средой .NET'
    $app = Join-Path $pkg 'app'
    Invoke-Native 'Публикация хоста' {
        dotnet publish $hostProject -c Release -r linux-x64 --self-contained true -p:IscProfile=investigation -o $app -nologo }

    # Страж состава поставки: профиль «Следствия» и его нативные части на месте, сборок Инспектора нет.
    foreach ($expected in 'ISC.AI.Profile.Investigation.dll', 'ISC.AI.Modules.Media.dll', 'ISC.AI.Vision.Onnx.dll',
            'libSkiaSharp.so', 'libonnxruntime.so') {
        if (-not (Test-Path -LiteralPath (Join-Path $app $expected))) {
            throw "В публикации нет $expected — хост собран не тем профилем или без нативных частей для Linux (SkiaSharp.NativeAssets.Linux)."
        }
    }
    $foreign = @(Get-ChildItem $app -Filter '*.dll' | Where-Object { $_.Name -match '^ISC\.AI\.Profile\.Inspector' })
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

    Write-Host '== Распознаватель речи (отдельный процесс, своя среда .NET и onnxruntime, ADR-0026)'
    Copy-Verified (Join-Path $offline 'speech-worker\linux-x64') 'worker.sha256' (Join-Path $pkg 'speech-worker') 'Распознаватель речи'

    Write-Host '== Идемпотентные SQL-скрипты миграций: core → docflow → media → investigation (ТО-прог-08)'
    $sqlDir = Join-Path $pkg 'sql'
    New-Item -ItemType Directory -Force $sqlDir | Out-Null
    $migrations = @(
        @{ File = '10-core.sql'; Schema = 'core'; Context = 'CoreDbContext'; Project = 'src\core\ISC.AI.Persistence' },
        @{ File = '20-docflow.sql'; Schema = 'docflow'; Context = 'DocFlowDbContext'; Project = 'src\modules\docflow\ISC.AI.Modules.DocFlow.Data' },
        @{ File = '30-media.sql'; Schema = 'media'; Context = 'MediaDbContext'; Project = 'src\modules\media\ISC.AI.Modules.Media.Data' },
        @{ File = '40-investigation.sql'; Schema = 'investigation'; Context = 'InvestigationDbContext'; Project = 'src\profiles\investigation\ISC.AI.Profile.Investigation.Data' }
    )
    foreach ($m in $migrations) {
        $project = Join-Path $repoRoot $m.Project
        $out = Join-Path $sqlDir $m.File
        Invoke-Native "Скрипт миграций $($m.Context)" {
            dotnet ef migrations script --idempotent --project $project --startup-project $project --context $m.Context --output $out }
        Write-LinuxText $out ([System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8))
    }
    # Порядок и схемы — для migrate.sh: тот же скрипт ставит и «ИнспекторAI», у которого схемы другие.
    Write-LinuxText (Join-Path $sqlDir 'ORDER') ((($migrations | ForEach-Object { "$($_.File) $($_.Schema)" }) -join "`n") + "`n")

    Write-Host '== Языковые файлы OCR (rus, kir) — с проверкой пинов SHA256'
    Copy-Verified (Join-Path $offline 'tessdata') 'tessdata.sha256' (Join-Path $pkg 'tessdata') 'OCR'

    Write-Host '== Модели распознавания лиц — с проверкой пинов из appsettings.json'
    foreach ($model in $visionPins.Keys) {
        Copy-Pinned (Join-Path $offline "models\$model") $visionPins[$model] (Join-Path $pkg "models\vision\$model")
    }

    Write-Host "== Модели распознавания речи ($SpeechModel) — с проверкой по speech-models.sha256"
    $speechTarget = Join-Path $pkg 'models\speech'
    New-Item -ItemType Directory -Force $speechTarget | Out-Null
    foreach ($file in $speechFiles) {
        $source = Join-Path $offline "models\speech\$file"
        if (-not $speechManifest.ContainsKey($file)) { throw "$file нет в speech-models.sha256 — выгрузка неполная." }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $speechManifest[$file]) {
            throw "$file не совпал с speech-models.sha256 — пакет не собран."
        }
        Copy-Item -LiteralPath $source (Join-Path $speechTarget $file)
    }

    Write-Host '== ffmpeg LGPL для Linux (раскадровка видео, звук для расшифровки, ADR-0020)'
    Copy-Verified (Join-Path $offline 'ffmpeg\linux-x64') 'ffmpeg.sha256' (Join-Path $pkg 'ffmpeg') 'ffmpeg'

    Write-Host '== Скрипты установки, служба, шаблоны настроек «Следствия», руководство'
    $packageSource = Join-Path $PSScriptRoot 'package'
    Get-ChildItem $packageSource -Recurse -File | ForEach-Object {
        Copy-LinuxText $_.FullName (Join-Path $pkg $_.FullName.Substring($packageSource.Length + 1))
    }
    # Шаблоны настроек «Следствия» — поверх общих (у Инспектора свои схемы БД и нет моделей).
    $investigationSource = Join-Path $PSScriptRoot 'package-investigation'
    Get-ChildItem $investigationSource -Recurse -File | ForEach-Object {
        Copy-LinuxText $_.FullName (Join-Path $pkg $_.FullName.Substring($investigationSource.Length + 1))
    }
    # Модель речи в шаблоне — та, что в пакете: имя файла и пин из манифеста выгрузки.
    if ($SpeechModel -ne '600m') {
        $template = Join-Path $pkg 'config\appsettings.Production.json'
        $text = [System.IO.File]::ReadAllText($template, [System.Text.Encoding]::UTF8)
        $text = $text.Replace('gigaam-multilingual-ctc-600m.int8.onnx', "gigaam-multilingual-ctc-$SpeechModel.int8.onnx").
            Replace($speechManifest['gigaam-multilingual-ctc-600m.int8.onnx'], $speechManifest["gigaam-multilingual-ctc-$SpeechModel.int8.onnx"])
        Write-LinuxText $template $text
    }
    # Руководство кладётся под латинским именем: имена файлов внутри архива — только ASCII (tar и sha256sum на любом
    # сервере читают их одинаково).
    Copy-LinuxText (Join-Path $repoRoot 'docs\следствие\Развёртывание_Следствие_Linux.md') (Join-Path $pkg 'docs\INSTALL.ru.md')

    Write-LinuxText (Join-Path $pkg 'VERSION') (
        "$Version`nprofile=investigation`ncommit=$commit`nbuilt=$((Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz'))`n" +
        "runtime=linux-x64 self-contained`nspeech-model=$SpeechModel`n")

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
        New-LinuxArchive $OutputRoot $name $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-LinuxText "$archive.sha256" "$hash  $name.tar.gz`n"
        Write-Host "   $archive"
        Write-Host "   SHA256 $hash"
    }

    Write-Host ''
    Write-Host "Готово: $pkg"
    Write-Host 'Дальше: перенос на носителе → в контуре «sha256sum -c *.tar.gz.sha256», распаковка, «bash verify.sh», «sudo bash install.sh» (docs/INSTALL.ru.md).'
}
finally {
    # Публикация переписала общий obj хоста под linux-x64. Возвращаем восстановление пакетов к профилю этой машины —
    # иначе следующая сборка в VS увидит чужие ресурсы. Профиль машины — переменная IscProfile процесса, а если её нет —
    # пользовательская переменная Windows (процесс, запущенный до её установки, её не видит); нет и её — профиль по
    # умолчанию из csproj («Следствие»).
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
