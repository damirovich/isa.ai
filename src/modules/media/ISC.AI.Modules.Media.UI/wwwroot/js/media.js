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

// --- Проигрыватель аудиозаписи (ADR-0047, ТФ-МЕД-18) ---
// Разметку (кнопки, скорость, громкость) рисует компонент AudioPlayer; здесь — то, что зависит от ширины экрана и от
// самого <audio>: волна записи на <canvas> (огибающая громкости с сервера, /media/waveforms/{id}), полоса участков
// речи, бегунок, время, переход щелчком и перетаскиванием, клавиши. На сервер уходят только смена «играет/пауза»,
// скорости и звука (OnPlayerState). У элементов, которые меняет этот код (canvas, подсказка, время, состояние волны),
// НЕТ детей в разметке Blazor — диф Blazor их не трогает. Звук не обрабатывается (ТЭ-007): скорость и громкость —
// свойства воспроизведения в браузере.
const audioPlayers = new Map();

// Сколько ждать волну до надписи «строится…»: короткое ожидание не мигает надписью.
const WAVEFORM_HINT_DELAY_MS = 400;

function audioClock(seconds) {
    const total = Math.max(0, Math.floor(Number(seconds) || 0));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor(total / 60) % 60;
    const secs = total % 60;
    return hours > 0 ? `${hours}:${pad(minutes, 2)}:${pad(secs, 2)}` : `${pad(minutes, 2)}:${pad(secs, 2)}`;
}

function normalizeAudioOptions(options) {
    const o = options || {};
    const ranges = Array.isArray(o.ranges)
        ? o.ranges.filter((r) => Array.isArray(r) && r.length >= 2).map((r) => [Number(r[0]) || 0, Number(r[1]) || 0])
        : [];
    return {
        elementId: o.elementId || null,
        waveformUrl: o.waveformUrl || null,
        durationMs: Math.max(0, Number(o.durationMs) || 0),
        ranges,
        keys: o.keys !== false,
    };
}

/** Длительность для шкалы, секунды: от браузера, иначе из реквизитов, иначе по волне; 0 — неизвестна. */
function audioDuration(state) {
    const media = state.media;
    if (media && Number.isFinite(media.duration) && media.duration > 0) {
        return media.duration;
    }
    if (state.options.durationMs > 0) {
        return state.options.durationMs / 1000;
    }
    if (state.wave) {
        return state.wave.stepMs * state.wave.peaks.length / 1000;
    }
    return 0;
}

function cssColor(element, name, fallback) {
    try {
        const value = getComputedStyle(element).getPropertyValue(name).trim();
        return value || fallback;
    } catch {
        return fallback;
    }
}

function decodePeaks(base64) {
    const binary = atob(base64 || '');
    const peaks = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        peaks[i] = binary.charCodeAt(i);
    }
    return peaks;
}

/**
 * Высоты столбиков волны под текущую ширину (0–1): столбик — наибольший пик своего отрезка времени, нормировка по
 * наибольшему пику всей записи и корень — тихая запись тоже видна (только картинка, звук не меняется).
 */
function layoutAudioBars(state) {
    const canvas = state.canvas;
    const dpr = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }
    const barWidth = Math.max(1, Math.round(2 * dpr));
    const gap = Math.max(1, Math.round(dpr));
    const count = Math.max(1, Math.floor(width / (barWidth + gap)));
    const duration = audioDuration(state);
    const bars = new Float32Array(count);
    if (state.wave && state.wave.peaks.length > 0 && duration > 0) {
        const { peaks, stepMs } = state.wave;
        let max = 0;
        for (let i = 0; i < peaks.length; i++) {
            if (peaks[i] > max) {
                max = peaks[i];
            }
        }
        for (let bar = 0; bar < count; bar++) {
            const from = Math.floor(bar / count * duration * 1000 / stepMs);
            const to = Math.max(from + 1, Math.ceil((bar + 1) / count * duration * 1000 / stepMs));
            let level = 0;
            for (let i = from; i < to && i < peaks.length; i++) {
                if (peaks[i] > level) {
                    level = peaks[i];
                }
            }
            bars[bar] = max > 0 ? Math.sqrt(level / max) : 0;
        }
    }
    state.layout = { width, height, dpr, barWidth, gap, count, bars, duration };
}

