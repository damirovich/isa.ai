# Офлайн-поставка нативной библиотеки sherpa-onnx БЕЗ синтеза речи для процесса-распознавателя
# ISC.AI.Speech.Worker профиля «Следствие» (ADR-0026, п. 7; ТИ-004, ТСТ-001/004; перенос — как у фида, ТБ-050/051).
#
# ЛИЦЕНЗИЯ — ЕДИНСТВЕННАЯ ПРИЧИНА ЭТОГО СКРИПТА. Нативные библиотеки из NuGet (org.k2fsa.sherpa.onnx.runtime.*
# 1.13.8, win-x64 и linux-x64) собраны вместе с синтезом речи и СТАТИЧЕСКИ содержат движок eSpeak NG
# (GPL-3.0-or-later): 25 функций espeak_ng_* в sherpa-onnx-c-api.dll (проверено 25.09.2026). Правило проекта —
# только MIT/Apache: поставлять такую библиотеку нельзя, как нельзя GPL-сборку ffmpeg (ADR-0020). Нам нужно
# только распознавание, а проект k2-fsa в каждом релизе выпускает официальные сборки БЕЗ синтеза речи
# (архивы *-no-tts-lib: sherpa-onnx — Apache-2.0, onnxruntime — MIT). Распознавание и детектор речи в них те же,
# eSpeak NG нет. Скрипт их скачивает; publish-speech-worker.ps1 подменяет ими нативные библиотеки пакета.
# Распознаватель из bin/Debug на машине разработчика работает с библиотекой NuGet — это использование, а не
# поставка; в контур уходит только публикация с библиотеками отсюда. Пакеты sherpa-onnx в общий офлайн-фид НЕ
# входят (ISC.AI.Offline.slnf, проверка в export-packages.ps1/verify-packages.ps1), поэтому утилита публикуется
# вне контура, на той же машине, что запускает этот скрипт.
#
# ВЕРСИЯ — СТРОГО ТА ЖЕ, ЧТО У ПАКЕТА org.k2fsa.sherpa.onnx (Directory.Packages.props). Управляемая обёртка
# sherpa-onnx.dll вызывает C API по именам функций и раскладке структур конфигурации СВОЕЙ версии: библиотека
# другого релиза может загрузиться и молча читать поля не оттуда. Обновление пакета = обновление пинов здесь
# (тег, имена архивов, SHA-256 архивов и файлов — одновременно), затем публикация и живая проверка
# распознавания (docs/следствие/Развёртывание_профиля_Следствие.md, §7.2).
#
# ПРОВЕРКА ВЫПОЛНЯЕТСЯ ТРИЖДЫ:
#   1) пин SHA-256 архива — поле digest ассета релиза GitHub (тег закреплён, не «latest»);
#   2) пины SHA-256 КАЖДОГО распакованного файла — они же позволяют проверить уже распакованную папку без
#      архива и без сети (-VerifyOnly: после переноса в контур или повторно);
#   3) содержимое бинарников — признаки eSpeak NG: имена функций espeak_ng_*, каталог данных «espeak-ng-data»,
#      сообщение «mbrola voice file». Поиск С УЧЁТОМ РЕГИСТРА: подстрока «espeak» без учёта регистра есть и в
#      сборке без синтеза — внутри имени OfflineSpeakerDiarization («offlin-eSpeak-er», разделение по голосам);
#      это ложное срабатывание, по нему сборку отвергать нельзя.
#
# ОСОБЕННОСТЬ WINDOWS-СБОРКИ. Берётся вариант MT (среда выполнения C++ вшита статически), а не MD: у MD-сборки
# sherpa-onnx-c-api.dll и onnxruntime.dll импортируют MSVCP140.dll, MSVCP140_1.dll, VCRUNTIME140.dll и
# VCRUNTIME140_1.dll, и на сервере понадобился бы Microsoft Visual C++ Redistributable 2015–2022 (x64) — отдельный
# компонент под лицензией Microsoft и ещё одна причина отказа на «голом» изолированном сервере. У MT-сборки
# таких импортов нет (проверено по таблице импорта 25.09.2026). Linux-сборка требований не добавляет:
# зависимости и RPATH ($ORIGIN) те же, что у пакета NuGet, libonnxruntime.so совпадает с пакетной побайтно.
#
# Результат: deploy/offline/sherpa-no-tts/<rid>/<имя архива>/lib/* (раскладка архива как есть) и манифест
# <rid>/sherpa-no-tts.sha256 ("<SHA256>  <относительный путь>", UTF-8 без BOM, LF — проверяется и sha256sum -c).
# В git НЕ хранится (бинарники, как ffmpeg и модели). Запуск — на машине С интернетом, ДО publish-speech-worker.ps1:
# публикация берёт библиотеки отсюда и там же, вне контура, собирает готовую папку распознавателя. В изолированный
# контур переносится эта готовая папка (с worker.sha256); сама sherpa-no-tts там не нужна. -VerifyOnly — проверка
# папки без сети, если её распаковали вручную или перенесли на другую машину публикации.
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param(
    # Платформы сервера; по умолчанию обе (архивы маленькие: 6,6 и 8,8 МБ).
    [ValidateSet('win-x64', 'linux-x64')]
    [string[]]$Runtime = @('win-x64', 'linux-x64'),

    # Корень поставки: в нём папки <rid>.
    [string]$TargetDir = "$PSScriptRoot\sherpa-no-tts",

    # Папка с архивами, скачанными заранее другим путём (браузером, на другой машине): архивы берутся оттуда
    # вместо загрузки. Пины SHA-256 проверяются точно так же.
    [string]$ArchiveDir = '',

    # Не скачивать, а проверить уже распакованную папку по пинам файлов и на отсутствие eSpeak NG (без сети);
    # если манифеста нет — записать его. Итог пишется в журнал переноса transfer-log.txt (ТБ-051).
    # Рядом со скриптом должен лежать offline-common.ps1 (общие функции поставки).
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- Пины (зафиксированы 25.09.2026; обновление ОСОЗНАННОЕ — см. шапку) ----
$sherpaVersion = '1.13.8'
$releaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v$sherpaVersion"

# Files — ВСЕ файлы каталога lib архива. SHA-256 распакованных файлов вычислены из архивов, прошедших сверку
# с digest релиза. Лишний или недостающий файл — отказ: состав сборки изменился, смотреть вручную.
$builds = @{
    'win-x64'   = @{
        # Вариант MT (среда выполнения C++ вшита статически) — НЕ MD: импортов VCRUNTIME140/MSVCP140 нет ни у
        # sherpa-onnx-c-api.dll, ни у onnxruntime.dll (проверено по таблице импорта 25.09.2026), поэтому на
        # сервере НЕ нужен Microsoft Visual C++ Redistributable — лишний компонент на изолированном сервере и
        # лишняя причина отказа распознавателя. SHA-256 архива — digest релиза GitHub.
        Name   = "sherpa-onnx-v$sherpaVersion-win-x64-shared-MT-Release-no-tts-lib"
        Sha256 = 'A1253E665C4F236119C443C8932A8ACFCA32A546C78C05962D483C9A0EAE21B7'
        Files  = [ordered]@{
            'lib/onnxruntime.dll'                  = '7F66F939A881BAF4F46A2216496798EDF4A1429878B646D12674AA62F27D8A25'
            'lib/onnxruntime.lib'                  = 'B9FC3CD678257D88A111B0773EDE4BFCEAF0FE95DAAB4379F2B2B37348A68781'
            'lib/onnxruntime_providers_shared.dll' = '551D0E1FE4C227D8542314BA718D52F4379E0C7BFE729A37C59833A884E27B4D'
            'lib/sherpa-onnx-c-api.dll'            = '86C7807D12982AA31C5CAE57C686F30DF9E5D5DB4309B314D1EFD5C873A8333C'
            'lib/sherpa-onnx-c-api.lib'            = 'AA04E30CD0D9386FA2446B5B8CC8DFF6DCF1C15D1263BE1BCFC86CF92F6663D2'
            'lib/sherpa-onnx-cxx-api.dll'          = 'B09238F19F35B568A524B907536917F8AAFB7BA515D862C44FB352ED12FACAA5'
            'lib/sherpa-onnx-cxx-api.lib'          = 'FBAD731250ED1C688C76DA5EA92C67C2B26CAC07D87DD0478C4DCB96F864FEEE'
        }
    }
    'linux-x64' = @{
        Name   = "sherpa-onnx-v$sherpaVersion-linux-x64-shared-no-tts-lib"
        Sha256 = 'BF2D998C8B07012CD5098F3B92673BC1333FD9B927767D7CB664BE8190D8BC0B'
        Files  = [ordered]@{
            'lib/libonnxruntime.so'         = '4B3607AEBD1784B26B6F9B20E4BD974C7AB8287043E4D095CB7D2CB40B5E566E'
            'lib/libsherpa-onnx-c-api.so'   = 'C067B18CD746E49EB5016EE428196D21E1DF14C14A3C54F128C1AC95F6AAED50'
            'lib/libsherpa-onnx-cxx-api.so' = '81B3C4FFDD19E3DA9D4F7DF7C41FDE233506CB701A074B005C31234BF910B1E1'
        }
    }
}

$manifestName = 'sherpa-no-tts.sha256'

# Общие функции поставки: Get-RelativeFiles, Get-Sha256, Find-AsciiMarker, Get-CrtImports и признаки eSpeak NG
# $GplMarkers — сравнение ОРДИНАЛЬНОЕ, с учётом регистра (см. шапку: ложное «eSpeak» в
# OfflineSpeakerDiarization). Тот же список — в publish-speech-worker.ps1 и в проверке офлайн-фида.
. (Join-Path $PSScriptRoot 'offline-common.ps1')

# Проверка папки <rid> по пинам: список расхождений (пустой — сборка подлинная и без eSpeak NG).
function Test-Build([string]$Root, $Build) {
    $bad = @()
    if (-not (Test-Path -LiteralPath $Root)) { return @("НЕТ ПАПКИ: $Root") }
    $expected = @($Build.Files.Keys | ForEach-Object { "$($Build.Name)/$_" })
    $actual = @(Get-RelativeFiles $Root | Where-Object { $_ -ne $manifestName })
    foreach ($relative in $actual) {
        if ($expected -notcontains $relative) { $bad += "ЛИШНИЙ ФАЙЛ (нет в пинах): $relative" }
    }
    foreach ($key in $Build.Files.Keys) {
        $relative = "$($Build.Name)/$key"
        $file = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $file)) { $bad += "ОТСУТСТВУЕТ: $relative"; continue }
        if ((Get-Sha256 $file) -ne $Build.Files[$key]) { $bad += "ХЕШ НЕ СОВПАЛ С ПИНОМ: $relative" }
        # Проверка содержимого — вторая линия (на случай ошибки в самих пинах) и понятный диагноз подмены:
        # выполняется и для файла, не совпавшего с пином.
        $marker = Find-AsciiMarker $file $GplMarkers
        if ($marker) { $bad += "В $relative встроен eSpeak NG (найдено «$marker», GPL-3.0) — это НЕ сборка без синтеза речи" }
    }
    return $bad
}

