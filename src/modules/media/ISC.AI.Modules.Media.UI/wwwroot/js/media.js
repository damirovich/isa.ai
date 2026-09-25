// Вспомогательные функции страниц пакета «Медиа» — ES-модуль, грузится ЛЕНИВО через
// IJSRuntime.InvokeAsync("import", "./_content/ISC.AI.Modules.Media.UI/js/media.js") только со страниц,
// которым он нужен (Blazor JS isolation): хосту/профилю про модуль знать не нужно (никаких правок App.razor).
// Изображение, видео и аудио показываются КАК ЕСТЬ (ТЭ-007): здесь только перемотка к таймкоду, измерение
// натурального размера картинки для CSS-оверлея рамок лиц и прокрутка к фрагменту расшифровки — никакой
// обработки пикселей и звука.

/**
 * Перемотать проигрыватель (<video> или <audio> с id="...") к секунде.
 * play = false (по умолчанию) — поставить на паузу: кнопка «к кадру» у лица видео показывает КАДР.
 * play = true — запустить воспроизведение с этого места: щелчок по фрагменту расшифровки (ADR-0026).
 * Если метаданные записи ещё не загружены (preload="metadata" не успел), место применяется по событию
 * loadedmetadata. Отказ браузера воспроизводить без жеста пользователя (autoplay policy) глотается:
 * перемотка при этом состоялась, оператор нажмёт «пуск» сам.
 * Возвращает false, если элемента нет (страница уже перерисована) — исключений наружу не даём.
 */
export function seek(elementId, seconds, play) {
    const media = document.getElementById(elementId);
    if (!media || typeof media.currentTime !== 'number') {
        return false;
    }
    try {
        const target = Math.max(0, Number(seconds) || 0);
        if (!play) {
            media.pause();
        }
        if (media.readyState >= 1) {
            media.currentTime = target;
        } else {
            media.addEventListener('loadedmetadata', () => { media.currentTime = target; }, { once: true });
        }
        if (play) {
            const started = media.play();
            if (started && typeof started.catch === 'function') {
                started.catch(() => { /* autoplay policy — перемотка уже сделана */ });
            }
        }
        if (typeof media.scrollIntoView === 'function') {
            media.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
        return true;
    } catch {
        return false;
    }
}

/**
 * Прокрутить к элементу с id="..." (фрагмент расшифровки, к которому пришли из поиска по ?t=) — и в
 * прокручиваемом списке фрагментов, и на странице. Возвращает false, если элемента нет.
 */
export function reveal(elementId) {
    const element = document.getElementById(elementId);
    if (!element || typeof element.scrollIntoView !== 'function') {
        return false;
    }
    try {
        element.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
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
window.iscaiMedia = { seek, reveal, measure };