/** Перерисовка: волна (сыгранная часть — цветом), полоса речи снизу, бегунок; время — в подписи. */
function drawAudioPlayer(state) {
    if (!state.layout || state.layout.duration !== audioDuration(state)) {
        layoutAudioBars(state);
    }
    const { width, height, dpr, barWidth, gap, count, bars, duration } = state.layout;
    const context = state.canvas.getContext('2d');
    if (!context) {
        return;
    }
    const position = state.media ? state.media.currentTime : 0;
    const played = duration > 0 ? Math.min(1, position / duration) : 0;
    const bandHeight = Math.round(5 * dpr);
    const bandGap = Math.round(4 * dpr);
    const waveHeight = Math.max(1, height - bandHeight - bandGap);
    const middle = waveHeight / 2;

    context.clearRect(0, 0, width, height);
    const playedColor = state.colors.played;
    const restColor = state.colors.rest;
    if (state.wave) {
        for (let bar = 0; bar < count; bar++) {
            const x = bar * (barWidth + gap);
            const h = Math.max(dpr, bars[bar] * (waveHeight - 2 * dpr));
            context.fillStyle = (bar + 0.5) / count <= played ? playedColor : restColor;
            context.fillRect(x, middle - h / 2, barWidth, h);
        }
    } else {
        // Волны нет (строится или не построилась) — простая полоса прослушанного.
        const lineHeight = Math.max(2, Math.round(4 * dpr));
        context.fillStyle = restColor;
        context.fillRect(0, middle - lineHeight / 2, width, lineHeight);
        context.fillStyle = playedColor;
        context.fillRect(0, middle - lineHeight / 2, width * played, lineHeight);
    }

    // Полоса речи: участки фрагментов расшифровки.
    if (duration > 0 && state.options.ranges.length > 0) {
        context.fillStyle = state.colors.speech;
        for (const [start, end] of state.options.ranges) {
            const x0 = Math.max(0, start / 1000 / duration * width);
            const x1 = Math.min(width, Math.max(x0 + dpr, end / 1000 / duration * width));
            context.fillRect(x0, height - bandHeight, x1 - x0, bandHeight);
        }
    }

    // Бегунок.
    context.fillStyle = state.colors.playhead;
    context.fillRect(Math.min(width - dpr * 2, played * width), 0, Math.max(1, Math.round(2 * dpr)), waveHeight);

    if (state.time) {
        state.time.textContent = `${audioClock(position)} / ${duration > 0 ? audioClock(duration) : '--:--'}`;
    }
}

function audioSecondsAt(state, clientX) {
    const rect = state.area.getBoundingClientRect();
    const duration = audioDuration(state);
    if (!(rect.width > 0) || !(duration > 0)) {
        return 0;
    }
    return Math.min(Math.max(clientX - rect.left, 0), rect.width) / rect.width * duration;
}

function seekAudio(state, seconds) {
    const media = state.media;
    if (!media) {
        return;
    }
    try {
        media.currentTime = Math.max(0, seconds);
    } catch {
        /* метаданные записи ещё не загружены — переход не состоялся */
    }
    drawAudioPlayer(state);
}

function reportAudioState(state) {
    const media = state.media;
    if (!media || !state.dotNetRef || typeof state.dotNetRef.invokeMethodAsync !== 'function') {
        return;
    }
    const playing = !media.paused && !media.ended;
    const key = `${playing}|${media.playbackRate}|${media.muted}`;
    if (key === state.reported) {
        return;
    }
    state.reported = key;
    state.dotNetRef.invokeMethodAsync('OnPlayerState', playing, media.playbackRate, media.muted)
        .catch(() => { /* circuit уже закрыт */ });
}

function toggleAudio(state) {
    const media = state.media;
    if (!media) {
        return;
    }
    if (media.paused || media.ended) {
        const started = media.play();
        if (started && typeof started.catch === 'function') {
            started.catch(() => { /* формат не воспроизводится или нет жеста пользователя */ });
        }
    } else {
        media.pause();
    }
}

function loadAudioWaveform(state) {
    const url = state.options.waveformUrl;
    if (!url || typeof fetch !== 'function') {
        return;
    }
    const request = ++state.waveRequest;
    const hint = setTimeout(() => {
        if (state.status && request === state.waveRequest && !state.wave) {
            state.status.textContent = 'Строится волна записи…';
        }
    }, WAVEFORM_HINT_DELAY_MS);
    fetch(url, { credentials: 'same-origin', cache: 'no-store' })
        .then((response) => (response.ok ? response.json() : null))
        .then((data) => {
            if (request !== state.waveRequest || !audioPlayers.has(state.rootId)) {
                return;
            }
            if (data && Number(data.stepMs) > 0) {
                state.wave = { stepMs: Number(data.stepMs), peaks: decodePeaks(data.peaks) };
            }
            state.layout = null;
            if (state.status) {
                // Не построилась — без надписи об ошибке: проигрыватель работает, причина — в журнале сервера.
                state.status.textContent = '';
            }
            drawAudioPlayer(state);
        })
        .catch(() => {
            if (state.status && request === state.waveRequest) {
                state.status.textContent = '';
            }
        })
        .finally(() => clearTimeout(hint));
}

function listenAudio(state, target, type, handler, options) {
    target.addEventListener(type, handler, options);
    state.listeners.push(() => target.removeEventListener(type, handler, options));
}