# Манифест (ТБ-051): тот же формат, что worker.sha256 распознавателя — UTF-8 без BOM, LF.
function Write-Manifest([string]$Root) {
    $lines = @(Get-RelativeFiles $Root | Where-Object { $_ -ne $manifestName } | ForEach-Object {
            '{0}  {1}' -f (Get-Sha256 (Join-Path $Root $_)), $_
        })
    [IO.File]::WriteAllText((Join-Path $Root $manifestName), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
}

# Совпадает ли манифест с файлами (для -VerifyOnly: расхождение при верных пинах — манифест устарел или правлен).
function Test-ManifestMatches([string]$Root) {
    $manifest = Join-Path $Root $manifestName
    if (-not (Test-Path -LiteralPath $manifest)) { return $false }
    $listed = @{}
    foreach ($line in (Get-Content -LiteralPath $manifest -Encoding UTF8 | Where-Object { $_ -match '\S' })) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { return $false }
        $listed[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant()
    }
    $files = @(Get-RelativeFiles $Root | Where-Object { $_ -ne $manifestName })
    if ($files.Count -ne $listed.Count) { return $false }
    foreach ($relative in $files) {
        if (-not $listed.ContainsKey($relative) -or $listed[$relative] -ne (Get-Sha256 (Join-Path $Root $relative))) { return $false }
    }
    return $true
}

# Windows PowerShell 5.1 (.NET Framework) не видит файлы с полным путём длиннее 259 символов (MAX_PATH):
# Test-Path отвечает «нет», Remove-Item падает — проверка приняла бы целую сборку за неполную. Имя каталога
# архива длинное (≈57 символов), поэтому глубокая папка -TargetDir упирается в предел: лучше явный отказ.
function Assert-PathLength([string]$Root, $Build) {
    if ($PSVersionTable.PSEdition -eq 'Core') { return }   # PowerShell 7 длинные пути поддерживает
    $longest = ($Build.Files.Keys | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $Root "$($Build.Name)/$_")) } |
            Sort-Object Length -Descending | Select-Object -First 1)
    if ($longest.Length -ge 260) {
        throw "Путь $longest длиннее 259 символов ($($longest.Length)) — Windows PowerShell 5.1 его не обработает. Укажите -TargetDir короче (или перенесите deploy/offline ближе к корню диска), либо запустите из PowerShell 7."
    }
}

