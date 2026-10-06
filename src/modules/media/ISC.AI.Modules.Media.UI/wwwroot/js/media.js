// Вспомогательные функции страниц пакета «Медиа» — ES-модуль, грузится ЛЕНИВО через
// IJSRuntime.InvokeAsync("import", "./_content/ISC.AI.Modules.Media.UI/js/media.js") только со страниц,
// которым он нужен (Blazor JS isolation): хосту/профилю про модуль знать не нужно (никаких правок App.razor).
// Изображение, видео и аудио показываются КАК ЕСТЬ (ТЭ-007): здесь только перемотка к таймкоду, шаг по кадрам
// браузерного <video> (ADR-0028), измерение натурального размера картинки для CSS-оверлея рамок лиц и
// прокрутка к фрагменту расшифровки, лента под видео (ADR-0038) — никакой обработки пикселей и звука.

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

// --- Лента под видео (ADR-0038, ТФ-МЕД-11) ---
// Разметку ленты (деления, отметки, легенду) рисует компонент VideoTimeline процентами от длительности. Здесь —
// то, что зависит от ширины экрана и от проигрывателя: плитки ленты кадров (одна картинка-ряд, раздаётся под
// решёткой носителя; плиток столько, сколько помещается без искажения пропорций), бегунок, подпись под курсором,
// текущий момент и время записи. Переход по ленте в режиме <video> — прямо в браузере (currentTime), в режиме кадров
// с сервера — один вызов OnTimelineSeek при отпускании кнопки. Никакой обработки изображения (ТЭ-007).
const timelines = new Map();

function pad(value, width) {
    return String(value).padStart(width, '0');
}

/** Момент «мм:сс.ммм» (от часа записи — «чч:мм:сс.ммм»), как PreciseTimecode на сервере. */
function timelineClock(ms, durationMs) {
    const total = Math.max(0, Math.round(ms));
    const hours = Math.floor(total / 3600000);
    const minutes = Math.floor(total / 60000) % 60;
    const seconds = Math.floor(total / 1000) % 60;
    const tail = `${pad(minutes, 2)}:${pad(seconds, 2)}.${pad(total % 1000, 3)}`;
    return durationMs >= 3600000 ? `${pad(hours, 2)}:${tail}` : tail;
}

/**
 * Время записи в момент offsetMs: опора — «настенное» время пояса сервера, записанное как UTC (VideoTimelineModel.
 * ClockBase), поэтому читается UTC-полями — пояс браузера на результат не влияет. Та же формула, что RecordTime на сервере.
 */
function recordClockText(startMs, approximate, offsetMs) {
    const d = new Date(startMs + Math.max(0, Math.round(offsetMs)));
    const date = `${pad(d.getUTCDate(), 2)}.${pad(d.getUTCMonth() + 1, 2)}.${d.getUTCFullYear()}`;
    const time = `${pad(d.getUTCHours(), 2)}:${pad(d.getUTCMinutes(), 2)}:${pad(d.getUTCSeconds(), 2)}`;
    return approximate ? `≈ ${date} ${time}` : `${date} ${time}.${pad(d.getUTCMilliseconds(), 3)}`;
}

function normalizeTimelineOptions(options) {
    const o = options || {};
    return {
        durationMs: Math.max(1, Number(o.durationMs) || 1),
        playable: o.playable === true,
        videoId: o.videoId || null,
        filmstripUrl: o.filmstripUrl || null,
        tileCount: Math.max(0, Number(o.tileCount) || 0),
        stepMs: Math.max(0, Number(o.stepMs) || 0),
        tileAspect: Number(o.tileAspect) > 0 ? Number(o.tileAspect) : 16 / 9,
        recordStartMs: Number.isFinite(o.recordStartMs) ? o.recordStartMs : null,
        recordApproximate: o.recordApproximate === true,
        startMs: Math.max(0, Number(o.startMs) || 0),
    };
}