/** Нажатие предназначено полю, списку или самой кнопке — клавиши проигрывателя его не перехватывают. */
function isOwnKeyTarget(target, key) {
    if (isTextInput(target)) {
        return true;
    }
    if (!(target instanceof Element)) {
        return false;
    }
    if (target.closest('[role="listbox"],[role="menu"],[role="slider"],[role="tablist"],[role="radiogroup"],[role="dialog"]')) {
        return true;
    }
    // Пробел на кнопке и ссылке — это их нажатие (щелчок по фрагменту расшифровки, кнопки проигрывателя).
    return key === ' ' && !!target.closest('button,a,[role="button"],[role="switch"],[role="checkbox"]');
}

/**
 * Включить проигрыватель в элементе id="rootId" (AudioPlayer). options: elementId (<audio>), waveformUrl, durationMs,
 * ranges ([[начало, конец], …] мс — участки речи), keys (клавиши). dotNetRef — компонент (OnPlayerState).
 * Повторный вызов заменяет прежнюю привязку. Возвращает false, если элемента нет.
 */
export function initAudioPlayer(rootId, options, dotNetRef) {
    disposeAudioPlayer(rootId);
    const root = document.getElementById(rootId);
    const normalized = normalizeAudioOptions(options);
    const media = normalized.elementId ? document.getElementById(normalized.elementId) : null;
    const canvas = root ? root.querySelector('[data-ap-canvas]') : null;
    const area = root ? root.querySelector('[data-ap-wave]') : null;
    if (!root || !media || !canvas || !area) {
        return false;
    }
    const state = {
        rootId,
        root,
        media,
        canvas,
        area,
        hover: root.querySelector('[data-ap-hover]'),
        time: root.querySelector('[data-ap-time]'),
        status: root.querySelector('[data-ap-status]'),
        volume: root.querySelector('[data-ap-volume]'),
        options: normalized,
        dotNetRef,
        colors: {
            played: cssColor(root, '--mud-palette-primary', '#594ae2'),
            rest: cssColor(root, '--mud-palette-lines-inputs', '#9e9e9e'),
            speech: cssColor(root, '--mud-palette-info', '#2196f3'),
            playhead: cssColor(root, '--mud-palette-primary-darken', '#3a2dbd'),
        },
        wave: null,
        waveRequest: 0,
        layout: null,
        dragging: false,
        frame: 0,
        observer: null,
        reported: '',
        listeners: [],
    };
    audioPlayers.set(rootId, state);

    // Тон голоса при ускорении сохраняется (по умолчанию так и есть; явно — для старых движков).
    try {
        media.preservesPitch = true;
    } catch {
        /* свойство не поддерживается — не страшно */
    }

    const animate = () => {
        drawAudioPlayer(state);
        state.frame = !media.paused && !media.ended ? requestAnimationFrame(animate) : 0;
    };
    const redraw = () => drawAudioPlayer(state);
    listenAudio(state, media, 'timeupdate', redraw);
    listenAudio(state, media, 'seeked', redraw);
    listenAudio(state, media, 'loadedmetadata', () => {
        state.layout = null;
        drawAudioPlayer(state);
    });
    listenAudio(state, media, 'play', () => {
        if (!state.frame) {
            state.frame = requestAnimationFrame(animate);
        }
        reportAudioState(state);
    });
    for (const type of ['pause', 'ended', 'ratechange', 'volumechange']) {
        listenAudio(state, media, type, () => {
            redraw();
            reportAudioState(state);
            if (type === 'volumechange' && state.volume) {
                state.volume.value = String(media.muted ? 0 : media.volume);
            }
        });
    }

    if (state.volume) {
        state.volume.value = String(media.muted ? 0 : media.volume);
        listenAudio(state, state.volume, 'input', () => {
            const value = Math.min(1, Math.max(0, Number(state.volume.value) || 0));
            media.volume = value;
            media.muted = value === 0;
        });
    }

    listenAudio(state, area, 'pointerdown', (event) => {
        if (event.button !== 0) {
            return;
        }
        state.dragging = true;
        if (typeof area.setPointerCapture === 'function') {
            area.setPointerCapture(event.pointerId);
        }
        seekAudio(state, audioSecondsAt(state, event.clientX));
        event.preventDefault();
    });
    listenAudio(state, area, 'pointermove', (event) => {
        const seconds = audioSecondsAt(state, event.clientX);
        if (state.hover) {
            const duration = audioDuration(state);
            state.hover.textContent = audioClock(seconds);
            state.hover.style.left = duration > 0 ? `${seconds / duration * 100}%` : '0%';
            state.hover.style.display = 'block';
        }
        if (state.dragging) {
            seekAudio(state, seconds);
        }
    });
    listenAudio(state, area, 'pointerup', (event) => {
        if (state.dragging) {
            state.dragging = false;
            seekAudio(state, audioSecondsAt(state, event.clientX));
        }
    });
    listenAudio(state, area, 'pointercancel', () => {
        state.dragging = false;
    });
    listenAudio(state, area, 'pointerleave', () => {
        if (state.hover && !state.dragging) {
            state.hover.style.display = 'none';
        }
    });

    if (normalized.keys) {
        // Клавиши — на document (фокус на проигрывателе не нужен): пробел — пуск/пауза, ←/→ — 5 секунд.
        listenAudio(state, document, 'keydown', (event) => {
            if (event.ctrlKey || event.altKey || event.metaKey || event.defaultPrevented) {
                return;
            }
            const key = event.key;
            if ((key !== ' ' && key !== 'ArrowLeft' && key !== 'ArrowRight') || isOwnKeyTarget(event.target, key)) {
                return;
            }
            if (key === ' ') {
                toggleAudio(state);
            } else {
                seekAudio(state, media.currentTime + (key === 'ArrowLeft' ? -5 : 5));
            }
            event.preventDefault();
        });
    }

    if (typeof ResizeObserver === 'function') {
        let pending = 0;
        state.observer = new ResizeObserver(() => {
            if (!pending) {
                pending = requestAnimationFrame(() => {
                    pending = 0;
                    state.layout = null;
                    drawAudioPlayer(state);
                });
            }
        });
        state.observer.observe(canvas);
    }

    drawAudioPlayer(state);
    reportAudioState(state);
    loadAudioWaveform(state);
    return true;
}

