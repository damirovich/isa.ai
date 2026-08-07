// Скачивание сформированного файла из Blazor Server.
// ЛЕНИВО через IJSRuntime.InvokeAsync("import", ...) со страницы отчётов (Blazor JS isolation) —
// глобального скрипта в оболочке хоста нет, модуль пакета не должен его туда добавлять.
//
// Байты приходят потоком (DotNetStreamReference), а не строкой base64: отчёт по большому периоду
// весит мегабайты, а base64 раздувает их ещё в полтора раза и целиком держит в памяти дважды —
// и на сервере, и в браузере.
export async function saveStream(fileName, contentType, streamReference) {
    const buffer = await streamReference.arrayBuffer();
    const blob = new Blob([buffer], { type: contentType });
    const url = URL.createObjectURL(blob);

    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName ?? 'report';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();

    // Освобождать объектный URL обязательно: иначе браузер держит blob до перезагрузки вкладки,
    // а страницу отчётов открывают надолго и жмут «Сформировать» много раз подряд.
    URL.revokeObjectURL(url);
}