/** Плитки ленты кадров по ширине полосы: плитка i — кадр, ближайший к середине своего отрезка времени. */
function layoutFilmstrip(state) {
    const { strip, options } = state;
    if (!strip || !options.filmstripUrl || options.tileCount <= 0) {
        return;
    }
    const width = strip.clientWidth;
    const height = strip.clientHeight;
    if (!(width > 0 && height > 0)) {
        return;
    }
    const count = options.tileCount;
    const slots = Math.max(1, Math.round(width / (height * options.tileAspect)));
    const fragment = document.createDocumentFragment();
    for (let slot = 0; slot < slots; slot++) {
        const center = (slot + 0.5) / slots * options.durationMs;
        const index = options.stepMs > 0
            ? Math.min(count - 1, Math.max(0, Math.round(center / options.stepMs)))
            : Math.min(count - 1, Math.floor((slot + 0.5) / slots * count));
        const tile = document.createElement('div');
        // Картинка-ряд растянута на count ширин плитки; позиция i/(count−1) ставит в окно ровно плитку i.
        tile.style.cssText = 'position:absolute;top:0;bottom:0;background-repeat:no-repeat;'
            + 'box-shadow:inset -1px 0 0 rgba(0,0,0,.55);pointer-events:none;';
        tile.style.left = `${slot / slots * 100}%`;
        tile.style.width = `${100 / slots}%`;
        tile.style.backgroundImage = `url("${options.filmstripUrl}")`;
        tile.style.backgroundSize = `${count * 100}% 100%`;
        tile.style.backgroundPosition = `${count > 1 ? index / (count - 1) * 100 : 0}% 0`;
        fragment.appendChild(tile);
    }
    strip.replaceChildren(fragment);
}

/** Бегунок, текущий момент и время записи — в момент ms. */
function showTimelinePosition(state, ms) {
    const { options } = state;
    const position = Math.min(Math.max(0, ms), options.durationMs);
    state.positionMs = position;
    if (state.playhead) {
        state.playhead.style.left = `${position / options.durationMs * 100}%`;
    }
    if (state.clock) {
        state.clock.textContent = `${timelineClock(position, options.durationMs)} / ${timelineClock(options.durationMs, options.durationMs)}`;
    }
    if (state.record) {
        state.record.textContent = options.recordStartMs === null
            ? ''
            : `запись: ${recordClockText(options.recordStartMs, options.recordApproximate, position)}`;
    }
}

function timelineMsAt(state, clientX) {
    const rect = state.area.getBoundingClientRect();
    if (!(rect.width > 0)) {
        return 0;
    }
    const x = Math.min(Math.max(clientX - rect.left, 0), rect.width);
    return x / rect.width * state.options.durationMs;
}

function showTimelineHover(state, clientX) {
    if (!state.hover) {
        return;
    }
    const ms = timelineMsAt(state, clientX);
    const { options } = state;
    let text = timelineClock(ms, options.durationMs);
    if (options.recordStartMs !== null) {
        text += ` · ${recordClockText(options.recordStartMs, options.recordApproximate, ms)}`;
    }
    state.hover.textContent = text;
    state.hover.style.left = `${ms / options.durationMs * 100}%`;
    state.hover.style.display = 'block';
}

/**
 * Переход к моменту с ленты. <video>: во время перетаскивания — быстрый переход к ближайшему ключевому кадру
 * (fastSeek, где он есть), при отпускании — точный. Кадры с сервера: перетаскивание только двигает бегунок, кадр
 * запрашивается один раз — при отпускании.
 */
function scrubTimeline(state, ms, final) {
    showTimelinePosition(state, ms);
    const video = state.video;
    if (state.options.playable && video) {
        try {
            if (!final && typeof video.fastSeek === 'function') {
                video.fastSeek(ms / 1000);
            } else {
                video.currentTime = ms / 1000;
            }
        } catch {
            /* метаданные записи ещё не загружены — переход не состоялся, бегунок покажет фактическое положение */
        }
    } else if (final && state.dotNetRef && typeof state.dotNetRef.invokeMethodAsync === 'function') {
        state.dotNetRef.invokeMethodAsync('OnTimelineSeek', Math.round(ms)).catch(() => { /* circuit уже закрыт */ });
    }
}

function listenTimeline(state, target, type, handler) {
    target.addEventListener(type, handler);
    state.listeners.push(() => target.removeEventListener(type, handler));
}

function bindTimelineVideo(state) {
    const video = state.options.playable && state.options.videoId ? document.getElementById(state.options.videoId) : null;
    state.video = video;
    if (!video) {
        return;
    }
    const follow = () => {
        if (!state.dragging) {
            showTimelinePosition(state, video.currentTime * 1000);
        }
    };
    // Во время воспроизведения — по кадрам анимации (timeupdate приходит лишь 4 раза в секунду, бегунок дёргался бы).
    const animate = () => {
        follow();
        state.frame = !video.paused && !video.ended ? requestAnimationFrame(animate) : 0;
    };
    listenTimeline(state, video, 'timeupdate', follow);
    listenTimeline(state, video, 'seeked', follow);
    listenTimeline(state, video, 'loadedmetadata', follow);
    listenTimeline(state, video, 'play', () => {
        if (!state.frame) {
            state.frame = requestAnimationFrame(animate);
        }
    });
    listenTimeline(state, video, 'pause', follow);
    listenTimeline(state, video, 'ended', follow);
}