/** Обновить настройки (пришли участки речи, длительность); волна перечитывается, только если сменилась ссылка. */
export function updateAudioPlayer(rootId, options) {
    const state = audioPlayers.get(rootId);
    if (!state) {
        return false;
    }
    const next = normalizeAudioOptions(options);
    const reload = next.waveformUrl !== state.options.waveformUrl;
    state.options = { ...next, elementId: state.options.elementId, keys: state.options.keys };
    state.layout = null;
    if (reload) {
        state.wave = null;
        loadAudioWaveform(state);
    }
    drawAudioPlayer(state);
    return true;
}

/**
 * Команда проигрывателю от кнопок компонента: 'toggle' — пуск/пауза, 'skip' — сдвиг на value секунд, 'rate' —
 * скорость value, 'mute' — выключить/включить звук. Возвращает false, если проигрывателя нет.
 */
export function audioCommand(rootId, command, value) {
    const state = audioPlayers.get(rootId);
    if (!state || !state.media) {
        return false;
    }
    const media = state.media;
    switch (command) {
        case 'toggle':
            toggleAudio(state);
            break;
        case 'skip':
            seekAudio(state, media.currentTime + (Number(value) || 0));
            break;
        case 'rate': {
            const rate = Number(value);
            if (rate > 0) {
                media.playbackRate = rate;
            }
            break;
        }
        case 'mute':
            media.muted = !media.muted;
            if (!media.muted && media.volume === 0) {
                media.volume = 1;
            }
            break;
        default:
            return false;
    }
    return true;
}

/** Снять слушатели проигрывателя (dispose компонента): элемент, указатель, клавиши, наблюдатель размера, анимация. */
export function disposeAudioPlayer(rootId) {
    const state = audioPlayers.get(rootId);
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
    state.waveRequest++;
    audioPlayers.delete(rootId);
    return true;
}

// --- Расшифровка следует за записью (ADR-0047) ---
// По timeupdate/seeked проигрывателя (<audio> или <video>) находится текущий фрагмент; при его СМЕНЕ — один вызов
// OnPlaybackSegment панели TranscriptPanel (подсветка строки). Прокрутку к строке панель просит отдельно
// (revealInList) — и только внутри списка: страница от проигрывателя не уезжает. Если оператор сам прокручивал
// список последние USER_SCROLL_PAUSE_MS, прокрутка его не перебивает.
const transcriptFollowers = new Map();
const listScrollMarks = new Map();
const USER_SCROLL_PAUSE_MS = 4000;

/** Текущий фрагмент: содержащий момент, иначе последний начавшийся раньше; до первого — −1 (как FindSegment панели). */
function segmentAt(segments, ms) {
    let low = 0;
    let high = segments.length - 1;
    let found = -1;
    while (low <= high) {
        const middle = (low + high) >> 1;
        if (segments[middle][1] <= ms) {
            found = middle;
            low = middle + 1;
        } else {
            high = middle - 1;
        }
    }
    return found < 0 ? -1 : segments[found][0];
}

function markUserScroll(listId) {
    const list = document.getElementById(listId);
    if (!list || listScrollMarks.has(listId)) {
        return;
    }
    const mark = () => listScrollMarks.set(listId, { list, at: Date.now(), handlers });
    const onPointer = (event) => {
        // Перетаскивание полосы прокрутки самого списка (щелчок по строке — это не прокрутка).
        if (event.target === list) {
            mark();
        }
    };
    const handlers = [['wheel', mark], ['touchmove', mark], ['pointerdown', onPointer]];
    for (const [type, handler] of handlers) {
        list.addEventListener(type, handler, { passive: true });
    }
    listScrollMarks.set(listId, { list, at: 0, handlers });
}

