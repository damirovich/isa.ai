# Экспорт языковых файлов OCR Tesseract (ПОДГ-02, порядок переноса — как у фида: ТБ-050/051).
# Запускается на машине С ИНТЕРНЕТОМ. Скачивает rus/kir traineddata из ОФИЦИАЛЬНОГО репозитория
# tesseract-ocr/tessdata (Apache-2.0), сверяет SHA256 с пином в этом скрипте (контроль подмены
# источника) и пишет манифест tessdata.sha256 — по нему целостность подтверждается после переноса
# за периметр (Get-FileHash по каждой строке). Файлы в git НЕ хранятся (.gitignore).
param([string]$TessdataDir = "$PSScriptRoot\tessdata")

$ErrorActionPreference = 'Stop'

# Пины SHA256 официальных файлов (репозиторий tesseract-ocr/tessdata, ветка main, зафиксировано
# 26.08.2026). При обновлении файлов обновить пины ОСОЗНАННО, сверив источник.
$files = @(
    @{ Name = 'rus.traineddata'
       Url  = 'https://github.com/tesseract-ocr/tessdata/raw/main/rus.traineddata'
       Sha  = '681BE2C2BEAD1BC7BD235DF88C44E8E60AE73AE866840C0AD4E3B4C247BD37C2' },
    @{ Name = 'kir.traineddata'
       Url  = 'https://github.com/tesseract-ocr/tessdata/raw/main/kir.traineddata'
       Sha  = '43A333AD96195DF9BFED08DDAFB308F30F1463D498E20362947D1A872FE67D26' }
)

New-Item -ItemType Directory -Force $TessdataDir | Out-Null

foreach ($file in $files) {
    $target = Join-Path $TessdataDir $file.Name
    Invoke-WebRequest -Uri $file.Url -OutFile $target
    $actual = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($actual -ne $file.Sha) {
        Remove-Item $target -Force
        throw "SHA256 файла $($file.Name) не совпал с пином (получен $actual) — файл отброшен, перенос запрещён."
    }
}

# Манифест целостности (ТБ-051): строка = "<SHA256>  <имя файла>", как у packages.sha256.
$manifest = Join-Path $TessdataDir 'tessdata.sha256'
Get-ChildItem $TessdataDir -Filter *.traineddata | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Out-File $manifest -Encoding utf8

Write-Host "Готово: $($files.Count) языковых файла в $TessdataDir, манифест tessdata.sha256. Переносятся с папкой offline целиком."
