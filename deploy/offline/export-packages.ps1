# Экспорт офлайн-фида пакетов (ТБ-050/051, Э1-01). Запускается на машине С ИНТЕРНЕТОМ.
# Делает полное восстановление решения в чистую папку, собирает все .nupkg в плоский фид
# deploy/offline/feed и пишет манифест целостности packages.sha256 (SHA256 каждого пакета) —
# по нему verify-packages.ps1 подтверждает целостность после переноса за периметр.
param([string]$FeedDir = "$PSScriptRoot\feed")

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path "$PSScriptRoot\..\.."
$tmp = Join-Path $env:TEMP 'isc-offline-packages'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

# --force: игнорировать кэши, чтобы фид собрался полным даже после частичных restore.
dotnet restore (Join-Path $repoRoot 'ISC.AI.slnx') --packages $tmp --force
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore завершился с ошибкой — фид не собран.' }

New-Item -ItemType Directory -Force $FeedDir | Out-Null
Get-ChildItem $FeedDir -Filter *.nupkg | Remove-Item -Force

# Плоская папка с .nupkg — законный локальный источник NuGet (deploy/offline/nuget.config).
Get-ChildItem $tmp -Recurse -Filter *.nupkg |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $FeedDir $_.Name) -Force }

# Манифест целостности (ТБ-051): строка = "<SHA256>  <имя файла>", сортировка — для стабильного диффа.
$manifest = Join-Path $FeedDir 'packages.sha256'
Get-ChildItem $FeedDir -Filter *.nupkg | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Out-File $manifest -Encoding utf8

Remove-Item $tmp -Recurse -Force
$count = (Get-ChildItem $FeedDir -Filter *.nupkg).Count
Write-Host "Готово: $count пакетов в $FeedDir, манифест packages.sha256. Переносите папку offline целиком."
