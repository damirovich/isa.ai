# Поставка процесса-распознавателя речи ISC.AI.Speech.Worker профиля «Следствие» (ADR-0026, ТИ-004;
# перенос — как у фида, ТБ-050/051). Собирает утилиту ИЗ ИСХОДНИКОВ репозитория (dotnet publish), подставляет
# нативную библиотеку sherpa-onnx БЕЗ синтеза речи, проверяет комплектность и лицензионную чистоту нативных
# библиотек и пишет манифест целостности worker.sha256.
# Результат: deploy/offline/speech-worker/<rid> (в git НЕ хранится — бинарники, как ffmpeg и модели).
#
# ПОЧЕМУ РАСПОЗНАВАТЕЛЬ — ОТДЕЛЬНЫЙ ПРОЦЕСС (ADR-0026, п. 1). Нативная сборка sherpa-onnx несёт СВОЙ
# onnxruntime (1.28.2), распознавание лиц хоста — свой (Microsoft.ML.OnnxRuntime 1.30). Оба пакета кладут
# библиотеку по ОДНОМУ пути runtimes/win-x64/native/onnxruntime.dll, и это РАЗНЫЕ сборки (≈16,5 и 17,8 МБ,
# проверено координатором) — в одном процессе/каталоге одна затёрла бы другую, и одна из подсистем упала бы
# на несовместимом API. Поэтому:
#   * утилита публикуется в СОБСТВЕННУЮ папку и НИКОГДА не кладётся в каталог хоста: RID-публикация
#     разворачивает runtimes/<rid>/native в корень (проверено 25.09.2026), onnxruntime.dll лежит рядом с exe;
#   * в зависимостях утилиты не должно быть Microsoft.ML.OnnxRuntime (второй поставщик той же библиотеки =
#     тот же конфликт) — скрипт это проверяет.
# Заодно длинная расшифровка (часы звука) не делит память и потоки с хостом.
#
# ЛИЦЕНЗИИ (правило проекта — только MIT/Apache, ТСТ-001/004):
#   * sherpa-onnx (k2-fsa) — Apache-2.0: управляемая обёртка sherpa-onnx.dll (из NuGet) и нативная sherpa-onnx-c-api;
#   * onnxruntime (Microsoft) — MIT, входит в нативную сборку sherpa-onnx;
#   * .NET — MIT (в поставку входит только при -SelfContained).
# !!! НАТИВНАЯ БИБЛИОТЕКА — ТОЛЬКО СБОРКА БЕЗ СИНТЕЗА РЕЧИ (ADR-0026, п. 7). Нативные библиотеки из NuGet
# (org.k2fsa.sherpa.onnx.runtime.* 1.13.8, win-x64 и linux-x64) собраны с синтезом речи и СТАТИЧЕСКИ содержат
# eSpeak NG (GPL-3.0-or-later): 25 функций espeak_ng_* в sherpa-onnx-c-api.dll. Поставлять их нельзя. Поэтому:
#   1) ПО УМОЛЧАНИЮ нативные sherpa-onnx-c-api и onnxruntime берутся из официальной сборки того же релиза без
#      синтеза речи — deploy/offline/sherpa-no-tts/<rid>/<сборка>/lib (скачивает и проверяет по пинам
#      export-sherpa-no-tts.ps1; здесь сверяются её манифест sherpa-no-tts.sha256 и версия с пакетом). Библиотеки
#      пакета, развёрнутые публикацией, заменяются ими на ТЕХ ЖЕ местах. Нет папки — отказ с подсказкой;
#   2) -NativeDir <папка> — СОБСТВЕННАЯ сборка из исходников того же тега (cmake -DSHERPA_ONNX_ENABLE_TTS=OFF
#      -DBUILD_SHARED_LIBS=ON ...) вместо официальной;
#   3) крайний случай — -AllowGplEspeakNg: если сборки без синтеза речи нет, допускаются библиотеки NuGet с eSpeak
#      NG (решение заказчика после юридической оценки; предупреждение и запись в THIRD-PARTY-NOTICES.txt; для
#      пилота/стенда, не как молчаливое умолчание). Сборка без синтеза речи при наличии берётся и с этим ключом.
# Какой бы ни была сборка, ВТОРАЯ проверка лицензии — по содержимому бинарников (как флаги сборки у ffmpeg):
# функции espeak_ng_*, строки «espeak-ng-data», «mbrola voice file». Поиск С УЧЁТОМ РЕГИСТРА: подстрока «espeak»
# без учёта регистра есть и в сборке без синтеза — в имени OfflineSpeakerDiarization («offlin-eSpeak-er»,
# разделение по голосам); это ложное срабатывание. Проверяются ВСЕ нативные файлы публикации (*.dll, *.so,
# *.so.*, *.dylib, кроме управляемых сборок), а не только две подменённые библиотеки: в другом релизе пакет может
# развернуть ещё одну библиотеку. Нативный файл сверх пары sherpa-onnx-c-api + onnxruntime (и, при -SelfContained,
# библиотек среды .NET из deps.json) — отказ «ЛИШНЯЯ нативная библиотека»: состав изменился, смотреть вручную.
#
# ТЕКСТЫ ЛИЦЕНЗИЙ В ПОСТАВКЕ. Apache-2.0 (§4(a)) и MIT требуют передавать текст лицензии вместе с копией, а в
# контуре ссылки на сайты не открываются. Поэтому в папку публикации кладутся тексты из deploy/offline/licenses
# (хранятся в git, взяты из пакетов NuGet без скачивания): LICENSE-sherpa-onnx.txt — стандартный текст Apache-2.0
# (лицензия пакета org.k2fsa.sherpa.onnx по его nuspec), LICENSE-onnxruntime.txt — MIT onnxruntime (© Microsoft;
# файл LICENSE пакета Microsoft.ML.OnnxRuntime, одинаковый во всех версиях). При -SelfContained — ещё
# LICENSE-dotnet.txt и THIRD-PARTY-NOTICES-dotnet.txt из runtime-пакета .NET той версии, что вошла в поставку.
# THIRD-PARTY-NOTICES.txt ссылается на эти файлы, а не на адреса в интернете. Нет текста или он не тот — отказ
# до сборки (как у export-ffmpeg.ps1 без LICENSE.txt). Необязательный ThirdPartyNotices-onnxruntime.txt
# (уведомления о компонентах внутри onnxruntime) кладётся, если лежит в той же папке: он должен быть от ТОЙ ЖЕ
# версии onnxruntime, что в сборке sherpa-onnx (см. строку «onnxruntime» в итоговом THIRD-PARTY-NOTICES.txt).
#
# СРЕДА C++ НА WINDOWS-СЕРВЕРЕ. export-sherpa-no-tts.ps1 ставит вариант MT (среда C++ вшита статически), поэтому
# Microsoft Visual C++ Redistributable НЕ нужен. Скрипт всё равно смотрит таблицу импорта нативных библиотек:
# если однажды подставят MD-сборку (импорты MSVCP140/VCRUNTIME140), он напечатает требование, а -Verify проверит
# наличие среды на машине, где запущен, — вместо загадочного кода 3 «нативная библиотека не загрузилась».
# Linux-сборка требований не добавляет.
#
# РЕЖИМ ПУБЛИКАЦИИ. Хост ставится framework-dependent (docs/следствие/Развёртывание_профиля_Следствие.md:
# dotnet publish ... -c Release без --self-contained) — на сервере уже стоит .NET 10 runtime, поэтому и утилита
# по умолчанию --no-self-contained (≈21 МБ, почти всё — нативные библиотеки). Сервер без runtime — -SelfContained.
# Сборка идёт во ВРЕМЕННУЮ папку артефактов (--artifacts-path): каждый раз чистая, не трогает bin/obj
# разработчика и открытую Visual Studio; после работы удаляется.
#
# ГДЕ ЗАПУСКАТЬ. Только ВНЕ изолированного контура: на машине с .NET SDK 10, доступом к nuget.org (корневой
# nuget.config) и папкой deploy/offline/sherpa-no-tts (export-sherpa-no-tts.ps1). В контур переносится ГОТОВАЯ
# папка публикации с манифестом worker.sha256, а не пакеты: пакетов sherpa-onnx в общем офлайн-фиде НЕТ и быть не
# должно — их нативные библиотеки из NuGet содержат eSpeak NG (GPL-3.0). Фид собирается по ISC.AI.Offline.slnf,
# где утилиты нет, а export-packages.ps1 и verify-packages.ps1 отказывают при таком пакете (ADR-0026, п. 7).
# Поэтому восстановления из офлайн-фида у скрипта нет: собрать утилиту в контуре не из чего, и это намеренно.
#
# ПРОВЕРКА ПОСЛЕ ПЕРЕНОСА (в контуре, без SDK): publish-speech-worker.ps1 -Verify [-WorkerDir <папка>] —
# сверка по worker.sha256 (несовпавший, отсутствующий и ЛИШНИЙ файл — отказ) и наличие текстов лицензий, с
# записью в transfer-log.txt; на Windows — и наличие среды C++ (Visual C++ Redistributable) на этой машине. Рядом
# со скриптом должен лежать offline-common.ps1 (папка deploy/offline переносится целиком).
# На Linux-сервере без PowerShell — в папке поставки: sha256sum -c worker.sha256 (лишние файлы он не ищет).
# Неполная папка опаснее, чем кажется: без onnxruntime.dll рядом с утилитой Windows подхватывает СИСТЕМНУЮ
# C:\Windows\System32\onnxruntime.dll (Windows ML, 1.10, API 1–10), и процесс падает аварийно (0xC0000005), а не
# с понятной ошибкой «библиотека не найдена» (проверено 25.09.2026). Поэтому сверка по манифесту обязательна.
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param(
    # Платформа сервера: win-x64 (по умолчанию) или linux-x64.
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Runtime = 'win-x64',

    # Включить .NET runtime в поставку (для сервера, где .NET 10 не установлен).
    [switch]$SelfContained,

    # Папка поставки (по умолчанию deploy/offline/speech-worker/<rid>); при -Verify — проверяемая папка.
    [string]$WorkerDir = '',

    # Папка текстов лицензий sherpa-onnx и onnxruntime, которые кладутся в поставку (см. шапку).
    [string]$LicenseDir = "$PSScriptRoot\licenses",

    # Корень официальных сборок sherpa-onnx без синтеза речи (в нём папки <rid>; создаёт export-sherpa-no-tts.ps1).
    [string]$NoTtsDir = "$PSScriptRoot\sherpa-no-tts",

    # Папка СОБСТВЕННОЙ нативной сборки sherpa-onnx без синтеза речи (sherpa-onnx-c-api + onnxruntime) — вместо
    # официальной из -NoTtsDir.
    [string]$NativeDir = '',

    # Крайний случай: сборки без синтеза речи нет — допустить нативные библиотеки NuGet с eSpeak NG (GPL-3.0).
    # Только решением заказчика.
    [switch]$AllowGplEspeakNg,

    # Не собирать, а проверить готовую папку по манифесту worker.sha256 (после переноса за периметр).
    [switch]$Verify,

    # Проект утилиты (параметр — для проверки скрипта на копии проекта; в поставке не менять).
    [string]$ProjectPath = "$PSScriptRoot\..\..\src\integrations\ISC.AI.Speech.Worker\ISC.AI.Speech.Worker.csproj"
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Общие функции поставки: Get-RelativeFiles, Get-Sha256, Find-AsciiMarker, Get-CrtImports и признаки eSpeak NG
# $GplMarkers (тот же список — в export-sherpa-no-tts.ps1 и в проверке офлайн-фида).
. (Join-Path $PSScriptRoot 'offline-common.ps1')

if (-not $WorkerDir) { $WorkerDir = Join-Path $PSScriptRoot "speech-worker\$Runtime" }
$manifestName = 'worker.sha256'
$noTtsManifestName = 'sherpa-no-tts.sha256'
$noticesName = 'THIRD-PARTY-NOTICES.txt'

# Тексты лицензий, ОБЯЗАТЕЛЬНЫЕ в поставке (ТСТ-001/004; см. шапку): имя файла → признаки, по которым текст
# узнаётся. Признаки защищают от пустого или перепутанного файла; переводы строк не важны (git на Windows
# может выдать файл с CRLF).
$requiredLicenses = [ordered]@{
    'LICENSE-sherpa-onnx.txt' = @('Apache License', 'Version 2.0, January 2004', 'TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION')
    'LICENSE-onnxruntime.txt' = @('MIT License', 'Copyright (c) Microsoft Corporation', 'Permission is hereby granted, free of charge')
}
# Необязательные уведомления: кладутся, если лежат в -LicenseDir.
$optionalNotices = @('ThirdPartyNotices-onnxruntime.txt')

# Имена файлов по платформе. Нативные имена — как в пакетах org.k2fsa.sherpa.onnx.runtime.<rid> 1.13.8 и в
# официальных сборках без синтеза речи того же релиза.
if ($Runtime -like 'win-*') {
    $names = @{ AppHost = 'ISC.AI.Speech.Worker.exe'; Sherpa = 'sherpa-onnx-c-api.dll'; Onnx = 'onnxruntime.dll' }
}
else {
    $names = @{ AppHost = 'ISC.AI.Speech.Worker'; Sherpa = 'libsherpa-onnx-c-api.so'; Onnx = 'libonnxruntime.so' }
}

# Текст лицензии не найден или не тот: сообщение о проблеме; $null — файл на месте и узнан по признакам.
function Test-LicenseText([string]$Path, [string[]]$Markers) {
    if (-not (Test-Path -LiteralPath $Path)) { return "нет текста лицензии $Path" }
    $text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).ProviderPath) -replace "`r`n", "`n"
    $missing = @($Markers | Where-Object { $text.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
    if ($missing.Count) { return "$Path не похож на нужный текст лицензии (нет строк: «$($missing -join '», «')»)" }
    return $null
}

# Управляемая ли это сборка .NET: у нативной DLL нет метаданных CLI, и чтение имени сборки падает с
# BadImageFormatException. .so/.dylib управляемыми не бывают. Любой другой сбой — «нативная» (такой файл
# проверяется на eSpeak NG и попадает под «лишнюю нативную», то есть ошибка громкая, а не тихая).
function Test-ManagedAssembly([IO.FileInfo]$File) {
    if ($File.Extension -ne '.dll') { return $false }
    try { [void][Reflection.AssemblyName]::GetAssemblyName($File.FullName); return $true }
    catch { return $false }
}

# Нативные файлы публикации (относительные пути с «/»): *.dll, *.so, *.so.*, *.dylib без управляемых сборок.
function Get-NativeFiles([string]$Root) {
    $full = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('\', '/')
    [string[]]$list = @(Get-ChildItem -LiteralPath $full -Recurse -File -Force |
            Where-Object { $_.Name -like '*.dll' -or $_.Name -like '*.so' -or $_.Name -like '*.so.*' -or $_.Name -like '*.dylib' } |
            Where-Object { -not (Test-ManagedAssembly $_) } |
            ForEach-Object { $_.FullName.Substring($full.Length + 1) -replace '\\', '/' })
    [Array]::Sort($list, [StringComparer]::Ordinal)
    $list
}

# Какие из нужных библиотек среды C++ НЕ найдутся при загрузке: ищутся рядом с нативной библиотекой и в
# системном каталоге (64-битном: из 32-битного PowerShell System32 перенаправлен в SysWOW64, отсюда Sysnative).
function Get-MissingCrt([string[]]$Crt, [string]$NativeFolder) {
    $system = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        Join-Path $env:SystemRoot 'Sysnative'
    } else { [Environment]::SystemDirectory }
    @($Crt | Where-Object { -not (Test-Path -LiteralPath (Join-Path $system $_)) -and -not (Test-Path -LiteralPath (Join-Path $NativeFolder $_)) })
}

# Нативная библиотека: при RID-публикации — в корне, при переносимой — в runtimes/<rid>/native.
function Find-Native([string]$Root, [string]$Name) {
    foreach ($candidate in (Join-Path $Root $Name), (Join-Path $Root "runtimes\$Runtime\native\$Name")) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

# Сверка папки с манифестом (ТБ-051): список расхождений; пустой — целостность подтверждена.
function Test-Manifest([string]$Root, [string]$Manifest = $manifestName) {
    $bad = @()
    $manifestPath = Join-Path $Root $Manifest
    if (-not (Test-Path -LiteralPath $manifestPath)) { return @("НЕТ МАНИФЕСТА: $manifestPath") }
    $listed = @()
    foreach ($line in (Get-Content -LiteralPath $manifestPath -Encoding UTF8 | Where-Object { $_ -match '\S' })) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { $bad += "ИСПОРЧЕНА СТРОКА МАНИФЕСТА: $line"; continue }
        $hash = $Matches[1]; $relative = $Matches[2].Trim()
        $listed += $relative
        $file = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $file)) { $bad += "ОТСУТСТВУЕТ: $relative" }
        elseif ((Get-Sha256 $file) -ne $hash.ToUpperInvariant()) { $bad += "ХЕШ НЕ СОВПАЛ: $relative" }
    }
    foreach ($relative in (Get-RelativeFiles $Root)) {
        if ($relative -ne $Manifest -and $listed -notcontains $relative) { $bad += "ВНЕ МАНИФЕСТА: $relative" }
    }
    return $bad
}