/**
 * Включить ленту в элементе id="rootId" (VideoTimeline). options — настройки компонента (длительность, режим,
 * картинка ленты, опора часов записи); dotNetRef — компонент (OnTimelineSeek). Повторный вызов заменяет прежнюю
 * привязку. Возвращает false, если элемента нет.
 */
export function initTimeline(rootId, options, dotNetRef) {
    disposeTimeline(rootId);
    const root = document.getElementById(rootId);
    if (!root) {
        return false;
    }
    const state = {
        root,
        options: normalizeTimelineOptions(options),
        dotNetRef,
        area: root.querySelector('[data-vt-area]'),
        strip: root.querySelector('[data-vt-strip]'),
        playhead: root.querySelector('[data-vt-playhead]'),
        hover: root.querySelector('[data-vt-hover]'),
        clock: root.querySelector('[data-vt-clock]'),
        record: root.querySelector('[data-vt-record]'),
        video: null,
        positionMs: 0,
        dragging: false,
        frame: 0,
        observer: null,
        listeners: [],
    };
    if (!state.area) {
        return false;
    }
    timelines.set(rootId, state);

    listenTimeline(state, state.area, 'pointerdown', (event) => {
        // Отметки обрабатывает компонент (переход к началу отрезка); здесь — только сама лента.
        if (event.button !== 0 || (event.target instanceof Element && event.target.closest('[data-vt-mark]'))) {
            return;
        }
        state.dragging = true;
        if (typeof state.area.setPointerCapture === 'function') {
            state.area.setPointerCapture(event.pointerId);
        }
        scrubTimeline(state, timelineMsAt(state, event.clientX), false);
        event.preventDefault();
    });
    listenTimeline(state, state.area, 'pointermove', (event) => {
        showTimelineHover(state, event.clientX);
        if (state.dragging) {
            scrubTimeline(state, timelineMsAt(state, event.clientX), false);
        }
    });
    listenTimeline(state, state.area, 'pointerup', (event) => {
        if (state.dragging) {
            state.dragging = false;
            scrubTimeline(state, timelineMsAt(state, event.clientX), true);
        }
    });
    listenTimeline(state, state.area, 'pointercancel', () => {
        state.dragging = false;
    });
    listenTimeline(state, state.area, 'pointerleave', () => {
        if (state.hover && !state.dragging) {
            state.hover.style.display = 'none';
        }
    });

    if (typeof ResizeObserver === 'function' && state.strip) {
        let pending = 0;
        state.observer = new ResizeObserver(() => {
            if (!pending) {
                pending = requestAnimationFrame(() => {
                    pending = 0;
                    layoutFilmstrip(state);
                });
            }
        });
        state.observer.observe(state.strip);
    }

    bindTimelineVideo(state);
    layoutFilmstrip(state);
    showTimelinePosition(state, state.video ? state.video.currentTime * 1000 : state.options.startMs);
    return true;
}

/**
 * Обновить настройки ленты (переиндексация дала ленту кадров, сменилась длительность или опора часов). Разметка
 * компонента при этом перерисована — элементы ищутся заново, бегунок остаётся на прежнем моменте.
 */
export function updateTimeline(rootId, options, dotNetRef) {
    const state = timelines.get(rootId);
    if (!state) {
        return initTimeline(rootId, options, dotNetRef);
    }
    const position = state.positionMs;
    initTimeline(rootId, options, dotNetRef || state.dotNetRef);
    const fresh = timelines.get(rootId);
    if (fresh && !fresh.video) {
        showTimelinePosition(fresh, position);
    }
    return true;
}

/** Бегунок к моменту ms (режим кадров с сервера: момент меняет просмотрщик). Во время перетаскивания не мешаем. */
export function setTimelinePosition(rootId, ms) {
    const state = timelines.get(rootId);
    if (!state) {
        return false;
    }
    if (!state.dragging) {
        showTimelinePosition(state, Number(ms) || 0);
    }
    return true;
}

/** Снять слушатели ленты (dispose компонента): проигрыватель, указатель, наблюдатель размера, кадры анимации. */
export function disposeTimeline(rootId) {
    const state = timelines.get(rootId);
    if (!state) {
        return false;
    }
    for (const remove of state.listeners) {
        remove();
    }
    if (state.observer) {
        state.observer.disconnect();
    }
    if (state.frame) {
        cancelAnimationFrame(state.frame);
    }
    timelines.delete(rootId);
    return true;
}

// Глобальный алиас для отладки из консоли; страницы используют экспорт модуля.
window.iscaiMedia = { seek, reveal, measure, currentTime, stepFrame, bindFrameKeys, unbindFrameKeys };
