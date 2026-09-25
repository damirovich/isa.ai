# Экспорт офлайн-фида пакетов (ТБ-050/051, Э1-01). Запускается на машине С ИНТЕРНЕТОМ.
# Восстанавливает решение по фильтру ISC.AI.Offline.slnf в чистую папку, собирает все .nupkg в плоский фид
# deploy/offline/feed и пишет манифест целостности packages.sha256 (SHA256 каждого пакета) —
# по нему verify-packages.ps1 подтверждает целостность после переноса за периметр.
#
# ПОЧЕМУ ФИЛЬТР, А НЕ ВСЁ РЕШЕНИЕ (ADR-0026, п. 7; ТСТ-001/004). В ISC.AI.slnx есть процесс-распознаватель речи
# ISC.AI.Speech.Worker. Его пакет org.k2fsa.sherpa.onnx тянет зависимостями org.k2fsa.sherpa.onnx.runtime.* всех
# платформ, а их нативные библиотеки статически содержат движок синтеза речи eSpeak NG (GPL-3.0). Такие пакеты не
# должны уходить за периметр ни с одной поставкой — и с «ИнспекторAI», где речи нет вовсе. Фильтр
# ISC.AI.Offline.slnf — всё решение, кроме утилиты и её тестов (ISC.AI.Speech.Worker.Tests); его состав и
# отсутствие ссылок на утилиту закрепляет тест OfflineSolutionFilterTests. Утилита публикуется вне контура
# (publish-speech-worker.ps1) и переносится ГОТОВОЙ папкой с манифестом worker.sha256.
# Вторая линия — проверка самого фида (offline-common.ps1): пакет org.k2fsa.sherpa.onnx* или нативный файл
# runtimes/*/native/* с признаками eSpeak NG — отказ, фид очищается. Та же проверка — в verify-packages.ps1.
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param([string]$FeedDir = "$PSScriptRoot\feed")

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')

$repoRoot = Resolve-Path "$PSScriptRoot\..\.."
$solutionFilter = Join-Path $repoRoot 'ISC.AI.Offline.slnf'
if (-not (Test-Path -LiteralPath $solutionFilter)) {
    throw "Нет фильтра решения $solutionFilter — офлайн-фид собирается только по нему, без процесса-распознавателя речи (ADR-0026, п. 7)."
}
$tmp = Join-Path $env:TEMP 'isc-offline-packages'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

# --force: игнорировать кэши, чтобы фид собрался полным даже после частичных restore. Восстанавливаются проекты
# фильтра И проекты, на которые они ссылаются, — поэтому в фильтре важны и ссылки (OfflineSolutionFilterTests).
dotnet restore $solutionFilter --packages $tmp --force
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore завершился с ошибкой — фид не собран.' }

# Прежний фид убирается целиком, с манифестом: при отказе ниже не должно остаться пары «старый манифест +
# неполный фид».
New-Item -ItemType Directory -Force $FeedDir | Out-Null
Get-ChildItem $FeedDir -Filter *.nupkg | Remove-Item -Force
$manifest = Join-Path $FeedDir 'packages.sha256'
if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }

# Плоская папка с .nupkg — законный локальный источник NuGet (deploy/offline/nuget.config).
Get-ChildItem $tmp -Recurse -Filter *.nupkg |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $FeedDir $_.Name) -Force }
Remove-Item $tmp -Recurse -Force

# Лицензионная проверка фида (ADR-0026, п. 7; ТСТ-001/004) — ДО манифеста: фид с GPL-кодом не собирается вовсе.
Write-Host 'Проверка фида: пакеты sherpa-onnx и признаки eSpeak NG (GPL-3.0) в нативных библиотеках...'
$licenseProblems = @(Get-FeedLicenseProblems $FeedDir)
if ($licenseProblems.Count) {
    Get-ChildItem $FeedDir -Filter *.nupkg | Remove-Item -Force
    $licenseProblems | ForEach-Object { Write-Host $_ }
    throw ('Фид НЕ собран и очищен: в нём пакеты sherpa-onnx или нативный GPL-код (eSpeak NG). Проверьте, что ' +
        'ISC.AI.Offline.slnf не содержит ISC.AI.Speech.Worker и его тестов и ни один проект фильтра на них не ссылается ' +
        '(dotnet test tests/ISC.AI.UnitTests --filter "FullyQualifiedName~OfflineSolutionFilter"; ADR-0026, п. 7).')
}

# Манифест целостности (ТБ-051): строка = "<SHA256>  <имя файла>", сортировка — для стабильного диффа.
Get-ChildItem $FeedDir -Filter *.nupkg | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Out-File $manifest -Encoding utf8

$count = (Get-ChildItem $FeedDir -Filter *.nupkg).Count
Write-Host "Готово: $count пакетов в $FeedDir, манифест packages.sha256; пакетов sherpa-onnx и eSpeak NG нет. Переносите папку offline целиком."
Write-Host 'В контуре: verify-packages.ps1, затем dotnet restore ISC.AI.Offline.slnf --configfile deploy\offline\nuget.config.'