/**
 * Следить за проигрывателем id="playerId": segments — [[номер, начало мс, конец мс], …] (порядок любой),
 * listId — прокручиваемый список фрагментов (для паузы после ручной прокрутки). Повторный вызов заменяет привязку.
 */
export function followTranscript(playerId, listId, segments, dotNetRef) {
    unfollowTranscript(playerId);
    const media = document.getElementById(playerId);
    if (!media || !Array.isArray(segments)) {
        return false;
    }
    const sorted = segments
        .filter((s) => Array.isArray(s) && s.length >= 3)
        .map((s) => [Number(s[0]), Number(s[1]) || 0, Number(s[2]) || 0])
        .sort((a, b) => a[1] - b[1]);
    const starts = sorted.map((s) => [s[0], s[1]]);
    let current = null;
    const handler = () => {
        // Пауза между фрагментами — подсвечивается последний начавшийся (как FindSegment панели).
        const index = segmentAt(starts, media.currentTime * 1000);
        if (index !== current) {
            current = index;
            if (dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function') {
                dotNetRef.invokeMethodAsync('OnPlaybackSegment', index).catch(() => { /* circuit уже закрыт */ });
            }
        }
    };
    media.addEventListener('timeupdate', handler);
    media.addEventListener('seeked', handler);
    if (listId) {
        markUserScroll(listId);
    }
    transcriptFollowers.set(playerId, { media, handler, listId });
    return true;
}

/** Снять слежение за проигрывателем (dispose панели или новая расшифровка). */
export function unfollowTranscript(playerId) {
    const follower = transcriptFollowers.get(playerId);
    if (!follower) {
        return false;
    }
    follower.media.removeEventListener('timeupdate', follower.handler);
    follower.media.removeEventListener('seeked', follower.handler);
    if (follower.listId) {
        const mark = listScrollMarks.get(follower.listId);
        if (mark) {
            for (const [type, handler] of mark.handlers) {
                mark.list.removeEventListener(type, handler);
            }
            listScrollMarks.delete(follower.listId);
        }
    }
    transcriptFollowers.delete(playerId);
    return true;
}

/**
 * Прокрутить СПИСОК id="listId" так, чтобы элемент id="elementId" был виден (на треть высоты от верха); страница не
 * прокручивается. force = false — не перебивать ручную прокрутку последних секунд; уже видимый элемент не двигается.
 */
export function revealInList(elementId, listId, force) {
    const element = document.getElementById(elementId);
    const list = document.getElementById(listId);
    if (!element || !list) {
        return false;
    }
    const mark = listScrollMarks.get(listId);
    if (!force && mark && Date.now() - mark.at < USER_SCROLL_PAUSE_MS) {
        return false;
    }
    const listRect = list.getBoundingClientRect();
    const rect = element.getBoundingClientRect();
    if (rect.top >= listRect.top && rect.bottom <= listRect.bottom) {
        return true;
    }
    const target = list.scrollTop + (rect.top - listRect.top) - list.clientHeight / 3;
    try {
        list.scrollTo({ top: Math.max(0, target), behavior: 'smooth' });
    } catch {
        list.scrollTop = Math.max(0, target);
    }
    return true;
}

/**
 * Скопировать текст в буфер обмена (выгрузка расшифровки, ADR-0047). В изолированном контуре страница часто открыта
 * по http (небезопасный контекст) — там Clipboard API нет, и копируется через скрытое поле и команду copy.
 * Возвращает true, если браузер принял текст.
 */
export async function copyText(text) {
    const value = String(text ?? '');
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(value);
            return true;
        }
    } catch {
        /* нет разрешения — пробуем через скрытое поле */
    }
    try {
        const area = document.createElement('textarea');
        area.value = value;
        area.setAttribute('readonly', '');
        area.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0;';
        document.body.appendChild(area);
        area.select();
        const copied = document.execCommand('copy');
        area.remove();
        return copied;
    } catch {
        return false;
    }
}

// --- Обработка фото: предпросмотр (ТФ-МЕД-14, ADR-0050) ---
// Предпросмотр строится в браузере из оригинала теми же операциями и формулами, что и копия на сервере (ImageEditing):
// поворот → кадрирование → тон (яркость, контраст, гамма — таблица на канал) → резкость (3×3). Картинка уменьшена под
// экран, поэтому резкость на ней выглядит чуть иначе, чем на полном размере. Ничего не сохраняет: копию строит сервер
// по набору операций (CreateEditedCopyCommand). В режиме кадрирования показывается всё изображение и выделение мышью;
// выделенная область уходит компоненту (OnCropSelected) в координатах изображения после поворота.
const imageEditors = new Map();