# ======================= Режим проверки после переноса =======================
if ($Verify) {
    if (-not (Test-Path -LiteralPath $WorkerDir)) { throw "Папка распознавателя не найдена: $WorkerDir (укажите -WorkerDir)." }
    $bad = @(Test-Manifest $WorkerDir)
    # Тексты лицензий — условие передачи (Apache-2.0 §4(a), MIT): поставка, опубликованная до их появления в
    # скрипте, по манифесту сойдётся, но в контур уходить не должна — её нужно опубликовать заново.
    foreach ($name in $requiredLicenses.Keys) {
        $problem = Test-LicenseText (Join-Path $WorkerDir $name) $requiredLicenses[$name]
        if ($problem) { $bad += "ЛИЦЕНЗИЯ: $problem" }
    }
    $count = @(Get-Content -LiteralPath (Join-Path $WorkerDir $manifestName) -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match '\S' }).Count
    $verdict = if ($bad.Count) { 'ОТКЛОНЕНО: ' + ($bad -join '; ') } else { 'целостность подтверждена' }

    # Windows-поставка со сборкой MD: без среды C++ на ЭТОЙ машине sherpa-onnx не загрузится. Это не нарушение
    # целостности (файлы верны), а условие запуска — предупреждение и отметка в журнале.
    $missingCrt = @()
    $winNative = Find-Native $WorkerDir 'sherpa-onnx-c-api.dll'
    if (-not $bad.Count -and $winNative -and $env:OS -eq 'Windows_NT') {
        $natives = @('sherpa-onnx-c-api.dll', 'onnxruntime.dll' | ForEach-Object { Find-Native $WorkerDir $_ } | Where-Object { $_ })
        $missingCrt = @(Get-MissingCrt (Get-CrtImports $natives) (Split-Path $winNative -Parent))
        if ($missingCrt.Count) { $verdict += "; ВНИМАНИЕ: на этой машине нет $($missingCrt -join ', ') (Visual C++ Redistributable x64)" }
    }

    $entry = '{0} | распознаватель речи {1} | файлов {2} | {3} | {4}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $WorkerDir, $count, $env:USERNAME, $verdict
    Add-Content (Join-Path $PSScriptRoot 'transfer-log.txt') $entry -Encoding utf8
    if ($bad.Count) {
        $bad | ForEach-Object { Write-Host $_ }
        throw ('Поставка распознавателя НЕ принята — папку использовать нельзя. Расхождение с манифестом: повторите перенос; ' +
            'нет текстов лицензий: опубликуйте заново publish-speech-worker.ps1 вне контура и перенесите папку.')
    }
    Write-Host "Целостность подтверждена: $count файлов в $WorkerDir. Запись добавлена в журнал переноса."
    if ($missingCrt.Count) {
        Write-Warning ("На этой машине нет $($missingCrt -join ', ') — среды C++, которую импортирует нативная sherpa-onnx " +
            'без синтеза речи. Если это сервер хоста, установите Microsoft Visual C++ Redistributable 2015–2022 (x64), ' +
            'иначе распознаватель завершится кодом 3 (нативная библиотека не загрузилась).')
    }
    return
}

