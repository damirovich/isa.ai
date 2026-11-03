# Офлайн-поставка ffmpeg для раскадровки видео профиля «Следствие» (ADR-0020, ТИ-004, ТСТ-001/004).
#
#   .\deploy\offline\export-ffmpeg.ps1                       — сервер Windows (win-x64)
#   .\deploy\offline\export-ffmpeg.ps1 -Platform linux-x64   — сервер Linux (пакет deploy/linux, «Следствие AI»)
#
# ЛИЦЕНЗИЯ — ЕДИНСТВЕННАЯ ПРИЧИНА, ПОЧЕМУ ЗДЕСЬ ПИН, А НЕ «СКАЧАЙ ПОСЛЕДНЮЮ». Правило проекта —
# только MIT/Apache и эквивалентно-пермиссивные; ffmpeg распространяется в двух вариантах сборки:
#   * LGPL — обязательства: текст лицензии в дистрибутиве и предложение исходных текстов; линковки
#     у нас нет вообще (вызов внешним процессом), это самый безопасный сценарий использования;
#   * GPL (сборки с --enable-gpl: x264, x265, xvid) — ЗАПРЕЩЕНА: сделала бы производным весь продукт.
# Сборки gyan.dev — GPLv3, брать нельзя. Берём BtbN FFmpeg-Builds, вариант «lgpl», из ТЕГИРОВАННОГО
# релиза (не «latest»: плавающая ссылка меняет содержимое под тем же адресом, пин теряет смысл).
#
# Проверка соответствия выполняется ДВАЖДЫ: пин SHA-256 архива здесь и проверка флагов конфигурации
# самой сборки ниже (не должно быть --enable-gpl и --enable-nonfree). Сборку для Windows проверяем запуском
# `ffmpeg -version`; сборку для Linux на Windows не запустить — ищем те же флаги в строке конфигурации внутри файлов,
# а запуском её проверяет стенд и install.sh на сервере.
#
# Релизы autobuild BtbN со временем удаляются (тег win-x64 2026-09-17 уже снят): выгруженную папку ХРАНИТЬ — пакет
# поставки собирается из неё. Новая выгрузка — осознанно: сменить тег, имя и SHA-256 одновременно, заново прогнать
# tests --filter "Category=Video" (раскадровка, таймкоды) и проверку на стенде, перенести папку в контур.
#
# Запуск — на машине С интернетом; в изолированный контур переносится папка deploy/offline целиком.
param(
    [string]$FfmpegDir = "$PSScriptRoot\ffmpeg",

    # Платформа сервера: win-x64 (по умолчанию) или linux-x64.
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Platform = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$builds = @{
    # Пин зафиксирован 18.09.2026. Стабильная ветка 9.0 (не master), вариант shared: бинарник 0,5 МБ + общие
    # библиотеки ≈ 148 МБ; статический вариант дублирует библиотеки в каждый exe и весит вдвое больше.
    'win-x64'   = @{
        Tag    = 'autobuild-2026-09-17-13-19'
        Name   = 'ffmpeg-n9.0.1-69-g3e11912860-win64-lgpl-shared-9.0.zip'
        Sha    = '3FA84213E8C38FAE99CDAB620CB47F8CEBFEF6C550D5E950700948133EDCE5C6'
        Commit = 'g3e11912860'
    }
    # Пин зафиксирован 03.11.2026 и совпал с checksums.sha256 самого релиза. Та же ветка 9.0, вариант shared
    # (60 МБ архив): bin/ffmpeg, bin/ffprobe и библиотеки lib/*.so, которые бинарник ищет рядом с собой ($ORIGIN/../lib).
    'linux-x64' = @{
        Tag    = 'autobuild-2026-11-02-12-51'
        Name   = 'ffmpeg-n9.0.2-45-g1e65c6cd5d-linux64-lgpl-shared-9.0.tar.xz'
        Sha    = '42E5CB14BEE42B9B88312208ACB2AA4DA6D8B2EC9F7F2FAE07D022FFEA6A8475'
        Commit = 'g1e65c6cd5d'
    }
}
$build = $builds[$Platform]
$build.Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$($build.Tag)/$($build.Name)"

$target = Join-Path $FfmpegDir $Platform
New-Item -ItemType Directory -Force $FfmpegDir | Out-Null
$archive = Join-Path $FfmpegDir $build.Name

Write-Host "Загрузка $($build.Name) (тег $($build.Tag))..."
Invoke-WebRequest -Uri $build.Url -OutFile $archive -MaximumRedirection 5

$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $build.Sha) {
    Remove-Item $archive -Force
    throw "SHA256 архива не совпал с пином (получен $actual) — файл отброшен, перенос запрещён (ТИ-004)."
}