function editToneTable(brightness, contrast, gamma) {
    const table = new Uint8ClampedArray(256);
    const c = contrast * 2.55;
    const factor = (259 * (c + 255)) / (255 * (259 - c));
    for (let i = 0; i < 256; i++) {
        let v = i + brightness * 2.55;
        v = factor * (v - 128) + 128;
        v = Math.min(255, Math.max(0, v));
        v = 255 * Math.pow(v / 255, 1 / gamma);
        table[i] = Math.round(Math.min(255, Math.max(0, v)));
    }
    return table;
}

function editSharpen(data, width, height, k) {
    const source = new Uint8ClampedArray(data);
    const center = 1 + 4 * k;
    for (let y = 0; y < height; y++) {
        const up = Math.max(0, y - 1) * width * 4;
        const row = y * width * 4;
        const down = Math.min(height - 1, y + 1) * width * 4;
        for (let x = 0; x < width; x++) {
            const left = Math.max(0, x - 1) * 4;
            const self = x * 4;
            const right = Math.min(width - 1, x + 1) * 4;
            for (let ch = 0; ch < 3; ch++) {
                data[row + self + ch] = center * source[row + self + ch]
                    - k * (source[up + self + ch] + source[down + self + ch] + source[row + left + ch] + source[row + right + ch]);
            }
        }
    }
}

/** Изображение, повёрнутое на rotation° по часовой (холст; повтор с тем же поворотом — из кэша). */
function editRotated(state) {
    const rotation = state.ops.rotation || 0;
    if (state.rotated && state.rotatedFor === rotation) {
        return state.rotated;
    }
    const image = state.image;
    const swap = rotation === 90 || rotation === 270;
    const canvas = document.createElement('canvas');
    canvas.width = swap ? image.naturalHeight : image.naturalWidth;
    canvas.height = swap ? image.naturalWidth : image.naturalHeight;
    const context = canvas.getContext('2d');
    context.translate(canvas.width / 2, canvas.height / 2);
    context.rotate(rotation * Math.PI / 180);
    context.drawImage(image, -image.naturalWidth / 2, -image.naturalHeight / 2);
    state.rotated = canvas;
    state.rotatedFor = rotation;
    return canvas;
}

function renderImageEditor(state) {
    if (!state.image) {
        return;
    }
    const rotated = editRotated(state);
    const crop = state.ops.crop;
    const showAll = state.cropMode || !crop;
    const region = showAll ? { x: 0, y: 0, width: rotated.width, height: rotated.height } : crop;
    const maxWidth = Math.max(200, Math.min(1400, state.root.clientWidth || 800));
    const maxHeight = Math.max(200, Math.round(window.innerHeight * 0.65));
    const scale = Math.min(1, maxWidth / region.width, maxHeight / region.height);
    const width = Math.max(1, Math.round(region.width * scale));
    const height = Math.max(1, Math.round(region.height * scale));
    const canvas = state.canvas;
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext('2d', { willReadFrequently: true });
    context.imageSmoothingEnabled = true;
    context.imageSmoothingQuality = 'high';
    context.drawImage(rotated, region.x, region.y, region.width, region.height, 0, 0, width, height);
    state.view = { region, scale };

    const ops = state.ops;
    const toned = ops.brightness !== 0 || ops.contrast !== 0 || Math.abs(ops.gamma - 1) >= 0.005;
    if (!state.cropMode && (toned || ops.sharpen > 0)) {
        const pixels = context.getImageData(0, 0, width, height);
        if (toned) {
            const table = editToneTable(ops.brightness, ops.contrast, ops.gamma);
            const data = pixels.data;
            for (let i = 0; i < data.length; i += 4) {
                data[i] = table[data[i]];
                data[i + 1] = table[data[i + 1]];
                data[i + 2] = table[data[i + 2]];
            }
        }
        if (ops.sharpen > 0) {
            editSharpen(pixels.data, width, height, ops.sharpen / 100);
        }
        context.putImageData(pixels, 0, 0);
    }

    // Режим кадрирования: затемнение вне выделения и рамка.
    const selection = state.dragCrop || (state.cropMode ? crop : null);
    if (state.cropMode && selection) {
        const sx = selection.x * scale;
        const sy = selection.y * scale;
        const sw = selection.width * scale;
        const sh = selection.height * scale;
        context.fillStyle = 'rgba(0, 0, 0, 0.55)';
        context.fillRect(0, 0, width, sy);
        context.fillRect(0, sy + sh, width, height - sy - sh);
        context.fillRect(0, sy, sx, sh);
        context.fillRect(sx + sw, sy, width - sx - sw, sh);
        context.strokeStyle = '#ffeb3b';
        context.lineWidth = 2;
        context.strokeRect(sx + 1, sy + 1, Math.max(0, sw - 2), Math.max(0, sh - 2));
    }
}