# ======================= Сборка поставки =======================
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Не найден dotnet: для публикации нужен .NET SDK 10.' }
if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "Проект распознавателя не найден: $ProjectPath" }
$project = (Resolve-Path -LiteralPath $ProjectPath).ProviderPath

# Тексты лицензий — ДО сборки: без них поставка не собирается (Apache-2.0 §4(a), MIT; ТСТ-001/004).
foreach ($name in $requiredLicenses.Keys) {
    $problem = Test-LicenseText (Join-Path $LicenseDir $name) $requiredLicenses[$name]
    if ($problem) {
        throw ("$problem. Тексты лицензий sherpa-onnx (Apache-2.0) и onnxruntime (MIT) обязательны в поставке: в контуре " +
            'ссылки на сайты не открываются. Они хранятся в git в deploy/offline/licenses — восстановите файлы или укажите -LicenseDir.')
    }
}

# ---- Источник нативных библиотек — выбирается ДО сборки, чтобы отказ был сразу, а не после публикации ----
# custom — собственная сборка (-NativeDir); no-tts — официальная без синтеза речи; nuget — пакет с eSpeak NG.
$noTtsRoot = Join-Path $NoTtsDir $Runtime
$noTtsCandidates = @()
if ($NativeDir) {
    foreach ($name in $names.Sherpa, $names.Onnx) {
        if (-not (Test-Path -LiteralPath (Join-Path $NativeDir $name))) { throw "В папке -NativeDir нет $name`: $NativeDir" }
    }
    $nativeSource = 'custom'
}
else {
    # Раскладка export-sherpa-no-tts.ps1: <NoTtsDir>/<rid>/<имя архива>/lib/<библиотеки>.
    if (Test-Path -LiteralPath $noTtsRoot) {
        $noTtsCandidates = @(Get-ChildItem -LiteralPath $noTtsRoot -Directory | Where-Object {
                (Test-Path -LiteralPath (Join-Path $_.FullName "lib\$($names.Sherpa)")) -and (Test-Path -LiteralPath (Join-Path $_.FullName "lib\$($names.Onnx)"))
            })
    }
    if ($noTtsCandidates.Count) {
        # Целостность после переноса (ТБ-051): те же файлы, что проверил по пинам export-sherpa-no-tts.ps1.
        $noTtsBad = @(Test-Manifest $noTtsRoot $noTtsManifestName)
        if ($noTtsBad.Count) {
            throw ("Сборка sherpa-onnx без синтеза речи в $noTtsRoot не прошла сверку с манифестом ${noTtsManifestName}:`n  - " + ($noTtsBad -join "`n  - ") +
                "`nПроверьте её: export-sherpa-no-tts.ps1 -VerifyOnly (сверка по пинам, без сети); при отказе — повторите export-sherpa-no-tts.ps1 и перенос.")
        }
        $nativeSource = 'no-tts'
    }
    elseif ($AllowGplEspeakNg) {
        $nativeSource = 'nuget'
        Write-Warning "Сборки sherpa-onnx без синтеза речи для $Runtime нет ($noTtsRoot) — по ключу -AllowGplEspeakNg публикуются библиотеки NuGet с eSpeak NG (GPL-3.0-or-later)."
    }
    else {
        throw ("Нет нативных библиотек sherpa-onnx без синтеза речи для ${Runtime}: $noTtsRoot. Выполните deploy/offline/export-sherpa-no-tts.ps1 " +
            '(на этой же машине с интернетом) и повторите публикацию. ' +
            'Библиотеки из NuGet статически содержат eSpeak NG (GPL-3.0) и не поставляются (ADR-0026, п. 7); ' +
            'крайний случай — решение заказчика и ключ -AllowGplEspeakNg.')
    }
}

