# Офлайн-поставка ffmpeg для раскадровки видео профиля «Следствие» (ADR-0020, ТИ-004, ТСТ-001/004).
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
# самого бинарника ниже (в выводе `ffmpeg -version` не должно быть --enable-gpl и --enable-nonfree).
#
# Запуск — на машине С интернетом; в изолированный контур переносится папка deploy/offline целиком.
param([string]$FfmpegDir = "$PSScriptRoot\ffmpeg")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Пин зафиксирован 18.09.2026. Обновление ОСОЗНАННОЕ: сменить тег, URL и SHA-256 одновременно,
# заново прогнать tests --filter "Category=Video" (раскадровка, таймкоды) и перенести папку в контур.
$build = @{
    Tag      = 'autobuild-2026-09-17-13-19'
    Name     = 'ffmpeg-n9.0.1-69-g3e11912860-win64-lgpl-shared-9.0.zip'
    Sha      = '3FA84213E8C38FAE99CDAB620CB47F8CEBFEF6C550D5E950700948133EDCE5C6'
    Platform = 'win-x64'
    # Стабильная ветка 9.0 (не master), вариант shared: бинарник 0,5 МБ + общие библиотеки ≈ 148 МБ;
    # статический вариант дублирует библиотеки в каждый exe и весит вдвое больше.
}
$build.Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$($build.Tag)/$($build.Name)"

$target = Join-Path $FfmpegDir $build.Platform
New-Item -ItemType Directory -Force $target | Out-Null
$archive = Join-Path $FfmpegDir $build.Name

Write-Host "Загрузка $($build.Name) (тег $($build.Tag))..."
Invoke-WebRequest -Uri $build.Url -OutFile $archive -MaximumRedirection 5

$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $build.Sha) {
    Remove-Item $archive -Force
    throw "SHA256 архива не совпал с пином (получен $actual) — файл отброшен, перенос запрещён (ТИ-004)."
}

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

$size = ((Get-ChildItem $target -File | Measure-Object -Property Length -Sum).Sum / 1MB).ToString('0.0')
Write-Host "Готово: $target ($size МБ), манифест ffmpeg.sha256, лицензия LICENSE.txt (LGPL)."
Write-Host "Конфигурация хоста: Vision:Ffmpeg:Folder = <путь к $($build.Platform)>. Без него фото обрабатываются, видео — нет."
Write-Host 'Обязательство LGPL: вместе с дистрибутивом передать текст лицензии и предложение исходных текстов —'
Write-Host "исходники ffmpeg этой сборки: тег $($build.Tag), коммит g3e11912860 (github.com/FFmpeg/FFmpeg)."