function editPointToImage(state, event) {
    const rect = state.canvas.getBoundingClientRect();
    const { region, scale } = state.view;
    const x = (event.clientX - rect.left) * (state.canvas.width / rect.width) / scale + region.x;
    const y = (event.clientY - rect.top) * (state.canvas.height / rect.height) / scale + region.y;
    const rotated = editRotated(state);
    return { x: Math.min(rotated.width, Math.max(0, x)), y: Math.min(rotated.height, Math.max(0, y)) };
}

function editRect(a, b) {
    const x = Math.floor(Math.min(a.x, b.x));
    const y = Math.floor(Math.min(a.y, b.y));
    return { x, y, width: Math.max(1, Math.ceil(Math.max(a.x, b.x)) - x), height: Math.max(1, Math.ceil(Math.max(a.y, b.y)) - y) };
}

/**
 * Включить предпросмотр обработки в элементе id="rootId": imageUrl — оригинал (эндпоинт раздачи с проверкой допуска),
 * ops — набор операций, dotNetRef — компонент (OnImageLoaded с размером после EXIF, OnCropSelected).
 */
export function initImageEditor(rootId, imageUrl, ops, dotNetRef) {
    disposeImageEditor(rootId);
    const root = document.getElementById(rootId);
    const canvas = root ? root.querySelector('[data-ie-canvas]') : null;
    if (!root || !canvas) {
        return false;
    }
    const state = { root, canvas, ops: normalizeEditOps(ops), cropMode: false, image: null, rotated: null, rotatedFor: null,
        view: null, dragStart: null, dragCrop: null, dotNetRef, listeners: [] };
    imageEditors.set(rootId, state);

    const image = new Image();
    image.onload = () => {
        if (!imageEditors.has(rootId)) {
            return;
        }
        state.image = image;
        renderImageEditor(state);
        if (dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function') {
            dotNetRef.invokeMethodAsync('OnImageLoaded', image.naturalWidth, image.naturalHeight).catch(() => { /* circuit закрыт */ });
        }
    };
    image.src = imageUrl;

    const on = (target, type, handler) => {
        target.addEventListener(type, handler);
        state.listeners.push(() => target.removeEventListener(type, handler));
    };
    on(canvas, 'pointerdown', (event) => {
        if (!state.cropMode || !state.image || event.button !== 0) {
            return;
        }
        state.dragStart = editPointToImage(state, event);
        state.dragCrop = null;
        if (typeof canvas.setPointerCapture === 'function') {
            canvas.setPointerCapture(event.pointerId);
        }
        event.preventDefault();
    });
    on(canvas, 'pointermove', (event) => {
        if (state.dragStart) {
            state.dragCrop = editRect(state.dragStart, editPointToImage(state, event));
            renderImageEditor(state);
        }
    });
    on(canvas, 'pointerup', (event) => {
        if (!state.dragStart) {
            return;
        }
        const crop = editRect(state.dragStart, editPointToImage(state, event));
        state.dragStart = null;
        state.dragCrop = null;
        if (crop.width >= 8 && crop.height >= 8 && dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function') {
            dotNetRef.invokeMethodAsync('OnCropSelected', crop.x, crop.y, crop.width, crop.height).catch(() => { /* circuit закрыт */ });
        } else {
            renderImageEditor(state);
        }
    });
    if (typeof ResizeObserver === 'function') {
        state.observer = new ResizeObserver(() => renderImageEditor(state));
        state.observer.observe(root);
    }
    return true;
}

function normalizeEditOps(ops) {
    const o = ops || {};
    const crop = o.crop && o.crop.width > 0 && o.crop.height > 0
        ? { x: Number(o.crop.x) || 0, y: Number(o.crop.y) || 0, width: Number(o.crop.width), height: Number(o.crop.height) }
        : null;
    return {
        brightness: Number(o.brightness) || 0,
        contrast: Number(o.contrast) || 0,
        gamma: Number(o.gamma) > 0 ? Number(o.gamma) : 1,
        sharpen: Number(o.sharpen) || 0,
        rotation: Number(o.rotation) || 0,
        crop,
        zoom: Number(o.zoom) || 1,
    };
}

/** Новый набор операций и режим кадрирования — перерисовать предпросмотр. */
export function updateImageEditor(rootId, ops, cropMode) {
    const state = imageEditors.get(rootId);
    if (!state) {
        return false;
    }
    state.ops = normalizeEditOps(ops);
    state.cropMode = cropMode === true;
    renderImageEditor(state);
    return true;
}

/** Снять слушатели предпросмотра. */
export function disposeImageEditor(rootId) {
    const state = imageEditors.get(rootId);
    if (!state) {
        return false;
    }
    for (const remove of state.listeners) {
        remove();
    }
    if (state.observer) {
        state.observer.disconnect();
    }
    imageEditors.delete(rootId);
    return true;
}