# tar с поддержкой bzip2. На Windows — СИСТЕМНЫЙ tar.exe (bsdtar, есть с Windows 10 1803 / Server 2019): tar из
# Git for Windows (GNU) принимает «C:\...» за адрес удалённой машины и падает. На Linux/macOS — tar из PATH.
function Get-Tar {
    if ($env:OS -eq 'Windows_NT') {
        $systemTar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (Test-Path -LiteralPath $systemTar) { return $systemTar }
        throw "Нет системного $systemTar (Windows 10 1803+ / Server 2019+). Распакуйте архивы вручную в <TargetDir>\<rid> и проверьте ключом -VerifyOnly."
    }
    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if (-not $tar) { throw 'Не найден tar — установите его или распакуйте архивы вручную и проверьте ключом -VerifyOnly.' }
    return $tar.Source
}

$cleanupPaths = New-Object 'System.Collections.Generic.List[string]'
$summary = @()
try {
    foreach ($rid in $Runtime) {
        $build = $builds[$rid]
        $root = Join-Path $TargetDir $rid

        if ($VerifyOnly) {
            # ======================= Проверка уже распакованной папки (без сети) =======================
            Assert-PathLength $root $build
            $bad = @(Test-Build $root $build)
            $verdict = if ($bad.Count) { 'ОТКЛОНЕНО: ' + ($bad -join '; ') } else { 'подлинность подтверждена по пинам, eSpeak NG нет' }
            $entry = '{0} | sherpa-onnx {1} без синтеза речи, {2} | {3} | {4} | {5}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $sherpaVersion, $rid, $root, $env:USERNAME, $verdict
            Add-Content (Join-Path $PSScriptRoot 'transfer-log.txt') $entry -Encoding utf8
            if ($bad.Count) {
                $bad | ForEach-Object { Write-Host $_ }
                throw "Сборка sherpa-onnx без синтеза речи для $rid НЕ подтверждена — использовать нельзя; повторите export-sherpa-no-tts.ps1 и перенос."
            }
            if (-not (Test-Path -LiteralPath (Join-Path $root $manifestName))) {
                Write-Manifest $root
                Write-Host "${rid}: манифеста не было — записан по файлам, подтверждённым пинами."
            }
            elseif (-not (Test-ManifestMatches $root)) {
                Write-Manifest $root
                Write-Warning "${rid}: манифест $manifestName расходился с файлами (при верных пинах) — перезаписан. Выясните, кто его правил."
            }
        }
        else {
            # ======================= Загрузка, проверка, распаковка =======================
            $staging = Join-Path $TargetDir ".tmp-$rid"
            Assert-PathLength $staging $build
            New-Item -ItemType Directory -Force $TargetDir | Out-Null
            $archiveName = "$($build.Name).tar.bz2"
            if ($ArchiveDir) {
                $archive = Join-Path $ArchiveDir $archiveName
                if (-not (Test-Path -LiteralPath $archive)) { throw "В папке -ArchiveDir нет $archiveName`: $ArchiveDir" }
            }
            else {
                # Windows PowerShell 5.1 по умолчанию может не предлагать TLS 1.2, без которого GitHub не отвечает.
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                $archive = Join-Path $TargetDir $archiveName
                $cleanupPaths.Add($archive)
                Write-Host "Загрузка $archiveName (тег v$sherpaVersion)..."
                Invoke-WebRequest -Uri "$releaseUrl/$archiveName" -OutFile $archive -MaximumRedirection 5 -UseBasicParsing
            }

            $actual = Get-Sha256 $archive
            if ($actual -ne $build.Sha256) {
                if (-not $ArchiveDir) { Remove-Item -LiteralPath $archive -Force }
                throw "SHA256 архива $archiveName не совпал с пином (получен $actual, ожидался $($build.Sha256)) — архив отброшен, перенос запрещён (ТИ-004)."
            }

            # Распаковка во ВРЕМЕННУЮ папку рядом: неудача не портит уже лежащую рабочую копию. Из архива берётся
            # только каталог lib (библиотеки), раскладка <имя архива>/lib сохраняется как есть. Имя временной папки
            # короткое и постоянное (MAX_PATH, см. Assert-PathLength); остаток прошлого сбоя удаляется.
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
            $cleanupPaths.Add($staging)
            New-Item -ItemType Directory -Force $staging | Out-Null
            Write-Host "Распаковка $archiveName..."
            & (Get-Tar) -xjf $archive -C $staging "$($build.Name)/lib"
            if ($LASTEXITCODE -ne 0) { throw "tar не распаковал $archiveName (код $LASTEXITCODE) — структура архива изменилась, проверьте вручную." }

            $bad = @(Test-Build $staging $build)
            if ($bad.Count) {
                $bad | ForEach-Object { Write-Host $_ }
                throw "Архив $archiveName прошёл пин, но распакованные файлы не совпали с пинами — поставка остановлена, проверьте вручную."
            }
            Write-Manifest $staging

            # Прежнюю копию заменяем целиком. Удаляем ТОЛЬКО то, что похоже на нашу поставку (есть манифест или
            # все файлы из пинов): опечатка в -TargetDir не должна стереть чужой каталог.
            if (Test-Path -LiteralPath $root) {
                $expected = @($build.Files.Keys | ForEach-Object { "$($build.Name)/$_" })
                $ours = (Test-Path -LiteralPath (Join-Path $root $manifestName)) -or
                    -not @(Get-RelativeFiles $root | Where-Object { $expected -notcontains $_ }).Count
                if (-not $ours) { throw "Папка $root не похожа на поставку этого скрипта (нет $manifestName, есть чужие файлы) — укажите другую или очистите вручную." }
                Remove-Item -LiteralPath $root -Recurse -Force
            }
            Move-Item -LiteralPath $staging -Destination $root
            if (-not $ArchiveDir) { Remove-Item -LiteralPath $archive -Force }
        }

        $files = @(Get-RelativeFiles $root | Where-Object { $_ -ne $manifestName })
        $size = ((Get-ChildItem -LiteralPath $root -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB).ToString('0.0')
        $line = "${rid}: $root ($size МБ, $($files.Count) файлов, манифест $manifestName) — пины совпали, функций espeak_ng_* нет."
        if ($rid -like 'win-*') {
            $binaries = @($files | Where-Object { $_ -like '*.dll' } | ForEach-Object { Join-Path $root $_ })
            $crt = @(Get-CrtImports $binaries)
            if ($crt.Count) { $line += "`n  Windows-сервер: нужен Microsoft Visual C++ Redistributable 2015–2022 (x64) — библиотеки импортируют $($crt -join ', ')." }
        }
        $summary += $line
    }
}
finally {
    foreach ($path in $cleanupPaths) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
Write-Host "Готово: sherpa-onnx $sherpaVersion без синтеза речи (Apache-2.0; onnxruntime — MIT)."
$summary | ForEach-Object { Write-Host $_ }
Write-Host 'Далее: publish-speech-worker.ps1 — берёт нативные библиотеки отсюда (сверяет манифест) и проверяет публикацию на eSpeak NG.'
if ($VerifyOnly) { Write-Host 'Запись добавлена в журнал переноса transfer-log.txt.' }
