// Вспомогательные функции страниц пакета «Медиа» — ES-модуль, грузится ЛЕНИВО через
// IJSRuntime.InvokeAsync("import", "./_content/ISC.AI.Modules.Media.UI/js/media.js") только со страниц,
// которым он нужен (Blazor JS isolation): хосту/профилю про модуль знать не нужно (никаких правок App.razor).
// Видео показывается КАК ЕСТЬ (ТЭ-007): здесь только перемотка к таймкоду, никакой обработки кадров.

/**
 * Перемотать <video id="..."> к секунде и поставить на паузу — кнопка «к кадру» у лица видео.
 * Возвращает false, если элемента нет (страница уже перерисована) — исключений наружу не даём.
 */
export function seek(elementId, seconds) {
    const video = document.getElementById(elementId);
    if (!video || typeof video.currentTime !== 'number') {
        return false;
    }
    try {
        video.pause();
        video.currentTime = Math.max(0, Number(seconds) || 0);
        if (typeof video.scrollIntoView === 'function') {
            video.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
        return true;
    } catch {
        return false;
    }
}

// Глобальный алиас для отладки из консоли; страницы используют экспорт модуля.
window.iscaiMedia = { seek };
