# Проверка целостности офлайн-фида ПОСЛЕ переноса за периметр (ТБ-051, Э1-01) + запись в журнал
# переноса (transfer-log.txt рядом с фидом; журнал живёт в контуре и в git не попадает).
# Отклоняет фид при любом расхождении: несовпавший хеш, отсутствующий или ЛИШНИЙ (вне манифеста)
# пакет — лишний файл в фиде так же подозрителен, как подменённый.
param([string]$FeedDir = "$PSScriptRoot\feed")

$ErrorActionPreference = 'Stop'
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

$verdict = if ($bad.Count) { 'ОТКЛОНЕНО: ' + ($bad -join '; ') } else { 'целостность подтверждена' }
$entry = '{0} | пакетов {1} | {2} | {3}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $lines.Count, $env:USERNAME, $verdict
Add-Content (Join-Path $PSScriptRoot 'transfer-log.txt') $entry -Encoding utf8

if ($bad.Count) {
    $bad | ForEach-Object { Write-Host $_ }
    throw 'Целостность НЕ подтверждена — фид использовать нельзя, повторите перенос.'
}
Write-Host "Целостность подтверждена: $($lines.Count) пакетов. Запись добавлена в журнал переноса."
