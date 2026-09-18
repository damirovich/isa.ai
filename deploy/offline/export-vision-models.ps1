# Экспорт моделей распознавания лиц (ADR-0020, ТИ-004; порядок переноса — как у фида: ТБ-050/051).
# Запускается на машине С ИНТЕРНЕТОМ. Скачивает ONNX-модели из ОФИЦИАЛЬНОГО репозитория
# opencv/opencv_zoo (YuNet — MIT, SFace — Apache-2.0; лицензии и кода, и весов), сверяет SHA256
# с пином в этом скрипте (контроль подмены источника) и пишет манифест models.sha256 — по нему
# целостность подтверждается после переноса за периметр, а хост при старте требует те же пины
# в конфигурации Vision:*:Sha256 (несовпадение — явная ошибка, ТИ-004). Файлы в git НЕ хранятся.
param([string]$ModelsDir = "$PSScriptRoot\models")

$ErrorActionPreference = 'Stop'

# Пины SHA256 официальных файлов (opencv/opencv_zoo, ветка main, зафиксировано 17.09.2026).
# При обновлении моделей обновить пины ОСОЗНАННО: смена модели = переиндексация всех шаблонов
# (баллы не переносимы, ТО-мат-09).
$files = @(
    @{ Name = 'face_detection_yunet_2023mar.onnx'
       Url  = 'https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx'
       Sha  = '8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4' },
    @{ Name = 'face_recognition_sface_2021dec.onnx'
       Url  = 'https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx'
       Sha  = '0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79' }
)

New-Item -ItemType Directory -Force $ModelsDir | Out-Null

foreach ($file in $files) {
    $target = Join-Path $ModelsDir $file.Name
    # Модели лежат в Git LFS — github.com/.../raw/ отдаёт содержимое через редирект.
    Invoke-WebRequest -Uri $file.Url -OutFile $target -MaximumRedirection 5
    $actual = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($actual -ne $file.Sha) {
        Remove-Item $target -Force
        throw "SHA256 файла $($file.Name) не совпал с пином (получен $actual) — файл отброшен, перенос запрещён."
    }
}

# Манифест целостности (ТБ-051): строка = "<SHA256>  <имя файла>", как у packages.sha256.
$manifest = Join-Path $ModelsDir 'models.sha256'
Get-ChildItem $ModelsDir -Filter *.onnx | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Out-File $manifest -Encoding utf8

Write-Host "Готово: $($files.Count) модели в $ModelsDir, манифест models.sha256. Переносятся с папкой offline целиком."
