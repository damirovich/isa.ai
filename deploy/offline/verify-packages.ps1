# Проверка целостности офлайн-фида ПОСЛЕ переноса за периметр (ТБ-051, Э1-01) + запись в журнал
# переноса (transfer-log.txt рядом с фидом; журнал живёт в контуре и в git не попадает).
# Отклоняет фид при любом расхождении: несовпавший хеш, отсутствующий или ЛИШНИЙ (вне манифеста)
# пакет — лишний файл в фиде так же подозрителен, как подменённый.
# Вторая линия — лицензионная (ADR-0026, п. 7; ТСТ-001/004): фид с пакетом org.k2fsa.sherpa.onnx* или с нативным
# файлом runtimes/*/native/*, в котором есть признаки eSpeak NG (GPL-3.0), тоже отклоняется — даже если хеши
# сошлись (фид могли собрать старым export-packages.ps1, по всему решению вместе с процессом-распознавателем).
# Проверка — offline-common.ps1 (переносится вместе с папкой deploy/offline), та же, что в export-packages.ps1.
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param([string]$FeedDir = "$PSScriptRoot\feed")

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')

$manifest = Join-Path $FeedDir 'packages.sha256'
if (-not (Test-Path $manifest)) { throw "Манифест $manifest не найден — фид собран без export-packages.ps1?" }

$bad = @()
$lines = @(Get-Content $manifest | Where-Object { $_ -match '\S' })
foreach ($line in $lines) {
    $parts = $line -split '\s+', 2
    $file = Join-Path $FeedDir $parts[1].Trim()
    if (-not (Test-Path $file)) { $bad += "ОТСУТСТВУЕТ: $($parts[1])" }
    elseif ((Get-FileHash $file -Algorithm SHA256).Hash -ne $parts[0]) { $bad += "ХЕШ НЕ СОВПАЛ: $($parts[1])" }
}
$listed = $lines | ForEach-Object { ($_ -split '\s+', 2)[1].Trim() }
Get-ChildItem $FeedDir -Filter *.nupkg | Where-Object { $listed -notcontains $_.Name } |
    ForEach-Object { $bad += "ВНЕ МАНИФЕСТА: $($_.Name)" }

# Лицензионная проверка — по всем .nupkg папки, включая те, что вне манифеста.
$bad += @(Get-FeedLicenseProblems $FeedDir)

$verdict = if ($bad.Count) { 'ОТКЛОНЕНО: ' + ($bad -join '; ') } else { 'целостность подтверждена, пакетов sherpa-onnx и eSpeak NG нет' }
$entry = '{0} | пакетов {1} | {2} | {3}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $lines.Count, $env:USERNAME, $verdict
Add-Content (Join-Path $PSScriptRoot 'transfer-log.txt') $entry -Encoding utf8

if ($bad.Count) {
    $bad | ForEach-Object { Write-Host $_ }
    throw ('Фид НЕ принят — использовать нельзя. Расхождение с манифестом: повторите перенос. Пакеты sherpa-onnx или ' +
        'eSpeak NG (GPL-3.0): пересоберите фид export-packages.ps1 по ISC.AI.Offline.slnf (ADR-0026, п. 7).')
}
Write-Host "Целостность подтверждена: $($lines.Count) пакетов; пакетов sherpa-onnx и eSpeak NG нет. Запись добавлена в журнал переноса."
