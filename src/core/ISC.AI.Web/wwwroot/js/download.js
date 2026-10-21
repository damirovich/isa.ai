// Скачивание файла из потока (.docx экспорт и т. п.) — Blazor Server JS-interop.
// Универсальный хелпер хоста: страницы модулей вызывают downloadFileFromStream(имя, поток).
window.downloadFileFromStream = async (fileName, contentStreamReference) => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName ?? 'document';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
};

// Открыть файл из потока в новой вкладке (PDF — встроенным просмотрщиком браузера, «Печать»).
// false — браузер не дал открыть вкладку (блокировка всплывающих окон): страница тогда скачивает файл.
// Адрес освобождается через минуту: вкладке нужно успеть загрузить файл.
window.openFileFromStream = async (contentStreamReference, contentType) => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: contentType ?? 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const opened = window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    return !!opened;
};
