// Вспомогательные функции страниц пакета «Медиа» — ES-модуль, грузится ЛЕНИВО через
// IJSRuntime.InvokeAsync("import", "./_content/ISC.AI.Modules.Media.UI/js/media.js") только со страниц,
// которым он нужен (Blazor JS isolation): хосту/профилю про модуль знать не нужно (никаких правок App.razor).
// Изображение, видео и аудио показываются КАК ЕСТЬ (ТЭ-007): здесь только перемотка к таймкоду, шаг по кадрам
// браузерного <video> (ADR-0028), измерение натурального размера картинки для CSS-оверлея рамок лиц и
// прокрутка к фрагменту расшифровки — никакой обработки пикселей и звука.

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

/**
 * Текущий момент проигрывателя <video id="..."> в секундах; null — элемента нет (страница уже перерисована).
 * Нужен кнопке «Снимок кадра» (ADR-0028): снимок берётся с того момента, на котором оператор остановил
 * запись мышью или клавишами, а не с последнего известного компоненту.
 */
export function currentTime(elementId) {
    const media = document.getElementById(elementId);
    if (!media || typeof media.currentTime !== 'number') {
        return null;
    }
    return media.currentTime;
}

/**
 * Кадр, который браузер показывает в момент t: кадр N занимает [N/fps, (N+1)/fps), поэтому N = floor(t·fps)
 * (НЕ round: середина кадра при округлении к ближайшему давала бы N+1 — шаг «через один» вперёд и «на месте»
 * назад). Малый допуск компенсирует погрешность произведения у границы кадра.
 */
function frameIndexAt(seconds, fps) {
    return Math.floor(seconds * fps + 1e-6);
}

/** Последний кадр по длительности элемента: ceil(D·fps) − 1, не меньше 0; null — длительность неизвестна. */
function lastFrameIndexOf(media, fps) {
    if (!Number.isFinite(media.duration) || media.duration <= 0) {
        return null;
    }
    return Math.max(0, Math.ceil(media.duration * fps - 1e-6) - 1);
}

/**
 * Шаг на один кадр браузерного <video id="..."> (ADR-0028, п. 3): direction = +1 — следующий, −1 — предыдущий.
 * frameSeconds — длительность кадра (1/fps; без известной частоты — 0,04 с). Видео ставится на паузу; текущий
 * кадр N = floor(t·fps), целевой N±1 зажимается в [0, последний кадр], момент ставится в СЕРЕДИНУ целевого
 * кадра: currentTime = (N' + 0,5)/fps — середина устойчива к округлению меток времени контейнера (ровно на границе
 * кадра браузер может показать соседний). Возвращает новый currentTime (секунды) или null, если элемента нет.
 * Полностью в браузере — обращений к серверу нет (нужен только оригинал, уже отданный эндпоинтом файла).
 * Компоненту момент приходит событием seeked (см. bindFrameKeys) и как результат этого вызова.
 */
export function stepFrame(elementId, frameSeconds, direction) {
    const media = document.getElementById(elementId);
    if (!media || typeof media.currentTime !== 'number') {
        return null;
    }
    try {
        const step = Number(frameSeconds) > 0 ? Number(frameSeconds) : 0.04;
        const fps = 1 / step;
        const dir = Number(direction) < 0 ? -1 : 1;
        media.pause();
        let index = frameIndexAt(media.currentTime, fps) + dir;
        if (index < 0) {
            index = 0;
        }
        const last = lastFrameIndexOf(media, fps);
        if (last !== null && index > last) {
            index = last;
        }
        media.currentTime = (index + 0.5) / fps;
        return media.currentTime;
    } catch {
        return null;
    }
}

// Привязки покадрового просмотра по id проигрывателя (клавиши на document + seeked/pause на элементе):
// одна на элемент, снимаются в unbindFrameKeys (dispose компонента).
const frameKeyBindings = new Map();