// --- Пометки на фото: рисование (ТФ-МЕД-13, ADR-0050) ---
// Сохранённые пометки рисует Blazor (svg карточки). Здесь — только рисование новой: слой id="svgId" без детей в разметке
// Blazor, поверх фото, в пикселях изображения (viewBox — натуральный размер). Пока протягивают мышью, видна заготовка;
// по отпусканию компонент получает вид и две точки (OnAnnotationDrawn), подпись — по щелчку в точке.
const annotationLayers = new Map();

function annotationPoint(svg, event) {
    const matrix = svg.getScreenCTM();
    if (!matrix) {
        return { x: 0, y: 0 };
    }
    const point = svg.createSVGPoint();
    point.x = event.clientX;
    point.y = event.clientY;
    const local = point.matrixTransform(matrix.inverse());
    return { x: Math.max(0, local.x), y: Math.max(0, local.y) };
}

/** Включить рисование пометок на слое id="svgId"; dotNetRef — карточка (OnAnnotationDrawn). */
export function bindAnnotationLayer(svgId, dotNetRef) {
    unbindAnnotationLayer(svgId);
    const svg = document.getElementById(svgId);
    if (!svg) {
        return false;
    }
    const state = { svg, dotNetRef, tool: null, color: '#E53935', start: null, preview: null, listeners: [] };
    annotationLayers.set(svgId, state);
    const on = (type, handler) => {
        svg.addEventListener(type, handler);
        state.listeners.push(() => svg.removeEventListener(type, handler));
    };
    const report = (tool, a, b) => {
        if (state.dotNetRef && typeof state.dotNetRef.invokeMethodAsync === 'function') {
            state.dotNetRef.invokeMethodAsync('OnAnnotationDrawn', tool, a.x, a.y, b.x, b.y).catch(() => { /* circuit закрыт */ });
        }
    };
    on('pointerdown', (event) => {
        if (!state.tool || event.button !== 0) {
            return;
        }
        const point = annotationPoint(svg, event);
        event.preventDefault();
        if (state.tool === 'text') {
            report('text', point, point);
            return;
        }
        state.start = point;
        const ns = 'http://www.w3.org/2000/svg';
        state.preview = document.createElementNS(ns, state.tool === 'rect' ? 'rect' : 'line');
        state.preview.setAttribute('stroke', state.color);
        state.preview.setAttribute('stroke-width', '3');
        state.preview.setAttribute('fill', 'none');
        state.preview.setAttribute('vector-effect', 'non-scaling-stroke');
        svg.appendChild(state.preview);
        if (typeof svg.setPointerCapture === 'function') {
            svg.setPointerCapture(event.pointerId);
        }
    });
    on('pointermove', (event) => {
        if (!state.start || !state.preview) {
            return;
        }
        const point = annotationPoint(svg, event);
        if (state.tool === 'rect') {
            state.preview.setAttribute('x', String(Math.min(state.start.x, point.x)));
            state.preview.setAttribute('y', String(Math.min(state.start.y, point.y)));
            state.preview.setAttribute('width', String(Math.abs(point.x - state.start.x)));
            state.preview.setAttribute('height', String(Math.abs(point.y - state.start.y)));
        } else {
            state.preview.setAttribute('x1', String(state.start.x));
            state.preview.setAttribute('y1', String(state.start.y));
            state.preview.setAttribute('x2', String(point.x));
            state.preview.setAttribute('y2', String(point.y));
        }
    });
    on('pointerup', (event) => {
        if (!state.start) {
            return;
        }
        const point = annotationPoint(svg, event);
        const start = state.start;
        state.start = null;
        if (state.preview) {
            state.preview.remove();
            state.preview = null;
        }
        report(state.tool === 'rect' ? 'rect' : 'arrow', start, point);
    });
    return true;
}

/** Инструмент рисования: 'arrow', 'rect', 'text' или null (рисование выключено) и цвет заготовки. */
export function setAnnotationTool(svgId, tool, color) {
    const state = annotationLayers.get(svgId);
    if (!state) {
        return false;
    }
    state.tool = tool || null;
    state.color = color || state.color;
    return true;
}

/** Снять слушатели слоя пометок. */
export function unbindAnnotationLayer(svgId) {
    const state = annotationLayers.get(svgId);
    if (!state) {
        return false;
    }
    for (const remove of state.listeners) {
        remove();
    }
    if (state.preview) {
        state.preview.remove();
    }
    annotationLayers.delete(svgId);
    return true;
}

// Глобальный алиас для отладки из консоли; страницы используют экспорт модуля.
window.iscaiMedia = { seek, reveal, measure, currentTime, stepFrame, bindFrameKeys, unbindFrameKeys };
