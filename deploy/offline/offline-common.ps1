# Общие функции скриптов офлайн-поставки deploy/offline (ТБ-050/051, ТИ-004, ТСТ-001/004).
# Подключается точкой (. "$PSScriptRoot\offline-common.ps1") из export-packages.ps1, verify-packages.ps1,
# export-sherpa-no-tts.ps1 и publish-speech-worker.ps1; сам ничего не делает. Лежит рядом с ними и переносится
# вместе с папкой deploy/offline: без него эти скрипты не запускаются.
#
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.

# Признаки eSpeak NG (GPL-3.0-or-later) в нативной библиотеке: имена функций движка, каталог данных, сообщение
# об ошибке голоса. Сравнение ОРДИНАЛЬНОЕ, с учётом регистра: подстрока «espeak» без учёта регистра есть и в
# сборке sherpa-onnx БЕЗ синтеза речи — внутри имени OfflineSpeakerDiarization («offlin-eSpeak-er», разделение
# по голосам). Это ложное срабатывание, по нему сборку отвергать нельзя (ADR-0026, п. 7).
$GplMarkers = @('espeak_ng_', 'espeak-ng-data', 'mbrola voice file')

# Пакеты, которым запрещено попадать в общий офлайн-фид (ADR-0026, п. 7): управляемая обёртка sherpa-onnx и её
# нативные пакеты org.k2fsa.sherpa.onnx.runtime.* — в 1.13.8 они статически содержат eSpeak NG (GPL-3.0), а
# обёртка тянет их зависимостями для всех платформ. Процесс-распознаватель, которому они нужны, публикуется ВНЕ
# контура (publish-speech-worker.ps1) и переносится готовой папкой; решение для контура восстанавливается по
# фильтру ISC.AI.Offline.slnf, в который утилита и её тесты не входят.
$ForbiddenFeedPackagePattern = '^org\.k2fsa\.sherpa\.onnx'

# Файлы папки — относительные пути с «/», порядок ординальный (стабильный манифест на любой локали).
function Get-RelativeFiles([string]$Root) {
    $full = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('\', '/')
    [string[]]$list = @(Get-ChildItem -LiteralPath $full -Recurse -File -Force | ForEach-Object {
            $_.FullName.Substring($full.Length + 1) -replace '\\', '/'
        })
    [Array]::Sort($list, [StringComparer]::Ordinal)
    $list
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

# Первый найденный в потоке маркер или $null. Latin-1 переводит байты в символы один к одному — ASCII-строки
# бинарника ищутся как есть. Поток читается кусками по 4 МБ с перекрытием на длину маркера без одного символа:
# маркер на стыке кусков не теряется, а большой файл (нативные библиотеки onnxruntime — десятки МБ, внутри
# .nupkg — без распаковки на диск) не читается в память целиком.
function Find-AsciiMarkerInStream([IO.Stream]$Stream, [string[]]$Markers) {
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $overlap = [int](($Markers | Measure-Object -Property Length -Maximum).Maximum) - 1
    $buffer = New-Object byte[] (4MB)
    $tail = ''
    while (($read = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $text = $tail + $latin1.GetString($buffer, 0, $read)
        foreach ($marker in $Markers) {
            if ($text.IndexOf($marker, [StringComparison]::Ordinal) -ge 0) { return $marker }
        }
        $tail = if ($text.Length -gt $overlap) { $text.Substring($text.Length - $overlap) } else { $text }
    }
    return $null
}

# Первый найденный в файле маркер или $null (см. Find-AsciiMarkerInStream). Путь разрешается PowerShell: у .NET
# свой текущий каталог, и относительный путь он понял бы иначе.
function Find-AsciiMarker([string]$Path, [string[]]$Markers) {
    $stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).ProviderPath)
    try { return Find-AsciiMarkerInStream $stream $Markers }
    finally { $stream.Dispose() }
}

# Библиотеки среды C++ (Visual C++ Redistributable), которые импортируют Windows-бинарники: имена в таблице
# импорта — ASCII, регистр у сборок разный.
function Get-CrtImports([string[]]$Paths) {
    $found = @()
    foreach ($path in $Paths) {
        $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path).ProviderPath))
        $found += @([regex]::Matches($text, '(?i)\b(msvcp140(_\d+)?|vcruntime140(_\d+)?)\.dll') | ForEach-Object { $_.Value.ToUpperInvariant() })
    }
    @($found | Sort-Object -Unique)
}

# Лицензионная проверка ОДНОГО пакета фида (ADR-0026, п. 7; ТСТ-001/004): список нарушений, пустой — чисто.
#   * запрещённый пакет — по имени файла И по id из .nuspec внутри (переименованный файл не проскочит);
#   * нативные файлы runtimes/<платформа>/native/* — признаки eSpeak NG по содержимому, как у
#     publish-speech-worker.ps1: так ловится GPL-движок и в пакете с другим именем. Вложенные архивы (.aar,
#     .xcframework.zip мобильных платформ) сжаты и по содержимому не проверяются — для sherpa-onnx их отсекает
#     первая проверка, по id пакета.
function Get-NupkgLicenseProblems([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $name = Split-Path $Path -Leaf
    $problems = @()
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).ProviderPath)
    try {
        $id = $null
        $nuspec = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if ($nuspec) {
            $reader = New-Object IO.StreamReader($nuspec.Open())
            try { $id = ([xml]$reader.ReadToEnd()).package.metadata.id }
            finally { $reader.Dispose() }
        }
        if ($name -match $ForbiddenFeedPackagePattern -or ($id -and $id -match $ForbiddenFeedPackagePattern)) {
            $problems += "ЗАПРЕЩЁННЫЙ ПАКЕТ: $name (id $id) — sherpa-onnx не входит в общий офлайн-фид: его нативные библиотеки из NuGet содержат eSpeak NG, GPL-3.0 (ADR-0026, п. 7)"
        }
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -notmatch '^runtimes/[^/]+/native/[^/]' -or $entry.Length -eq 0) { continue }
            $stream = $entry.Open()
            try { $marker = Find-AsciiMarkerInStream $stream $GplMarkers }
            finally { $stream.Dispose() }
            if ($marker) { $problems += "GPL в пакете $name`: $($entry.FullName) содержит eSpeak NG (найдено «$marker», GPL-3.0-or-later)" }
        }
    }
    finally {
        $zip.Dispose()
    }
    return $problems
}

# Лицензионная проверка всего фида: нарушения по всем .nupkg папки (и тем, что вне манифеста).
function Get-FeedLicenseProblems([string]$FeedDir) {
    $problems = @()
    foreach ($package in @(Get-ChildItem -LiteralPath $FeedDir -Filter *.nupkg -File | Sort-Object Name)) {
        $problems += @(Get-NupkgLicenseProblems $package.FullName)
    }
    return $problems
}