# Прежнюю поставку заменяем целиком (устаревший файл попал бы в манифест). Удаляем ТОЛЬКО то, что похоже на
# поставку этого скрипта (есть worker.sha256) или пусто: опечатка в -WorkerDir не должна стереть чужой каталог.
if (Test-Path -LiteralPath $WorkerDir) {
    $existing = @(Get-ChildItem -LiteralPath $WorkerDir -Force)
    if ($existing.Count -and -not (Test-Path -LiteralPath (Join-Path $WorkerDir $manifestName))) {
        throw "Папка $WorkerDir не пуста и не похожа на поставку распознавателя (нет $manifestName) — укажите другую или очистите вручную."
    }
    Remove-Item -LiteralPath $WorkerDir -Recurse -Force
}

$artifacts = Join-Path ([IO.Path]::GetTempPath()) ('isc-speech-worker-' + [guid]::NewGuid().ToString('N'))
$selfContainedText = if ($SelfContained) { 'true' } else { 'false' }
$succeeded = $false
$nativeLib = $NativeDir
$nativeLabel = ''
try {
    Write-Host "Восстановление пакетов ($Runtime)..."
    & dotnet restore $project -r $Runtime "-p:SelfContained=$selfContainedText" --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore завершился с ошибкой (код $LASTEXITCODE)." }

    # Версия обёртки sherpa-onnx из восстановленных пакетов: нативная библиотека должна быть ТОГО ЖЕ релиза —
    # обёртка вызывает C API по именам функций и раскладке структур конфигурации своей версии.
    $assetsFile = Get-ChildItem -LiteralPath (Join-Path $artifacts 'obj') -Recurse -Filter 'project.assets.json' | Select-Object -First 1
    $assets = Get-Content -LiteralPath $assetsFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $packagesRoot = @($assets.packageFolders.PSObject.Properties.Name)[0]
    $sherpaPackage = @($assets.libraries.PSObject.Properties.Name) | Where-Object { $_ -like 'org.k2fsa.sherpa.onnx/*' } | Select-Object -First 1
    if (-not $sherpaPackage) { throw 'В восстановленных пакетах нет org.k2fsa.sherpa.onnx — проверьте ISC.AI.Speech.Worker.csproj.' }
    $sherpaVersion = $sherpaPackage.Split('/')[1]

    if ($nativeSource -eq 'no-tts') {
        $matching = @($noTtsCandidates | Where-Object { $_.Name -like "sherpa-onnx-v$sherpaVersion-*" })
        if ($matching.Count -ne 1) {
            $found = ($noTtsCandidates | ForEach-Object { $_.Name }) -join ', '
            throw ("В $noTtsRoot $(if ($matching.Count) { 'несколько сборок' } else { 'нет сборки' }) sherpa-onnx v$sherpaVersion без синтеза речи (есть: $found), " +
                "а пакет обёртки — $sherpaVersion. Версии должны совпадать: обновите пины export-sherpa-no-tts.ps1 вместе с версией пакета.")
        }
        $nativeLib = Join-Path $matching[0].FullName 'lib'
        $nativeLabel = "официальная сборка без синтеза речи $($matching[0].Name)"
    }
    elseif ($nativeSource -eq 'custom') {
        # Метка без «без синтеза речи»: это утверждение проверяет только проверка содержимого ниже.
        $nativeLabel = "собственная сборка из $NativeDir (-NativeDir)"
    }

    Write-Host "Публикация ISC.AI.Speech.Worker (Release, $Runtime, self-contained=$selfContainedText)..."
    # PublishDocumentationFile=false — XML-документация в поставке не нужна; --disable-build-servers — сборка
    # не оставляет фоновых процессов, держащих временную папку.
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', $Runtime, '--no-restore', '--disable-build-servers',
        '--artifacts-path', $artifacts, '-o', $WorkerDir, '-p:PublishDocumentationFile=false', '-nologo')
    $publishArgs += $(if ($SelfContained) { '--self-contained' } else { '--no-self-contained' })
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой (код $LASTEXITCODE)." }

    # ---- Проверка комплектности: всё, без чего процесс не стартует или падает на первом файле ----
    $problems = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in 'ISC.AI.Speech.Worker.dll', 'ISC.AI.Speech.Worker.deps.json', 'ISC.AI.Speech.Worker.runtimeconfig.json', $names.AppHost) {
        if (-not (Test-Path -LiteralPath (Join-Path $WorkerDir $name))) { $problems.Add("нет $name") }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $WorkerDir 'sherpa-onnx.dll'))) {
        # Главная ловушка этого проекта: в .csproj у sherpa-onnx стоит PrivateAssets="all" (защита тестов от
        # транзитивного onnxruntime), а SDK .NET по умолчанию НЕ публикует такие пакеты
        # (Microsoft.NET.Sdk.Shared.targets: PrivateAssets=All без Publish → Publish=false).
        $problems.Add('нет sherpa-onnx.dll (обёртка sherpa-onnx): пакет с PrivateAssets="all" SDK не публикует — у PackageReference org.k2fsa.sherpa.onnx в ISC.AI.Speech.Worker.csproj нужен Publish="true" (PrivateAssets оставить)')
    }

    $depsPath = Join-Path $WorkerDir 'ISC.AI.Speech.Worker.deps.json'
    $onnxVersion = $null
    if (Test-Path -LiteralPath $depsPath) {
        $deps = Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $libraries = @($deps.libraries.PSObject.Properties.Name)
        if (-not ($libraries | Where-Object { $_ -like 'org.k2fsa.sherpa.onnx/*' })) {
            $problems.Add('в deps.json нет org.k2fsa.sherpa.onnx — среда .NET не загрузит обёртку, даже если файл лежит рядом')
        }
        $foreignOnnx = @($libraries | Where-Object { $_ -like 'Microsoft.ML.OnnxRuntime*' })
        if ($foreignOnnx.Count) {
            $problems.Add("в зависимостях утилиты $($foreignOnnx -join ', ') — второй onnxruntime в том же процессе, ровно тот конфликт, ради которого процесс отдельный (ADR-0026)")
        }
        foreach ($target in $deps.targets.PSObject.Properties) {
            foreach ($library in $target.Value.PSObject.Properties) {
                if ($library.Name -like 'org.k2fsa.sherpa.onnx.runtime.*' -and $library.Value.native) {
                    $entry = $library.Value.native.PSObject.Properties | Where-Object { $_.Name -like "*/$($names.Onnx)" } | Select-Object -First 1
                    if ($entry -and $entry.Value.fileVersion) { $onnxVersion = $entry.Value.fileVersion }
                }
            }
        }
    }

    $sherpaNative = Find-Native $WorkerDir $names.Sherpa
    $onnxNative = Find-Native $WorkerDir $names.Onnx
    if (-not $sherpaNative) { $problems.Add("нет нативной $($names.Sherpa) (ни в корне, ни в runtimes/$Runtime/native)") }
    if (-not $onnxNative) { $problems.Add("нет нативной $($names.Onnx) (ни в корне, ни в runtimes/$Runtime/native)") }

    $gplMarker = $null
    $crtImports = @()
    if ($sherpaNative -and $onnxNative) {
        if ($nativeLib) {
            # Сборка без синтеза речи заменяет библиотеки пакета на ТЕХ ЖЕ местах — ПАРОЙ: sherpa-onnx-c-api
            # связана со своим onnxruntime. Остальное из каталога lib не нужно: *-cxx-api — обёртка для C++,
            # onnxruntime_providers_shared — только для внешних провайдеров (CUDA и т. п.), распознаватель работает
            # на CPU (проверено живым прогоном 25.09.2026); .lib — библиотеки импорта для компоновщика.
            foreach ($pair in @(@($sherpaNative, $names.Sherpa), @($onnxNative, $names.Onnx))) {
                $source = Join-Path $nativeLib $pair[1]
                Copy-Item -LiteralPath $source -Destination $pair[0] -Force
                if ((Get-Sha256 $pair[0]) -ne (Get-Sha256 $source)) { $problems.Add("$($pair[1]) не скопировалась без искажений из $nativeLib") }
            }
            $onnxVersion = $null
            Write-Host "Нативные библиотеки заменены: $nativeLabel (релиз sherpa-onnx v$sherpaVersion)."
        }
        else {
            # Библиотеки пакета (-AllowGplEspeakNg): побайтная сверка — onnxruntime в поставке именно из пакета
            # sherpa-onnx, а не чужой сборки.
            $runtimePackage = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -like "org.k2fsa.sherpa.onnx.runtime.$Runtime/*" } | Select-Object -First 1
            if (-not $runtimePackage) { $problems.Add("в восстановленных пакетах нет org.k2fsa.sherpa.onnx.runtime.$Runtime") }
            else {
                foreach ($pair in @(@($sherpaNative, $names.Sherpa), @($onnxNative, $names.Onnx))) {
                    $reference = Join-Path (Join-Path $packagesRoot $runtimePackage.Value.path) "runtimes\$Runtime\native\$($pair[1])"
                    if (-not (Test-Path -LiteralPath $reference)) { $problems.Add("в пакете $($runtimePackage.Name) нет $($pair[1]) — раскладка пакета изменилась, проверьте вручную") }
                    elseif ((Get-Sha256 $pair[0]) -ne (Get-Sha256 $reference)) { $problems.Add("$($pair[1]) в поставке отличается от файла пакета $($runtimePackage.Name) — чужая сборка затёрла библиотеку") }
                }
            }
            $nativeLabel = "сборка из пакета NuGet sherpa-onnx $sherpaVersion, сверена с пакетом побайтно"
        }

        if (-not $onnxVersion -and $Runtime -like 'win-*') { $onnxVersion = (Get-Item -LiteralPath $onnxNative).VersionInfo.FileVersion }
        if ($Runtime -like 'win-*') { $crtImports = @(Get-CrtImports @($sherpaNative, $onnxNative)) }
    }

    # ---- ВТОРАЯ проверка лицензии — по содержимому ВСЕХ нативных файлов публикации (как флаги сборки у ffmpeg),
    # какой бы ни была их сборка: ошибка в пинах, чужая папка -NativeDir или лишняя библиотека из runtime-пакета
    # другого релиза не должны протащить eSpeak NG (ADR-0026, п. 7; ТСТ-001/004). ----
    $workerRoot = (Resolve-Path -LiteralPath $WorkerDir).ProviderPath.TrimEnd('\', '/')
    $nativeFiles = @(Get-NativeFiles $workerRoot)
    # Ожидаемые нативные файлы: пара sherpa-onnx-c-api + onnxruntime (на тех местах, где их нашла публикация) и,
    # при -SelfContained, библиотеки среды .NET, которые deps.json относит к её runtime-пакету (runtimepack.*).
    $expectedNatives = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($native in @($sherpaNative, $onnxNative) | Where-Object { $_ }) {
        [void]$expectedNatives.Add(((Resolve-Path -LiteralPath $native).ProviderPath.Substring($workerRoot.Length + 1) -replace '\\', '/'))
    }
    if ($SelfContained -and $deps) {
        foreach ($target in $deps.targets.PSObject.Properties) {
            foreach ($library in $target.Value.PSObject.Properties) {
                if ($library.Name -like 'runtimepack.*' -and $library.Value.native) {
                    foreach ($asset in $library.Value.native.PSObject.Properties.Name) { [void]$expectedNatives.Add($asset) }
                }
            }
        }
    }
    $gplFiles = @()
    foreach ($relative in $nativeFiles) {
        if (-not $expectedNatives.Contains($relative)) {
            $problems.Add("ЛИШНЯЯ нативная библиотека $relative — ожидаются только $($names.Sherpa) и $($names.Onnx)$(if ($SelfContained) { ' и библиотеки среды .NET из deps.json' }): состав нативной части изменился (другой релиз пакета?), проверьте вручную")
        }
        $marker = Find-AsciiMarker (Join-Path $workerRoot $relative) $GplMarkers
        if ($marker) {
            if (-not $gplMarker) { $gplMarker = $marker }
            $gplFiles += $relative
        }
    }
    $gplFile = $gplFiles -join ', '
    if ($gplFiles.Count -and -not $AllowGplEspeakNg) {
        $problems.Add("в $gplFile встроен eSpeak NG (найдено «$gplMarker»), лицензия GPL-3.0-or-later — поставка запрещена (ТСТ-001/004). Нужна сборка без синтеза речи: export-sherpa-no-tts.ps1 (или своя, -NativeDir); крайний случай — решение заказчика и ключ -AllowGplEspeakNg")
    }
    elseif ($gplFiles.Count) {
        Write-Warning "В $gplFile встроен eSpeak NG (GPL-3.0-or-later). Поставка допущена ключом -AllowGplEspeakNg — это решение заказчика, оно записано в $noticesName."
    }

    $layout = if ($sherpaNative -and ((Split-Path $sherpaNative -Parent).TrimEnd('\', '/') -eq (Resolve-Path -LiteralPath $WorkerDir).ProviderPath.TrimEnd('\', '/'))) {
        'в корне папки (RID-публикация разворачивает runtimes/<rid>/native)'
    } else { "в runtimes/$Runtime/native" }

    $runtimeRequirement = 'не требуется (.NET runtime входит в поставку, -SelfContained)'
    $runtimeConfigPath = Join-Path $WorkerDir 'ISC.AI.Speech.Worker.runtimeconfig.json'
    if (-not $SelfContained -and (Test-Path -LiteralPath $runtimeConfigPath)) {
        $framework = (Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json).runtimeOptions.framework
        if ($framework) { $runtimeRequirement = "$($framework.name) $($framework.version) или новее в той же основной версии (как у хоста; ASP.NET Core не нужен)" }
    }

    # ---- Тексты лицензий (Apache-2.0 §4(a), MIT; ТСТ-001/004) — рядом с бинарниками; в манифест попадут сами ----
    $copiedOptional = @()
    foreach ($name in @(@($requiredLicenses.Keys) + @($optionalNotices | Where-Object { Test-Path -LiteralPath (Join-Path $LicenseDir $_) }))) {
        $source = Join-Path $LicenseDir $name
        $target = Join-Path $WorkerDir $name
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-Sha256 $target) -ne (Get-Sha256 $source)) { $problems.Add("$name не скопировался без искажений из $LicenseDir") }
        if ($optionalNotices -contains $name) { $copiedOptional += $name }
    }
    $dotnetVersion = $null
    if ($SelfContained) {
        # .NET runtime в поставке: его лицензия и уведомления — из runtime-пакета ТОЙ версии, что вошла в папку
        # (includedFrameworks в runtimeconfig.json). Пакет лежит в папке пакетов NuGet или среди packs SDK.
        $packName = "Microsoft.NETCore.App.Runtime.$Runtime"
        $included = $null
        if (Test-Path -LiteralPath $runtimeConfigPath) {
            $included = @((Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json).runtimeOptions.includedFrameworks) |
                Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | Select-Object -First 1
        }
        $packCandidates = @()
        if ($included) {
            $dotnetVersion = $included.version
            $packCandidates += Join-Path (Join-Path $packagesRoot $packName.ToLowerInvariant()) $dotnetVersion
            $packCandidates += Join-Path (Split-Path (Get-Command dotnet).Source -Parent) "packs\$packName\$dotnetVersion"
        }
        $packDir = $packCandidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'LICENSE.TXT') } | Select-Object -First 1
        if (-not $packDir) {
            $problems.Add("нет текста лицензии .NET для -SelfContained: не найден LICENSE.TXT runtime-пакета $packName $dotnetVersion (искали: $($packCandidates -join '; '))")
        }
        else {
            foreach ($pair in @(@('LICENSE.TXT', 'LICENSE-dotnet.txt'), @('THIRD-PARTY-NOTICES.TXT', 'THIRD-PARTY-NOTICES-dotnet.txt'))) {
                $source = Join-Path $packDir $pair[0]
                if (-not (Test-Path -LiteralPath $source)) { $problems.Add("в runtime-пакете .NET $packDir нет $($pair[0])"); continue }
                Copy-Item -LiteralPath $source -Destination (Join-Path $WorkerDir $pair[1]) -Force
            }
        }
    }

    if ($problems.Count) {
        throw ("Поставка распознавателя НЕ собрана, папка удалена ($WorkerDir):`n  - " + ($problems -join "`n  - "))
    }

    # ---- Уведомление о сторонних компонентах (Apache-2.0 и MIT требуют передавать сведения о лицензии). Ссылки
    # только на ЛОКАЛЬНЫЕ файлы папки: в контуре сайты не открываются. Правообладатель sherpa-onnx — из nuspec. ----
    $sherpaCopyright = $null
    $sherpaLibrary = $assets.libraries.PSObject.Properties[$sherpaPackage]
    if ($sherpaLibrary) {
        $nuspecPath = Join-Path (Join-Path $packagesRoot $sherpaLibrary.Value.path) 'org.k2fsa.sherpa.onnx.nuspec'
        if (Test-Path -LiteralPath $nuspecPath) { $sherpaCopyright = ([xml](Get-Content -LiteralPath $nuspecPath -Raw -Encoding UTF8)).package.metadata.copyright }
    }
    $notices = @(
        'ISC.AI.Speech.Worker — процесс-распознаватель речи ISC.AI (ADR-0026). Сторонние компоненты поставки:',
        '',
        "sherpa-onnx $sherpaVersion (проект k2-fsa$(if ($sherpaCopyright) { ", $sherpaCopyright" })) — лицензия Apache-2.0, текст: LICENSE-sherpa-onnx.txt",
        "  файлы: sherpa-onnx.dll (управляемая обёртка, NuGet), $($names.Sherpa) ($nativeLabel)",
        "onnxruntime $(if ($onnxVersion) { $onnxVersion } else { '(версия — по сборке sherpa-onnx)' }) (Copyright (c) Microsoft Corporation) — лицензия MIT, текст: LICENSE-onnxruntime.txt",
        "  файл: $($names.Onnx), в составе нативной сборки sherpa-onnx"
    )
    if ($copiedOptional -contains 'ThirdPartyNotices-onnxruntime.txt') { $notices += '  уведомления о компонентах внутри onnxruntime: ThirdPartyNotices-onnxruntime.txt' }
    if ($SelfContained) {
        $notices += ".NET runtime $dotnetVersion (Copyright (c) .NET Foundation and Contributors) — лицензия MIT, текст: LICENSE-dotnet.txt"
        $notices += '  уведомления о компонентах среды .NET: THIRD-PARTY-NOTICES-dotnet.txt'
    }
    if ($gplMarker) {
        $notices += "eSpeak NG — GPL-3.0-or-later — СТАТИЧЕСКИ в $gplFile;"
        $notices += '  поставка допущена ключом -AllowGplEspeakNg по решению заказчика; обязательства GPL (текст лицензии,'
        $notices += '  исходные тексты) — на поставщике, в эту папку скрипт их НЕ кладёт.'
    }
    $notices += @('', "Исходные тексты: sherpa-onnx — github.com/k2-fsa/sherpa-onnx (тег v$sherpaVersion), onnxruntime — github.com/microsoft/onnxruntime.")
    $notices | Out-File (Join-Path $WorkerDir $noticesName) -Encoding utf8

    # ---- Манифест целостности (ТБ-051): "<SHA256>  <относительный путь>", как у ffmpeg и моделей ----
    # UTF-8 без BOM и с LF — тот же файл проверяется и на Linux-сервере без PowerShell: sha256sum -c worker.sha256
    # (регистр шестнадцатеричных цифр sha256sum не различает).
    $manifestPath = Join-Path $WorkerDir $manifestName
    $manifestLines = @(Get-RelativeFiles $WorkerDir | Where-Object { $_ -ne $manifestName } | ForEach-Object {
            '{0}  {1}' -f (Get-Sha256 (Join-Path $WorkerDir $_)), $_
        })
    [IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    $selfCheck = @(Test-Manifest $WorkerDir)
    if ($selfCheck.Count) { throw "Манифест не сошёлся сразу после записи: $($selfCheck -join '; ')" }

    $succeeded = $true
}
finally {
    Remove-Item -LiteralPath $artifacts -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $succeeded -and (Test-Path -LiteralPath $WorkerDir)) { Remove-Item -LiteralPath $WorkerDir -Recurse -Force -ErrorAction SilentlyContinue }
    # Пустой deploy/offline/speech-worker после неудачи не оставляем — только для папки по умолчанию.
    $defaultParent = Join-Path $PSScriptRoot 'speech-worker'
    if (-not $succeeded -and -not $PSBoundParameters.ContainsKey('WorkerDir') -and (Test-Path -LiteralPath $defaultParent) -and
        -not @(Get-ChildItem -LiteralPath $defaultParent -Force).Count) {
        Remove-Item -LiteralPath $defaultParent -Force -ErrorAction SilentlyContinue
    }
}