# Прежняя выгрузка снимается только теперь — когда новая скачана и совпала с пином: снятый релиз или обрыв связи не
# оставляют машину без рабочей папки.
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null

# Строка конфигурации сборки (то, что печатает `ffmpeg -version`) хранится в самих файлах. Ищем в ней запрещённые флаги
# побайтно — так проверяется и сборка, которую на этой машине не запустить (Linux).
function Test-ForbiddenFlags([string[]]$Files) {
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $seenConfiguration = $false
    foreach ($file in $Files) {
        $text = $latin1.GetString([System.IO.File]::ReadAllBytes($file))
        if ($text.Contains('--prefix=')) { $seenConfiguration = $true }
        foreach ($forbidden in '--enable-gpl', '--enable-nonfree') {
            if ($text.Contains($forbidden)) {
                Remove-Item $target -Recurse -Force
                throw "Сборка содержит $forbidden ($(Split-Path $file -Leaf)) — это НЕ LGPL-вариант, поставка запрещена (ТСТ-001/004). Папка удалена."
            }
        }
    }
    if (-not $seenConfiguration) {
        throw 'Строка конфигурации сборки в файлах не найдена — флаги лицензии не проверены, перенос запрещён до ручной проверки.'
    }
}

if ($Platform -eq 'win-x64') {
    Write-Host 'Распаковка: только исполняемые файлы и текст лицензии (ffplay не нужен — проигрыватель)...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $isBinary = $entry.FullName -match '/bin/[^/]+\.(dll|exe)$' -and $entry.FullName -notmatch 'ffplay'
            $isLicense = $entry.FullName -match 'LICENSE\.txt$'
            if ($isBinary -or $isLicense) {
                $name = Split-Path $entry.FullName -Leaf
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target $name), $true)
            }
        }
    }
    finally {
        $zip.Dispose()
    }
    Remove-Item $archive -Force

    $exe = Join-Path $target 'ffmpeg.exe'
    if (-not (Test-Path $exe)) { throw "В архиве нет bin/ffmpeg.exe — структура сборки изменилась, проверьте вручную." }
    if (-not (Test-Path (Join-Path $target 'LICENSE.txt'))) { throw "В архиве нет LICENSE.txt: текст лицензии обязателен в дистрибутиве (LGPL)." }

    # ВТОРАЯ проверка лицензии — по самому бинарнику: вариант сборки виден в строке configuration.
    $configuration = & $exe -hide_banner -version 2>&1 | Select-String -Pattern 'configuration:' | Select-Object -First 1
    if (-not $configuration) { throw "Не удалось прочитать конфигурацию сборки: $exe -version не дал строку configuration." }
    foreach ($forbidden in '--enable-gpl', '--enable-nonfree') {
        if ($configuration -match [regex]::Escape($forbidden)) {
            Remove-Item $target -Recurse -Force
            throw "Сборка содержит $forbidden — это НЕ LGPL-вариант, поставка запрещена (ТСТ-001/004). Папка удалена."
        }
    }

    # Манифест целостности (ТБ-051): по нему проверяется перенос в контур, как у моделей и пакетов.
    $manifest = Join-Path $target 'ffmpeg.sha256'
    Get-ChildItem $target -File | Where-Object { $_.Name -ne 'ffmpeg.sha256' } | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
    } | Out-File $manifest -Encoding utf8
}
else {
    # tar Windows (bsdtar 3.5) формат xz не читает — разжимаем xz из Git for Windows (или из PATH), а сам .tar
    # разбираем tar Windows.
    $xz = (Get-Command xz.exe -ErrorAction SilentlyContinue).Source
    if (-not $xz -and ($git = Get-Command git.exe -ErrorAction SilentlyContinue)) {
        $gitRoot = Split-Path (Split-Path $git.Source)
        $xz = @("$gitRoot\mingw64\bin\xz.exe", "$gitRoot\usr\bin\xz.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $xz) { throw 'Нет xz.exe: Linux-сборка ffmpeg упакована в .tar.xz. Поставьте Git for Windows (в нём есть xz) и повторите.' }

    Write-Host 'Распаковка: исполняемые ffmpeg и ffprobe, библиотеки и текст лицензии (ffplay не нужен)...'
    & $xz -dkf $archive
    if ($LASTEXITCODE -ne 0) { throw "xz не разжал $($build.Name) (код $LASTEXITCODE)." }
    $tarFile = $archive.Substring(0, $archive.Length - 3)
    $bsdtar = "$env:SystemRoot\System32\tar.exe"
    $extract = Join-Path $FfmpegDir "extract-$Platform"
    try {
        # Только обычные файлы. Ссылки libX.so → libX.so.N → libX.so.N.M.P на Windows без прав администратора не создать,
        # поэтому ниже каждая библиотека кладётся под именем, которое ищет загрузчик (SONAME, libX.so.N), — копией.
        $members = @(& $bsdtar -tf $tarFile | Where-Object {
            $_ -match '^[^/]+/bin/(ffmpeg|ffprobe)$' -or $_ -match '^[^/]+/lib/lib[a-z0-9_]+\.so\.\d+\.\d+\.\d+$' -or $_ -match '^[^/]+/LICENSE\.txt$'
        })
        if ($LASTEXITCODE -ne 0 -or -not $members) { throw "Не удалось прочитать список файлов $tarFile." }
        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
        New-Item -ItemType Directory -Force $extract | Out-Null
        & $bsdtar -xf $tarFile -C $extract @members
        if ($LASTEXITCODE -ne 0) { throw "tar не распаковал $tarFile (код $LASTEXITCODE)." }

        $top = Join-Path $extract ($members[0] -split '/')[0]
        New-Item -ItemType Directory -Force (Join-Path $target 'bin'), (Join-Path $target 'lib') | Out-Null
        foreach ($exeName in 'ffmpeg', 'ffprobe') {
            $source = Join-Path $top "bin\$exeName"
            if (-not (Test-Path -LiteralPath $source)) { throw "В архиве нет bin/$exeName — структура сборки изменилась, проверьте вручную." }
            Copy-Item -LiteralPath $source (Join-Path $target "bin\$exeName")
        }
        foreach ($library in Get-ChildItem (Join-Path $top 'lib') -File) {
            $soname = $library.Name -replace '^(lib.+\.so\.\d+)\.\d+\.\d+$', '$1'
            Copy-Item -LiteralPath $library.FullName (Join-Path $target "lib\$soname")
        }
        $license = Join-Path $top 'LICENSE.txt'
        if (-not (Test-Path -LiteralPath $license)) { throw "В архиве нет LICENSE.txt: текст лицензии обязателен в дистрибутиве (LGPL)." }
        Copy-Item -LiteralPath $license (Join-Path $target 'LICENSE.txt')
    }
    finally {
        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
        if (Test-Path -LiteralPath $tarFile) { Remove-Item -LiteralPath $tarFile -Force }
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    }

    # ВТОРАЯ проверка лицензии — по строке конфигурации внутри файлов (запустить Linux-сборку здесь нельзя).
    Test-ForbiddenFlags (Get-ChildItem $target -Recurse -File | ForEach-Object FullName)

    # Библиотеки бинарник ищет рядом с собой ($ORIGIN/../lib) — без этой записи пришлось бы задавать LD_LIBRARY_PATH.
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    if (-not $latin1.GetString([System.IO.File]::ReadAllBytes((Join-Path $target 'bin\ffmpeg'))).Contains('$ORIGIN')) {
        Write-Warning 'В bin/ffmpeg нет пути поиска библиотек $ORIGIN — на сервере понадобится LD_LIBRARY_PATH; проверьте на стенде.'
    }

    # Манифест для Linux: пути через «/», хеши строчными, UTF-8 без BOM и LF — его можно проверить и sha256sum -c.
    $lines = Get-ChildItem $target -Recurse -File | Where-Object { $_.Name -ne 'ffmpeg.sha256' } | ForEach-Object {
        $relative = $_.FullName.Substring($target.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    } | Sort-Object { $_.Substring(66) } -CaseSensitive
    [System.IO.File]::WriteAllText((Join-Path $target 'ffmpeg.sha256'), (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
}

$size = ((Get-ChildItem $target -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB).ToString('0.0')
Write-Host "Готово: $target ($size МБ), манифест ffmpeg.sha256, лицензия LICENSE.txt (LGPL)."
if ($Platform -eq 'win-x64') {
    Write-Host "Конфигурация хоста: Vision:Ffmpeg:Folder = <путь к $Platform>. Без него фото обрабатываются, видео — нет."
}
else {
    Write-Host 'Пакет «Следствия» для Linux берёт эту папку сам (deploy/linux/build-investigation-package.ps1); на сервере — /opt/isc-ai/ffmpeg/bin.'
}
Write-Host 'Обязательство LGPL: вместе с дистрибутивом передать текст лицензии и предложение исходных текстов —'
Write-Host "исходники ffmpeg этой сборки: тег $($build.Tag), коммит $($build.Commit) (github.com/FFmpeg/FFmpeg)."
