// Вспомогательные функции страниц пакета «Медиа» — ES-модуль, грузится ЛЕНИВО через
// IJSRuntime.InvokeAsync("import", "./_content/ISC.AI.Modules.Media.UI/js/media.js") только со страниц,
// которым он нужен (Blazor JS isolation): хосту/профилю про модуль знать не нужно (никаких правок App.razor).
// Изображение и видео показываются КАК ЕСТЬ (ТЭ-007): здесь только перемотка к таймкоду и измерение
// натурального размера картинки для CSS-оверлея рамок лиц — никакой обработки пикселей.

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

/**
 * Натуральный размер <img id="..."> ({ width, height }) — координаты рамок лиц (ТФ-МЕД-03) хранятся
 * в пикселях оригинала, а оверлей рисуется в процентах от него, чтобы рамки держались при любом
 * масштабе показа. Если картинка ещё грузится — ждём load/error; при ошибке (404 — нет файла ИЛИ вне
 * допуска, причины не различаем) или отсутствии элемента возвращаем null: рамок не будет, страница живёт.
 */
export function measure(elementId) {
    const img = document.getElementById(elementId);
    if (!img) {
        return Promise.resolve(null);
    }
    const sizeOf = () => (img.naturalWidth > 0 && img.naturalHeight > 0)
        ? { width: img.naturalWidth, height: img.naturalHeight }
        : null;
    if (img.complete) {
        return Promise.resolve(sizeOf());
    }
    return new Promise((resolve) => {
        const done = () => {
            img.removeEventListener('load', done);
            img.removeEventListener('error', done);
            resolve(sizeOf());
        };
        img.addEventListener('load', done);
        img.addEventListener('error', done);
    });
}

// Глобальный алиас для отладки из консоли; страницы используют экспорт модуля.
window.iscaiMedia = { seek, measure };