$workerFull = (Resolve-Path -LiteralPath $WorkerDir).ProviderPath

# Папка поставки не должна попасть в git (бинарники). Проверка — только если есть git и папка в рабочей копии
# (код 1 — «не исключена»; вне репозитория git вернёт 128, предупреждать не о чем).
if (Get-Command git -ErrorAction SilentlyContinue) {
    $ErrorActionPreference = 'Continue'   # в 5.1 stderr внешней программы при 'Stop' стал бы исключением
    & git -C $PSScriptRoot check-ignore -q -- $workerFull 2>$null
    $ignoreCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($ignoreCode -eq 1) {
        Write-Warning "Папка $workerFull не исключена в .gitignore — не коммитьте её; добавьте строку deploy/offline/speech-worker/."
    }
}

$files = @(Get-RelativeFiles $workerFull)
$size = ((Get-ChildItem -LiteralPath $workerFull -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB).ToString('0.0')
Write-Host ''
Write-Host "Готово: $workerFull ($size МБ, $($files.Count) файлов), манифест $manifestName, $noticesName."
Write-Host "Тексты лицензий: $((@($requiredLicenses.Keys) + $copiedOptional + $(if ($SelfContained) { @('LICENSE-dotnet.txt', 'THIRD-PARTY-NOTICES-dotnet.txt') } else { @() })) -join ', ')."
Write-Host "Нативные sherpa-onnx и onnxruntime: $layout; источник — $nativeLabel."
# Итог — по ФАКТИЧЕСКИ проверенному списку: сколько нативных файлов просмотрено и какие.
$checkedNatives = if ($nativeFiles.Count -le 4) { $nativeFiles -join ', ' } else { "$(($nativeFiles | Select-Object -First 4) -join ', ') и ещё $($nativeFiles.Count - 4)" }
Write-Host "eSpeak NG (GPL-3.0): проверено нативных файлов — $($nativeFiles.Count) ($checkedNatives); $(if ($gplMarker) { "ЕСТЬ в $gplFile — допущено ключом -AllowGplEspeakNg" } else { 'признаков движка (функций espeak_ng_* и его строк) нет' })."
Write-Host ".NET на сервере: $runtimeRequirement."
if ($crtImports.Count) {
    Write-Host "Среда C++ на сервере: Microsoft Visual C++ Redistributable 2015–2022 (x64) — нативные библиотеки импортируют $($crtImports -join ', ')."
    $missingHere = @(Get-MissingCrt $crtImports (Split-Path $sherpaNative -Parent))
    if ($missingHere.Count) { Write-Warning "На ЭТОЙ машине нет $($missingHere -join ', ') — распознаватель здесь не запустится (на сервере проверит -Verify)." }
}
Write-Host ''
Write-Host 'Конфигурация хоста (секция Speech; путь — АБСОЛЮТНЫЙ, папка распознавателя — ОТДЕЛЬНО от каталога хоста:'
Write-Host 'там лежит onnxruntime.dll распознавания лиц другой сборки, ADR-0026):'
if ($Runtime -like 'win-*') {
    Write-Host "  Speech:Worker:Path = <каталог поставки>\$($names.AppHost)"
    Write-Host "    (для этой машины: $(Join-Path $workerFull $names.AppHost))"
}
else {
    Write-Host '  Speech:Worker:Path = <каталог поставки>/ISC.AI.Speech.Worker.dll'
    Write-Host '    (.dll хост запускает через dotnet — нужен dotnet в PATH службы; бит исполнения после переноса с Windows'
    Write-Host "    не нужен. Вариант с apphost: <каталог>/$($names.AppHost) после chmod +x.)"
}
Write-Host "Перенос: папка целиком; в контуре — publish-speech-worker.ps1 -Verify -WorkerDir <папка> (ТБ-051)."