/**
 * Клавиши и слежение за положением для <video id="...">: «,» — предыдущий кадр, «.» — следующий (как в монтажных
 * программах). Слушатель keydown вешается на document, чтобы не требовать фокуса на видео; нажатия в полях
 * ввода (input/textarea/select/contenteditable) и с модификаторами Ctrl/Alt/Meta игнорируются — там знаки
 * препинания набирают. О НОВОМ ПОЛОЖЕНИИ компонент узнаёт по событиям seeked и pause самого элемента через
 * dotNetRef.invokeMethodAsync('OnFrameStepped', seconds): так подпись «кадр № N» следует и за шагом клавишей, и
 * за перемоткой ползунком, и за паузой мышью, а не только за кнопками. Без dotNetRef — только шаг.
 * Повторный вызов для того же id заменяет прежнюю привязку (частота кадров стала известна после переиндексации).
 */
export function bindFrameKeys(elementId, frameSeconds, dotNetRef) {
    unbindFrameKeys(elementId);
    const keyHandler = (event) => {
        if (event.key !== ',' && event.key !== '.') {
            return;
        }
        if (event.ctrlKey || event.altKey || event.metaKey || isTextInput(event.target)) {
            return;
        }
        if (stepFrame(elementId, frameSeconds, event.key === '.' ? 1 : -1) !== null) {
            event.preventDefault();
        }
    };
    const media = document.getElementById(elementId);
    const positionHandler = () => {
        if (media && dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function') {
            dotNetRef.invokeMethodAsync('OnFrameStepped', media.currentTime).catch(() => { /* circuit уже закрыт */ });
        }
    };
    document.addEventListener('keydown', keyHandler);
    if (media) {
        media.addEventListener('seeked', positionHandler);
        media.addEventListener('pause', positionHandler);
    }
    frameKeyBindings.set(elementId, { keyHandler, media, positionHandler });
    return true;
}

/** Снять клавиши и слежение за положением для элемента (dispose компонента). */
export function unbindFrameKeys(elementId) {
    const binding = frameKeyBindings.get(elementId);
    if (!binding) {
        return false;
    }
    document.removeEventListener('keydown', binding.keyHandler);
    if (binding.media) {
        binding.media.removeEventListener('seeked', binding.positionHandler);
        binding.media.removeEventListener('pause', binding.positionHandler);
    }
    frameKeyBindings.delete(elementId);
    return true;
}

function isTextInput(target) {
    if (!target || typeof target.tagName !== 'string') {
        return false;
    }
    const tag = target.tagName.toUpperCase();
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable === true;
}

// --- Предпросмотр выбранных файлов ДО загрузки (ТФ-МЕД-17: оператор видит, какому снимку ставит дату) ---
// Файл показывается прямо с диска пользователя через blob-URL: на сервер ради предпросмотра ничего не уходит.
// Слушатель change — на фазе перехвата у document: он срабатывает раньше, чем Blazor передаст выбор серверу,
// поэтому к моменту запроса URL уже есть. Ключ — имя и размер (так же страница отличает файлы в списке).
const uploadPreviews = new Map();
let uploadPreviewsBound = false;

function previewKey(name, size) {
    return name + '|' + size;
}

export function bindUploadPreviews() {
    if (uploadPreviewsBound) {
        return;
    }

    uploadPreviewsBound = true;
    document.addEventListener('change', e => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement) || input.type !== 'file' || !input.files) {
            return;
        }

        for (const file of input.files) {
            const key = previewKey(file.name, file.size);
            if (!uploadPreviews.has(key)) {
                uploadPreviews.set(key, URL.createObjectURL(file));
            }
        }
    }, true);
}

export function uploadPreviewUrl(name, size) {
    return uploadPreviews.get(previewKey(name, size)) ?? null;
}

export function releaseUploadPreview(name, size) {
    const key = previewKey(name, size);
    const url = uploadPreviews.get(key);
    if (url) {
        URL.revokeObjectURL(url);
        uploadPreviews.delete(key);
    }
}

export function releaseAllUploadPreviews() {
    for (const url of uploadPreviews.values()) {
        URL.revokeObjectURL(url);
    }

    uploadPreviews.clear();
}

// Глобальный алиас для отладки из консоли; страницы используют экспорт модуля.
window.iscaiMedia = { seek, reveal, measure, currentTime, stepFrame, bindFrameKeys, unbindFrameKeys };
