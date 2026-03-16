// FEED_ID, IS_DEV, PROGRESS_SMOOTHING are set as globals by the HTML page
const SHOW_GHOST = IS_DEV && window.location.search.includes('ghost');
const DEBUG_TITLE_ANIMATION = IS_DEV && window.location.search.includes('alive');
const VELOCITY_OVERRIDES = IS_DEV ? parseVelocityOverrides() : {};
/**
 * @typedef {Object} Episode
 * @property {string} id - Episode ID
 * @property {string} title - Episode title
 * @property {string} fileName - Audio file name
 * @property {number} [fileSize] - File size in bytes
 * @property {string} [duration] - Duration string (e.g. "1:23:45")
 * @property {string} [publishedDate] - ISO date string
 * @property {string} [uploadedAt] - ISO date string
 */

/**
 * Parse velocity override URL params (dev only).
 * @returns {{Uploading?: number, Analyzing?: number, Normalizing?: number}}
 */
function parseVelocityOverrides() {
    const params = new URLSearchParams(window.location.search);
    const overrides = {};
    const mapping = { vup: 'Uploading', vanal: 'Analyzing', vnorm: 'Normalizing' };
    for (const [param, stage] of Object.entries(mapping)) {
        const value = params.get(param);
        if (value != null) {
            const parsed = parseFloat(value);
            if (!isNaN(parsed)) {
                overrides[stage] = parsed * 1024; // kB/s to bytes/s
            }
        }
    }

    return overrides;
}
/**
 * @typedef {Object} QueueEntry
 * @property {string} id - Unique entry ID
 * @property {File|null} file - File object (null after session restore)
 * @property {'queued'|'uploading'|'normalizing'|'completed'|'failed'|'cancelled'} status
 * @property {number} progress - Progress percentage (0-100)
 * @property {string|null} stage - Normalization stage (Queued, Analyzing, Normalizing, Finishing, Completed)
 * @property {string|null} jobId - Server normalization job ID
 * @property {string|null} episodeId - Episode ID after completion
 * @property {Episode|null} episode - Full episode data after completion
 * @property {string|null} error - Error message if failed
 * @property {XMLHttpRequest|null} xhr - Active XHR for upload cancellation
 * @property {EventSource|null} eventSource - Active SSE connection for normalization
 * @property {number} fileSize - File size in bytes
 * @property {string} fileName - Original file name
 * @property {boolean} validationError - Whether failure is due to validation (no retry)
 * @property {boolean} backgroundMonitoring - Whether normalization is being monitored in the background
 * @property {number} startedAt - Epoch ms when entry was created (for 1-hour localStorage filtering)
 * @property {Function|null} _resolveMonitor - Internal: resolve function for normalization promise
 */

let apiKey = null;
const states = ['no-key', 'ready', 'queue', 'error'];
const QUEUE_STORAGE_KEY = 'featherpod_queue_' + FEED_ID;
const HISTORY_STORAGE_KEY = 'featherpod_history_' + FEED_ID;
const HISTORY_FILTER_KEY = 'featherpod_history_filter_' + FEED_ID;
const API_KEY_SESSION_KEY = 'featherpod_api_key_' + FEED_ID;
const API_KEY_LOCAL_KEY = 'featherpod_api_key_local_' + FEED_ID;
const API_KEY_COOKIE_KEY = 'featherpod_key_' + FEED_ID;
const MAX_LOCAL_HISTORY = 50;

// No-key state UI strings
const STR_PASTE_KEY_BELOW = 'Paste key below';
const STR_PASTE_KEY = 'Paste here';
const STR_SAVE_KEY = 'Save key';
const STR_API_KEY_REQUIRED = 'API key required';
const STR_INVALID_KEY = 'Invalid key';
const STR_NO_ACCESS = 'No access';
const STR_NO_FEED_ACCESS = 'This key does not have access to this feed';

/** @type {{host: string, token: string}|null} - Local file server connection info (from URL fragment or sessionStorage on reload) */
let localSourceConfig = null;
/** @type {EventSource|null} - SSE connection to local file server for new-file events */
let localSourceEvents = null;
/** @type {Set<number>} - Local file server indices already fetched (persisted in sessionStorage to survive reloads) */
let localSourceSeen = new Set();
/** @type {Set<string>} - Job IDs dismissed by the user (guards against mergeServerJobs re-adding before server cancel completes) */
let dismissedJobIds = new Set();

/** @type {EventSource|null} - SSE connection for feed-level events (cross-tab sync) */
let feedEventsSource = null;

/** @type {Array<QueueEntry>} - Upload queue entries */
let uploadQueue = [];
/** @type {string|null} - ID of the entry currently uploading */
let activeUploadId = null;
/** @type {boolean} - Whether an upload is currently in progress */
let isUploading = false;
/** @type {number} - Counter for generating unique entry IDs */
let nextEntryId = 0;

/** @type {Array<Object>|null} - History data for ready state */
let historyData = null;
/** @type {string|null} - Selected upload ID in history */
let historySelectedId = null;
/** @type {'local'|'browser'|'all'} - Current history filter */
let historyFilter = 'local';
/** @type {boolean} - Whether a history.pushState entry exists for the open history panel */
let historyPanelPushedState = false;
/** @type {number} - Counter for tracking pending filter requests (prevents race conditions) */
let pendingFilterRequest = 0;

/** @type {Array<Object>|null} - Cached browser uploads from server */
let cachedBrowserUploads = null;
/** @type {Array<Object>|null} - Cached all uploads from server */
let cachedAllUploads = null;

/** @type {number|null} - Animation ID for title text animation */
let titleAnimationId = null;
/** @type {string} - Current first word of title (for animation) */
let currentTitleText = 'Push';
/** @type {boolean} - Skip animation on first showState call (page load) */
let isFirstStateChange = true;

// Title animation timing (ms)
const TITLE_ANIMATION_CHAR_DELAY = 150;
const TITLE_ANIMATION_LOAD_DELAY = 600;
const TITLE_ANIMATION_PAUSE_DELAY = 300;

/**
 * Animate the first word of the page title character by character.
 * Removes characters to find common prefix, then adds characters to reach target.
 * The suffix " to Feed" remains static. Timing controlled by TITLE_ANIMATION_* constants.
 * @param {string} targetWord - The target first word (e.g., "Push", "Pushing", "Pushed")
 */
function animateTitle(targetWord) {
    const titleEl = document.getElementById('page-title');
    if (!titleEl) {
        return;
    }

    const suffix = ' to Feed';

    // Cancel any in-progress animation
    if (titleAnimationId != null) {
        clearTimeout(titleAnimationId);
        titleAnimationId = null;
    }

    // Find common prefix length
    let commonLength = 0;
    while (commonLength < currentTitleText.length &&
           commonLength < targetWord.length &&
           currentTitleText[commonLength] === targetWord[commonLength]) {
        commonLength++;
    }

    // Build animation steps: remove chars down to common prefix, pause, then add chars to target
    const steps = [];

    // Remove characters (from current down to common prefix)
    for (let i = currentTitleText.length; i > commonLength; i--) {
        steps.push({ text: currentTitleText.slice(0, i - 1), pause: false });
    }

    // Add a pause after erasing (if we erased anything and have chars to add)
    const hasErased = currentTitleText.length > commonLength;
    const hasToAdd = targetWord.length > commonLength;
    if (hasErased && hasToAdd) {
        steps.push({ text: currentTitleText.slice(0, commonLength), pause: true });
    }

    // Add characters (from common prefix up to target)
    for (let i = commonLength + 1; i <= targetWord.length; i++) {
        steps.push({ text: targetWord.slice(0, i), pause: false });
    }

    let stepIndex = 0;

    function nextStep() {
        if (stepIndex >= steps.length) {
            titleAnimationId = null;

            return;
        }

        const step = steps[stepIndex];
        titleEl.textContent = step.text + suffix;
        currentTitleText = step.text; // Update immediately so interrupts work correctly
        stepIndex++;
        const delay = step.pause ? TITLE_ANIMATION_PAUSE_DELAY : TITLE_ANIMATION_CHAR_DELAY;
        titleAnimationId = setTimeout(nextStep, delay);
    }

    if (steps.length > 0) {
        nextStep();
    }
}

/**
 * Immediately collapse the history section without animation.
 * Strips all animation classes, clears inline styles, resets aria and toggle text.
 * Pops the pushed browser history entry if one exists (so back button navigates normally).
 * Safe to call even if history is already collapsed or not yet initialized.
 */
function collapseHistoryImmediate() {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    const frostedOverlay = document.getElementById('frosted-overlay');
    const selectFileBtn = document.getElementById('select-file');

    if (section) {
        section.classList.remove('history-section--expanded', 'history-section--settled', 'history-section--collapsing', 'history-section--fade-out');
        section.style.height = '';
        section.style.width = '';
        section.style.marginLeft = '';
        section.style.marginRight = '';
    }

    if (toggle) {
        toggle.setAttribute('aria-expanded', 'false');
        const textSpan = toggle.querySelector('.history-toggle-text');
        if (textSpan) {
            textSpan.textContent = 'History';
        }
        toggle.classList.remove('text-fading');
        toggle.style.width = '';
    }

    if (frostedOverlay) {
        frostedOverlay.classList.remove('frosted-overlay--active');
    }

    // Re-enable select-file button (may have been disabled when history was expanded in ready state)
    if (selectFileBtn) {
        selectFileBtn.disabled = false;
    }

    // Pop the pushed history entry if one exists
    if (historyPanelPushedState) {
        historyPanelPushedState = false;
        history.back();
    }
}

/** @param {string} stateName */
function showState(stateName) {
    // Collapse history immediately before switching states to prevent stale expanded state
    collapseHistoryImmediate();

    states.forEach(s => document.getElementById(s).style.display = s === stateName ? '' : 'none');

    // Update container state class for CSS styling
    const container = document.querySelector('.container');
    if (container) {
        states.forEach(s => container.classList.remove('state-' + s));
        container.classList.add('state-' + stateName);
    }

    // Update page title based on state (animate first word only)
    let targetWord;
    if (stateName === 'queue') {
        const hasActive = uploadQueue.some(e =>
            e.status === 'queued' || e.status === 'uploading' || e.status === 'normalizing'
        );
        targetWord = hasActive ? 'Pushing' : 'Pushed';
    } else {
        targetWord = 'Push';
    }

    // Debug mode: set starting word before comparison so animation triggers
    if (isFirstStateChange && DEBUG_TITLE_ANIMATION) {
        let startWord;
        if (stateName === 'queue') {
            startWord = 'Push';
        } else if (stateName === 'error') {
            startWord = 'Pushing';
        } else {
            startWord = 'Pushed';
        }
        currentTitleText = startWord;
        const titleEl = document.getElementById('page-title');
        if (titleEl) {
            titleEl.textContent = startWord + ' to Feed';
        }
    }

    if (targetWord !== currentTitleText) {
        if (isFirstStateChange && !DEBUG_TITLE_ANIMATION) {
            // On page load, set title immediately without animation
            const titleEl = document.getElementById('page-title');
            if (titleEl) {
                titleEl.textContent = targetWord + ' to Feed';
            }
            currentTitleText = targetWord;
        } else {
            // Delay to let h1/h2 position transition complete before animating
            setTimeout(() => animateTitle(targetWord), TITLE_ANIMATION_LOAD_DELAY);
        }
    }
    isFirstStateChange = false;
}

/**
 * Check if a file is a valid audio file by MIME type.
 * Accepts any file with an audio/* MIME type, or files with no type set
 * (some browsers don't report MIME types for less common audio formats).
 * @param {File} file
 * @returns {boolean}
 */
function isValidAudioFile(file) {
    if (!file.type) {
        return true;
    }

    return file.type.startsWith('audio/');
}

/**
 * Generate a unique entry ID for queue items.
 * @returns {string}
 */
function generateEntryId() {
    return 'q' + (nextEntryId++) + '_' + Date.now().toString(36);
}

/**
 * Get the currently visible state name.
 * @returns {string|null}
 */
function getCurrentState() {
    return states.find(s => {
        const el = document.getElementById(s);

        return el && el.style.display !== 'none';
    }) || null;
}

/**
 * Update the queue title to "Pushing" or "Pushed" based on active work.
 * No-op if not currently in queue state.
 */
function updateQueueTitle() {
    if (getCurrentState() !== 'queue') {
        return;
    }
    const hasActive = uploadQueue.some(e =>
        e.status === 'queued' || e.status === 'uploading' || e.status === 'normalizing'
    );
    animateTitle(hasActive ? 'Pushing' : 'Pushed');
}

/** @type {number} - Cached container width for consistent animations */
let cachedContainerWidth = 0;
/** @type {number} - Cached collapsed margin for consistent animations */
let cachedCollapsedMargin = 0;
/** @type {number} - Width of the collapsed drop zone / history section */
const COLLAPSED_WIDTH = 500;
/** @type {number} - Default height of the collapsed drop zone / history section */
const COLLAPSED_HEIGHT_DEFAULT = 280;

/**
 * Get the CSS-driven drop zone height (ignoring any inline overrides).
 * Temporarily clears inline height to read the computed value, or falls back
 * to computing from the aspect-ratio when the element is hidden.
 * @returns {number} Height in pixels
 */
function getCollapsedHeight() {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) {

        return COLLAPSED_HEIGHT_DEFAULT;
    }

    // Temporarily clear inline height to read CSS-driven value
    const savedHeight = dropZone.style.height;
    dropZone.style.height = '';
    const measured = dropZone.offsetHeight;
    dropZone.style.height = savedHeight;

    if (measured > 0) {

        return measured;
    }

    // Element hidden (display: none) — compute from CSS aspect-ratio
    if (dropZone.classList.contains('drop-zone--has-artwork')) {

        return COLLAPSED_WIDTH;
    }

    return COLLAPSED_HEIGHT_DEFAULT;
}

/**
 * Calculate and cache layout dimensions used for history panel animations.
 * Call this once after the page renders to get consistent values.
 */
function cacheLayoutDimensions() {
    const container = document.querySelector('.container');
    // Use clientWidth to get content area (excludes padding), matching CSS 100%
    cachedContainerWidth = container?.clientWidth || 800;
    cachedCollapsedMargin = Math.max(0, (cachedContainerWidth - COLLAPSED_WIDTH) / 2);

    // Set CSS custom properties so CSS uses the same values
    document.documentElement.style.setProperty('--history-container-width', cachedContainerWidth + 'px');
    document.documentElement.style.setProperty('--history-collapsed-margin', cachedCollapsedMargin + 'px');
}

/**
 * @typedef {Object} ApiKeyValidationResult
 * @property {boolean} valid - Whether the API key is valid
 * @property {UserInfo|null} user - User object from /api/users/me if valid
 * @property {boolean} feedAccess - Whether the user has access to this feed
 * @property {string|null} error - Error message if validation failed
 * @property {boolean} networkError - Whether the failure was due to a network/server error (vs invalid key)
 */

/**
 * @typedef {Object} UserInfo
 * @property {string} id - User ID
 * @property {string} role - User role ('Admin' or 'FeedOwner')
 * @property {string[]} ownedFeeds - Array of feed IDs the user owns (for FeedOwner role)
 */

/**
 * Validate an API key by calling /api/users/me.
 * Checks both key validity and feed access (Admin has all, FeedOwner needs feed in ownedFeeds).
 * @param {string} key - The API key to validate
 * @returns {Promise<ApiKeyValidationResult>}
 */
async function validateApiKey(key) {
    if (!key || key.trim().length === 0) {
        return { valid: false, user: null, feedAccess: false, error: 'API key is empty', networkError: false };
    }

    try {
        const response = await fetch('/api/users/me', {
            headers: { 'X-API-Key': key.trim() }
        });

        if (!response.ok) {
            if (response.status === 401) {
                return { valid: false, user: null, feedAccess: false, error: STR_INVALID_KEY, networkError: false };
            }

            return { valid: false, user: null, feedAccess: false, error: 'Server error (' + response.status + ')', networkError: false };
        }

        const user = await response.json();

        // Check feed access: Admin has all, FeedOwner needs feed in ownedFeeds
        const feedAccess = user.role === 'Admin' || (user.role === 'FeedOwner' && user.ownedFeeds && user.ownedFeeds.includes(FEED_ID));

        return { valid: true, user, feedAccess, error: null, networkError: false };
    } catch (err) {
        return { valid: false, user: null, feedAccess: false, error: 'Network error', networkError: true };
    }
}

/**
 * Set a cookie with the given name, value, and max-age in days.
 * Uses SameSite=Strict and Secure (when on HTTPS) for security.
 * @param {string} name - Cookie name
 * @param {string} value - Cookie value
 * @param {number} days - Max age in days
 */
function setCookie(name, value, days) {
    const maxAge = days * 24 * 60 * 60;
    const secure = window.location.protocol === 'https:' ? '; Secure' : '';
    document.cookie = name + '=' + encodeURIComponent(value) + '; max-age=' + maxAge + '; path=/' + FEED_ID + '/push; SameSite=Strict' + secure;
}

/**
 * Get a cookie value by name.
 * @param {string} name - Cookie name
 * @returns {string|null} The cookie value, or null if not found
 */
function getCookie(name) {
    const match = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '=([^;]*)'));
    return match ? decodeURIComponent(match[1]) : null;
}

/**
 * Delete a cookie by name.
 * @param {string} name - Cookie name
 */
function deleteCookie(name) {
    document.cookie = name + '=; max-age=0; path=/' + FEED_ID + '/push; SameSite=Strict';
}

/**
 * Save API key to sessionStorage, localStorage, and a cookie backup.
 * All writes are guarded with try/catch — the current session always works
 * even if all persistent storage fails.
 * @param {string} key - The API key to save
 */
function saveApiKey(key) {
    const trimmedKey = key.trim();
    apiKey = trimmedKey;
    try {
        sessionStorage.setItem(API_KEY_SESSION_KEY, trimmedKey);
    } catch (e) {
        // sessionStorage unavailable (private browsing, storage full)
    }
    try {
        localStorage.setItem(API_KEY_LOCAL_KEY, trimmedKey);
    } catch (e) {
        // localStorage unavailable
    }
    try {
        setCookie(API_KEY_COOKIE_KEY, trimmedKey, 365);
    } catch (e) {
        // Cookie write failed
    }
}

/**
 * Clear API key from all storage layers (sessionStorage, localStorage, cookie).
 */
function clearApiKey() {
    apiKey = null;
    try { sessionStorage.removeItem(API_KEY_SESSION_KEY); } catch (e) { /* ignore */ }
    try { localStorage.removeItem(API_KEY_LOCAL_KEY); } catch (e) { /* ignore */ }
    try { deleteCookie(API_KEY_COOKIE_KEY); } catch (e) { /* ignore */ }
}

/**
 * Get stored API key with precedence: sessionStorage > localStorage > cookie.
 * @returns {string|null} The stored API key, or null if none found
 */
function getStoredApiKey() {
    try {
        const sessionKey = sessionStorage.getItem(API_KEY_SESSION_KEY);
        if (sessionKey) {
            return sessionKey;
        }
    } catch (e) { /* ignore */ }
    try {
        const localKey = localStorage.getItem(API_KEY_LOCAL_KEY);
        if (localKey) {
            return localKey;
        }
    } catch (e) { /* ignore */ }
    return getCookie(API_KEY_COOKIE_KEY);
}

/**
 * Show a warning banner at the top of the page that auto-dismisses.
 * @param {string} message - Warning message to display
 * @param {number} [duration=5000] - Duration in ms before auto-dismiss
 */
function showWarningBanner(message, duration = 5000) {
    const existingBanner = document.getElementById('warning-banner');
    if (existingBanner) {
        existingBanner.remove();
    }

    const banner = document.createElement('div');
    banner.id = 'warning-banner';
    banner.className = 'warning-banner';

    const textSpan = document.createElement('span');
    textSpan.className = 'warning-banner-text';
    textSpan.textContent = message;
    banner.appendChild(textSpan);

    const dismissBtn = document.createElement('button');
    dismissBtn.className = 'warning-banner-dismiss';
    dismissBtn.setAttribute('aria-label', 'Dismiss');
    dismissBtn.textContent = '×';
    banner.appendChild(dismissBtn);

    document.body.appendChild(banner);

    // Dismiss on click
    banner.querySelector('.warning-banner-dismiss').addEventListener('click', () => {
        dismissBanner();
    });

    // Trigger slide-in animation
    requestAnimationFrame(() => {
        banner.classList.add('warning-banner--visible');
    });

    // Auto-dismiss after duration
    const timeoutId = setTimeout(dismissBanner, duration);

    function dismissBanner() {
        clearTimeout(timeoutId);
        banner.classList.remove('warning-banner--visible');
        setTimeout(() => banner.remove(), 300);
    }
}

/**
 * Validate an API key with retry on network errors.
 * @param {string} key - The API key to validate
 * @param {number} [retries=2] - Number of retries on network error
 * @returns {Promise<ApiKeyValidationResult>}
 */
async function validateApiKeyWithRetry(key, retries = 2) {
    const result = await validateApiKey(key);
    if (result.networkError && retries > 0) {
        await new Promise(r => setTimeout(r, 1000));
        return validateApiKeyWithRetry(key, retries - 1);
    }
    return result;
}

/**
 * Recursive median cut color quantization. Splits an array of RGB pixels
 * along the channel with the largest range at the median, producing 2^depth
 * representative color buckets.
 *
 * @param {number[][]} pixels - Array of [r, g, b] triplets
 * @param {number} depth - Recursion depth (produces 2^depth buckets)
 * @returns {{color: number[], count: number}[]} Representative colors with pixel counts
 */
function medianCut(pixels, depth) {
    if (depth === 0 || pixels.length === 0) {
        if (pixels.length === 0) {
            return [];
        }
        let rSum = 0, gSum = 0, bSum = 0;
        for (const p of pixels) {
            rSum += p[0];
            gSum += p[1];
            bSum += p[2];
        }
        const n = pixels.length;

        return [{ color: [Math.round(rSum / n), Math.round(gSum / n), Math.round(bSum / n)], count: n }];
    }

    let rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
    for (const p of pixels) {
        if (p[0] < rMin) { rMin = p[0]; }
        if (p[0] > rMax) { rMax = p[0]; }
        if (p[1] < gMin) { gMin = p[1]; }
        if (p[1] > gMax) { gMax = p[1]; }
        if (p[2] < bMin) { bMin = p[2]; }
        if (p[2] > bMax) { bMax = p[2]; }
    }

    const rRange = rMax - rMin;
    const gRange = gMax - gMin;
    const bRange = bMax - bMin;
    const channel = rRange >= gRange && rRange >= bRange ? 0 : gRange >= bRange ? 1 : 2;

    pixels.sort((a, b) => a[channel] - b[channel]);
    const mid = Math.floor(pixels.length / 2);

    return [
        ...medianCut(pixels.slice(0, mid), depth - 1),
        ...medianCut(pixels.slice(mid), depth - 1)
    ];
}

/**
 * Convert RGB values (0-255) to HSL. Returns an object with h (0-360),
 * s (0-100), and l (0-100).
 *
 * @param {number} r - Red (0-255)
 * @param {number} g - Green (0-255)
 * @param {number} b - Blue (0-255)
 * @returns {{h: number, s: number, l: number}}
 */
function rgbToHsl(r, g, b) {
    r /= 255;
    g /= 255;
    b /= 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    const d = max - min;

    if (d === 0) {
        return { h: 0, s: 0, l: l * 100 };
    }

    const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    let h;
    if (max === r) {
        h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
    } else if (max === g) {
        h = ((b - r) / d + 2) / 6;
    } else {
        h = ((r - g) / d + 4) / 6;
    }

    return { h: h * 360, s: s * 100, l: l * 100 };
}

/**
 * Convert HSL values to an RGB array [r, g, b] with values 0-255.
 *
 * @param {number} h - Hue in degrees (0-360)
 * @param {number} s - Saturation percentage (0-100)
 * @param {number} l - Lightness percentage (0-100)
 * @returns {number[]} [r, g, b] values (0-255, rounded)
 */
function hslToRgb(h, s, l) {
    s /= 100;
    l /= 100;
    const c = (1 - Math.abs(2 * l - 1)) * s;
    const x = c * (1 - Math.abs((h / 60) % 2 - 1));
    const m = l - c / 2;
    let r, g, b;
    if (h < 60) { r = c; g = x; b = 0; }
    else if (h < 120) { r = x; g = c; b = 0; }
    else if (h < 180) { r = 0; g = c; b = x; }
    else if (h < 240) { r = 0; g = x; b = c; }
    else if (h < 300) { r = x; g = 0; b = c; }
    else { r = c; g = 0; b = x; }

    return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
}

/**
 * Extract primary and accent colors from a loaded image using median cut
 * quantization. Stride-samples ~2500 pixels at native resolution (no
 * downscale blending), quantizes to 8 representative colors, then selects
 * primary (most pixels, excluding near-black/white) and accent (most
 * saturated color at least 30 degrees from primary hue).
 *
 * @param {HTMLImageElement} img - A loaded, same-origin image element
 * @returns {{primaryHue: number, accentHue: number}|null} Hues in degrees, or null if grayscale
 */
function extractColors(img) {
    const w = img.naturalWidth;
    const h = img.naturalHeight;
    if (w === 0 || h === 0) {
        return null;
    }

    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    ctx.drawImage(img, 0, 0);

    let data;
    try {
        data = ctx.getImageData(0, 0, w, h).data;
    } catch (e) {
        return null;
    }

    // Stride-sample ~2500 pixels evenly across the image
    const targetSamples = 2500;
    const stride = Math.max(1, Math.floor(Math.sqrt(w * h / targetSamples)));
    const pixels = [];
    for (let y = 0; y < h; y += stride) {
        for (let x = 0; x < w; x += stride) {
            const i = (y * w + x) * 4;
            pixels.push([data[i], data[i + 1], data[i + 2]]);
        }
    }

    // Quantize to 8 representative colors
    const palette = medianCut(pixels, 3);

    // Filter out near-black and near-white, convert to HSL
    const candidates = [];
    for (const entry of palette) {
        const hsl = rgbToHsl(entry.color[0], entry.color[1], entry.color[2]);
        if (hsl.l < 12 || hsl.l > 88) {
            continue;
        }
        candidates.push({ hsl, count: entry.count });
    }

    if (candidates.length === 0) {
        return null;
    }

    // Check if image is too desaturated (grayscale)
    let totalSat = 0;
    let totalCount = 0;
    for (const c of candidates) {
        totalSat += c.hsl.s * c.count;
        totalCount += c.count;
    }
    if (totalCount === 0 || totalSat / totalCount < 10) {
        return null;
    }

    // Primary = color with most pixels
    candidates.sort((a, b) => b.count - a.count);
    const primaryHue = candidates[0].hsl.h;

    // Accent = most saturated color at least 30° from primary
    let accentHue = (primaryHue + 20) % 360;
    let bestAccentSat = -1;
    for (let i = 1; i < candidates.length; i++) {
        const hueDiff = Math.abs(candidates[i].hsl.h - primaryHue);
        const angularDiff = Math.min(hueDiff, 360 - hueDiff);
        if (angularDiff >= 30 && candidates[i].hsl.s > bestAccentSat) {
            bestAccentSat = candidates[i].hsl.s;
            accentHue = candidates[i].hsl.h;
        }
    }

    return { primaryHue, accentHue };
}

/**
 * Apply a complete color palette derived from extracted hues to the body
 * element. Overrides all hue-dependent CSS custom properties while preserving
 * each variable's original saturation and lightness values.
 *
 * @param {number} hue - Primary hue in degrees (0-360)
 * @param {number} accentHue - Accent hue in degrees (0-360)
 */
function applyArtworkPalette(hue, accentHue) {
    const s = document.body.style;

    // Background colors
    s.setProperty('--bg-base', 'hsl(' + hue + ', 28%, 14%)');
    s.setProperty('--bg-elevated', 'hsl(' + hue + ', 31%, 24%)');
    s.setProperty('--bg-surface', 'hsl(' + hue + ', 27%, 20%)');
    s.setProperty('--bg-grad-1', 'hsl(' + hue + ', 30%, 22%)');
    s.setProperty('--bg-grad-2', 'hsl(' + hue + ', 31%, 20%)');
    s.setProperty('--bg-grad-3', 'hsl(' + hue + ', 30%, 18%)');
    s.setProperty('--bg-grad-4', 'hsl(' + hue + ', 30%, 16%)');

    // Border colors
    s.setProperty('--border-subtle', 'hsl(' + hue + ', 22%, 29%)');
    s.setProperty('--border-muted', 'hsl(' + hue + ', 18%, 35%)');

    // Primary palette
    s.setProperty('--primary-900', 'hsl(' + hue + ', 47%, 34%)');
    s.setProperty('--primary-800', 'hsl(' + hue + ', 55%, 41%)');
    s.setProperty('--primary-500', 'hsl(' + hue + ', 84%, 67%)');
    s.setProperty('--primary-400', 'hsl(' + hue + ', 89%, 74%)');
    s.setProperty('--primary-300', 'hsl(' + hue + ', 94%, 82%)');
    s.setProperty('--primary-200', 'hsl(' + hue + ', 96%, 89%)');

    // Accent
    s.setProperty('--accent-500', 'hsl(' + accentHue + ', 90%, 66%)');

    // Success
    s.setProperty('--success', 'hsl(' + hue + ', 100%, 84%)');

    // Text colors (tertiary and muted have hue tint)
    s.setProperty('--text-tertiary', 'hsl(' + hue + ', 20%, 77%)');
    s.setProperty('--text-muted', 'hsl(' + hue + ', 18%, 66%)');

    // Primary alpha variants
    const primary500 = hslToRgb(hue, 84, 67);
    const alphas = [
        ['--primary-a5', 0.05], ['--primary-a8', 0.08], ['--primary-a10', 0.1],
        ['--primary-a12', 0.12], ['--primary-a15', 0.15], ['--primary-a20', 0.2],
        ['--primary-a25', 0.25], ['--primary-a30', 0.3], ['--primary-a40', 0.4]
    ];
    for (const [prop, alpha] of alphas) {
        s.setProperty(prop, 'rgba(' + primary500[0] + ', ' + primary500[1] + ', ' + primary500[2] + ', ' + alpha + ')');
    }

    // Glow variants
    const glow400 = hslToRgb(hue, 89, 74);
    s.setProperty('--glow-400-50', 'rgba(' + glow400[0] + ', ' + glow400[1] + ', ' + glow400[2] + ', 0.5)');
    s.setProperty('--glow-400-40', 'rgba(' + glow400[0] + ', ' + glow400[1] + ', ' + glow400[2] + ', 0.4)');

    const glow300 = hslToRgb(hue, 94, 82);
    s.setProperty('--glow-300-40', 'rgba(' + glow300[0] + ', ' + glow300[1] + ', ' + glow300[2] + ', 0.4)');
    s.setProperty('--glow-300-50', 'rgba(' + glow300[0] + ', ' + glow300[1] + ', ' + glow300[2] + ', 0.5)');
    s.setProperty('--glow-300-60', 'rgba(' + glow300[0] + ', ' + glow300[1] + ', ' + glow300[2] + ', 0.6)');

    const glow200 = hslToRgb(hue, 96, 89);
    s.setProperty('--glow-200-60', 'rgba(' + glow200[0] + ', ' + glow200[1] + ', ' + glow200[2] + ', 0.6)');
}

/**
 * Apply the default indigo palette to body.style, overriding the monochrome
 * :root defaults. Uses exact Tailwind indigo hex values (each shade has its own
 * hue) so the result matches the original pre-artwork-extraction appearance.
 */
function applyDefaultPalette() {
    const s = document.body.style;

    s.setProperty('--bg-base', '#1a1a2e');
    s.setProperty('--bg-elevated', '#2a2a50');
    s.setProperty('--bg-surface', '#252541');
    s.setProperty('--bg-grad-1', '#28284a');
    s.setProperty('--bg-grad-2', '#242445');
    s.setProperty('--bg-grad-3', '#202040');
    s.setProperty('--bg-grad-4', '#1c1c35');

    s.setProperty('--border-subtle', '#3a3a5a');
    s.setProperty('--border-muted', '#4b4a6a');

    s.setProperty('--primary-900', '#312e81');
    s.setProperty('--primary-800', '#3730a3');
    s.setProperty('--primary-500', '#6366f1');
    s.setProperty('--primary-400', '#818cf8');
    s.setProperty('--primary-300', '#a5b4fc');
    s.setProperty('--primary-200', '#c7d2fe');

    s.setProperty('--accent-500', '#8b5cf6');
    s.setProperty('--success', '#adb4ff');

    s.setProperty('--text-tertiary', '#b8b8d0');
    s.setProperty('--text-muted', '#9898b8');

    const alphaBase = [99, 102, 241];
    for (const [prop, a] of [
        ['--primary-a5', 0.05], ['--primary-a8', 0.08], ['--primary-a10', 0.1],
        ['--primary-a12', 0.12], ['--primary-a15', 0.15], ['--primary-a20', 0.2],
        ['--primary-a25', 0.25], ['--primary-a30', 0.3], ['--primary-a40', 0.4]
    ]) {
        s.setProperty(prop, 'rgba(' + alphaBase[0] + ', ' + alphaBase[1] + ', ' + alphaBase[2] + ', ' + a + ')');
    }

    s.setProperty('--glow-400-50', 'rgba(129, 140, 248, 0.5)');
    s.setProperty('--glow-400-40', 'rgba(129, 140, 248, 0.4)');
    s.setProperty('--glow-300-40', 'rgba(165, 180, 252, 0.4)');
    s.setProperty('--glow-300-50', 'rgba(165, 180, 252, 0.5)');
    s.setProperty('--glow-300-60', 'rgba(165, 180, 252, 0.6)');
    s.setProperty('--glow-200-60', 'rgba(199, 210, 254, 0.6)');
}

/**
 * Initialize feed artwork with dynamic color theming. The page starts with a
 * monochrome (B&W) appearance; once colors are determined (from artwork
 * extraction or defaults), the colored gradient crossfades in via `theme-ready`.
 * Loads the feed icon, extracts primary and accent colors via median cut
 * quantization, and re-themes the page's CSS custom properties. Sets the artwork
 * image inside the drop zone (making it clickable to select files, hiding the
 * CTA button), and displays a gradient backdrop for ambient color. On load
 * failure (404), adds `theme-ready` so the default indigo gradient fades in;
 * the CTA button is shown as fallback.
 */
function initFeedArtwork() {
    const artwork = document.getElementById('feed-artwork');
    if (!artwork) {
        applyDefaultPalette();
        document.body.classList.add('theme-ready');

        return;
    }

    let artworkProcessed = false;

    /** Process a successfully loaded artwork image (guarded against double invocation). */
    function processArtwork() {
        if (artworkProcessed) {
            return;
        }
        artworkProcessed = true;

        const colors = extractColors(artwork);

        const backdrop = document.getElementById('artwork-backdrop');
        if (!backdrop) {
            applyDefaultPalette();
            document.body.classList.add('theme-ready');

            return;
        }

        const backdropImg = document.getElementById('artwork-backdrop-img');
        if (backdropImg) {
            backdropImg.src = artwork.src;
        }

        const dropZone = document.getElementById('drop-zone');
        if (dropZone && !dropZone.classList.contains('drop-zone--has-artwork')) {
            dropZone.classList.add('drop-zone--has-artwork');
            dropZone.addEventListener('click', () => {
                if (dropZone.classList.contains('drop-zone--has-artwork')) {
                    document.getElementById('file-input')?.click();
                }
            });
        }

        if (colors) {
            applyArtworkPalette(colors.primaryHue, colors.accentHue);
            backdrop.style.background = 'linear-gradient(135deg, hsl(' + colors.primaryHue + ', 50%, 18%), hsl(' + colors.accentHue + ', 50%, 18%))';
        } else {
            applyDefaultPalette();
        }

        document.body.classList.add('theme-ready');

        // Force layout read so the browser paints the image before the opacity transition
        backdrop.offsetHeight;
        backdrop.classList.add('artwork-backdrop--visible');
    }

    artwork.addEventListener('load', processArtwork);
    artwork.addEventListener('error', () => {
        artwork.remove();
        // Server may have pre-set artwork layout, but icon failed — revert to CTA
        document.getElementById('drop-zone')?.classList.remove('drop-zone--has-artwork');
        applyDefaultPalette();
        document.body.classList.add('theme-ready');
    });

    // Handle cached image that loaded before event listeners attached
    if (artwork.complete) {
        if (artwork.naturalWidth > 0) {
            processArtwork();
        } else {
            artwork.remove();
            document.getElementById('drop-zone')?.classList.remove('drop-zone--has-artwork');
            applyDefaultPalette();
            document.body.classList.add('theme-ready');
        }
    }
}

async function init() {
    initFeedArtwork();

    // Cache layout dimensions for consistent animations
    cacheLayoutDimensions();
    // Recalculate on resize
    window.addEventListener('resize', cacheLayoutDimensions);

    /**
     * Show the no-key UI with an optional error state.
     * @param {'invalid'|'no-access'|null} [errorType=null] - Type of error to display
     */
    function showNoKeyUI(errorType = null) {
        if (errorType) {
            clearApiKey();
        }
        setNoKeyError(errorType);
        showState('no-key');
        initNoKeyState();
    }

    /**
     * Try to validate and use a fallback key when the primary key fails.
     * @param {string|null} fallbackKey - The fallback key to try
     * @param {string} warningMessage - Message to show if fallback succeeds
     * @returns {Promise<boolean>} True if fallback succeeded
     */
    async function tryFallbackKey(fallbackKey, warningMessage) {
        if (!fallbackKey) {
            return false;
        }

        const fallbackValidation = await validateApiKey(fallbackKey);
        if (fallbackValidation.valid && fallbackValidation.feedAccess) {
            showWarningBanner(warningMessage);
            saveApiKey(fallbackKey);

            return true;
        }

        return false;
    }

    // Storage precedence: fragment > sessionStorage > localStorage > cookie
    // Fragment format: API_KEY or API_KEY&source=localhost:PORT&token=TOKEN (local source mode)
    const fragment = window.location.hash.slice(1);
    let extractedKey = null;

    if (fragment) {
        const ampIndex = fragment.indexOf('&');
        if (ampIndex === -1) {
            extractedKey = fragment;
        } else {
            extractedKey = fragment.substring(0, ampIndex);
            const params = new URLSearchParams(fragment.substring(ampIndex + 1));
            const source = params.get('source');
            const token = params.get('token');
            if (source && token) {
                localSourceConfig = { host: source, token };
                sessionStorage.setItem('localSource', JSON.stringify(localSourceConfig));
            }
        }
    }

    if (!localSourceConfig) {
        try {
            const stored = sessionStorage.getItem('localSource');
            if (stored) {
                localSourceConfig = JSON.parse(stored);
            }
        } catch { /* ignore corrupt data */ }
    }

    const storedKey = getStoredApiKey();
    const primaryKey = extractedKey || storedKey;

    if (extractedKey) {
        // Clear fragment from URL immediately for cleaner UX
        history.replaceState(null, '', window.location.pathname + window.location.search);
    }

    if (primaryKey) {
        // Optimistic: set key and proceed to ready state immediately
        apiKey = primaryKey;

        // Background validation (fire-and-forget)
        validateApiKeyWithRetry(primaryKey).then(async (validation) => {
            if (validation.valid && validation.feedAccess) {
                // Key is good — ensure it's persisted in all storage layers
                saveApiKey(primaryKey);
            } else if (validation.networkError) {
                showWarningBanner(extractedKey ? 'Server unreachable \u2014 using URL key' : 'Server unreachable \u2014 using saved key');
                saveApiKey(primaryKey);
            } else if (extractedKey) {
                // Fragment key failed — try stored key as fallback
                if (validation.valid && !validation.feedAccess) {
                    if (await tryFallbackKey(storedKey, 'URL key does not have access to this feed. Using saved key.')) {
                        return;
                    }
                } else {
                    if (await tryFallbackKey(storedKey, 'Invalid URL key. Using saved key.')) {
                        return;
                    }
                }

                // Both keys failed — only disrupt UI if user is still on the ready screen
                if (document.getElementById('ready').style.display !== 'none') {
                    const errorType = (validation.valid && !validation.feedAccess) ? 'no-access' : (validation.valid ? null : 'invalid');
                    showNoKeyUI(errorType);
                }
            } else {
                // Stored key failed — only disrupt UI if user is still on the ready screen
                if (document.getElementById('ready').style.display !== 'none') {
                    const errorType = (validation.valid && !validation.feedAccess) ? 'no-access' : null;
                    showNoKeyUI(errorType);
                }
            }
        }).catch(() => {});
    } else {
        // No key available
        showNoKeyUI(null);

        return;
    }

    // Debug: show error state with ?error query param (dev only)
    if (IS_DEV && window.location.search.includes('error')) {
        showError('This is a test error message');

        return;
    }

    // Open feed-level SSE for cross-tab/cross-device queue sync
    connectFeedEvents();

    // Start fetching recent jobs from server (fire-and-forget, merges when resolved)
    const serverJobsPromise = fetchRecentJobs();

    // Local source mode: restore saved queue first (so completed/in-progress entries
    // are present for dedup), then fetch files from local server on top.
    if (localSourceConfig) {
        await restoreQueueState();
        await connectLocalSource();
        if (uploadQueue.length === 0) {
            showState('ready');
        }
        await initHistorySection();
        serverJobsPromise.then(mergeServerJobs).catch(() => {});

        return;
    }

    // Consume any files shared via PWA Share Target. If found, skip restoreQueueState
    // because it overwrites uploadQueue and would discard the shared files.
    if (await consumeSharedFiles()) {
        await initHistorySection();
        serverJobsPromise.then(mergeServerJobs).catch(() => {});

        return;
    }

    // Try to restore previous queue state (e.g., after page refresh)
    if (await restoreQueueState()) {
        serverJobsPromise.then(mergeServerJobs).catch(() => {});

        return;
    }

    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
    serverJobsPromise.then(mergeServerJobs).catch(() => {});
}

/** @type {boolean} - Whether initNoKeyState has already been called (prevents duplicate listeners) */
let noKeyStateInitialized = false;

/**
 * Set the no-key state error display.
 * Updates the no-key title to show error state.
 * @param {'invalid'|'no-access'|null} errorType - Type of error, or null to clear
 */
function setNoKeyError(errorType) {
    const noKeyTitleEl = document.getElementById('no-key-title');
    if (!noKeyTitleEl) {
        return;
    }

    if (errorType === 'no-access') {
        noKeyTitleEl.textContent = STR_NO_ACCESS;
    } else if (errorType === 'invalid') {
        noKeyTitleEl.textContent = STR_INVALID_KEY;
    } else {
        noKeyTitleEl.textContent = STR_API_KEY_REQUIRED;
    }
}

/**
 * Initialize the no-key state UI with paste button and textarea functionality.
 * Only attaches event listeners once.
 *
 * Phase 8b: The paste button auto-detects fp_-prefixed API keys in the clipboard.
 * If an fp_ key is found, it is validated automatically. If not found (or clipboard
 * is inaccessible), the textarea is shown for manual input.
 */
function initNoKeyState() {
    if (noKeyStateInitialized) {
        return;
    }
    noKeyStateInitialized = true;

    const pasteBtn = document.getElementById('paste-key-btn');
    const textareaContainer = document.getElementById('key-textarea-container');
    const textarea = document.getElementById('key-textarea');
    const saveBtn = document.getElementById('save-key-btn');

    /** @type {boolean} - Guard to prevent concurrent validation attempts */
    let isValidating = false;

    /**
     * Morph the paste button into the textarea input.
     * @param {boolean} [preserveTitle=false] - If true, don't change the title (e.g., error title already shown)
     */
    function morphToTextarea(preserveTitle = false) {
        pasteBtn.style.display = 'none';
        textareaContainer.style.display = 'flex';
        if (!preserveTitle) {
            const noKeyTitleEl = document.getElementById('no-key-title');
            if (noKeyTitleEl) {
                noKeyTitleEl.textContent = STR_PASTE_KEY_BELOW;
            }
        }
        textarea.focus();
    }

    /**
     * Reset the no-key state to initial (paste button visible).
     */
    function resetNoKeyState() {
        pasteBtn.style.display = 'block';
        pasteBtn.disabled = false;
        pasteBtn.textContent = STR_PASTE_KEY;
        textareaContainer.style.display = 'none';
        textarea.value = '';
        textarea.disabled = false;
        saveBtn.disabled = false;
        saveBtn.textContent = STR_SAVE_KEY;
        setNoKeyError(null);
    }

    /**
     * Transition to ready state after successful key validation.
     */
    async function transitionToReadyState() {
        resetNoKeyState();
        if (localSourceConfig) {
            await connectLocalSource();
            if (uploadQueue.length === 0) {
                showState('ready');
            }
            await initHistorySection();

            return;
        }
        if (await consumeSharedFiles()) {
            await initHistorySection();

            return;
        }
        showState('ready');
        await initHistorySection();
        document.getElementById('select-file').focus();
    }

    /**
     * Validate the key from textarea and proceed if valid.
     * Guarded to prevent concurrent validation attempts.
     */
    async function validateTextareaKey() {
        if (isValidating) {
            return;
        }

        let key = textarea.value.trim();
        if (!key) {
            showWarningBanner('Please enter an API key');

            return;
        }

        // Auto-extract fp_ prefixed key if pasted with surrounding text
        // Format: fp_{userId}_{secret} where secret is 22 chars base64url
        const fpKeyMatch = key.match(/fp_[a-zA-Z0-9-]+_[A-Za-z0-9_-]{22}(?=[^A-Za-z0-9_-]|$)/);
        if (fpKeyMatch) {
            key = fpKeyMatch[0];
        }

        isValidating = true;

        // Show loading state
        saveBtn.disabled = true;
        saveBtn.textContent = 'Validating...';
        textarea.disabled = true;

        try {
            const validation = await validateApiKey(key);

            if (validation.valid && validation.feedAccess) {
                saveApiKey(key);
                await transitionToReadyState();
            } else if (validation.networkError) {
                // Server unreachable - accept key optimistically
                showWarningBanner('Server unreachable \u2014 using entered key');
                saveApiKey(key);
                await transitionToReadyState();
            } else if (validation.valid && !validation.feedAccess) {
                saveBtn.disabled = false;
                saveBtn.textContent = STR_SAVE_KEY;
                textarea.disabled = false;
                showWarningBanner(STR_NO_FEED_ACCESS);
            } else {
                saveBtn.disabled = false;
                saveBtn.textContent = STR_SAVE_KEY;
                textarea.disabled = false;
                showWarningBanner(validation.error || STR_INVALID_KEY);
            }
        } finally {
            isValidating = false;
        }
    }

    // Paste button: attempt clipboard read, auto-validate if fp_ key found, else show textarea
    pasteBtn.addEventListener('click', async () => {
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            // Clipboard API not available (requires secure context: HTTPS or localhost)
            morphToTextarea();

            return;
        }

        try {
            pasteBtn.disabled = true;
            pasteBtn.textContent = 'Reading...';

            const clipboardText = await navigator.clipboard.readText();
            const trimmed = clipboardText ? clipboardText.trim() : '';

            // Match fp_ key - use lookahead for end boundary to avoid issues with \b and special chars
            // Format: fp_{userId}_{secret} where secret is 22 chars base64url
            const fpKeyMatch = trimmed.match(/fp_[a-zA-Z0-9-]+_[A-Za-z0-9_-]{22}(?=[^A-Za-z0-9_-]|$)/);

            if (!fpKeyMatch) {
                // No fp_ key recognized in clipboard - show textarea for manual input
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea();

                return;
            }

            // fp_ key found - validate it
            pasteBtn.textContent = 'Validating...';
            const apiKeyToValidate = fpKeyMatch[0];
            const validation = await validateApiKey(apiKeyToValidate);

            if (validation.valid && validation.feedAccess) {
                saveApiKey(apiKeyToValidate);
                await transitionToReadyState();
            } else if (validation.networkError) {
                // Server unreachable - accept pasted key optimistically
                showWarningBanner('Server unreachable \u2014 using pasted key');
                saveApiKey(apiKeyToValidate);
                await transitionToReadyState();
            } else if (validation.valid && !validation.feedAccess) {
                // Valid key but no feed access - show textarea with error
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea(true);
                setNoKeyError('no-access');
            } else {
                // fp_ key found but invalid - show textarea with error
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea(true);
                setNoKeyError('invalid');
            }
        } catch (err) {
            // Permission denied or other error, morph to textarea
            pasteBtn.disabled = false;
            pasteBtn.textContent = STR_PASTE_KEY;
            morphToTextarea();
        }
    });

    // Textarea input handler - reset title to textarea mode on typing
    textarea.addEventListener('input', () => {
        const noKeyTitleEl = document.getElementById('no-key-title');
        if (noKeyTitleEl) {
            noKeyTitleEl.textContent = STR_PASTE_KEY_BELOW;
        }
    });

    // Textarea paste handler - validate immediately on paste
    textarea.addEventListener('paste', async () => {
        // Let the paste complete, then validate
        setTimeout(async () => {
            if (textarea.value.trim()) {
                await validateTextareaKey();
            }
        }, 0);
    });

    // Textarea Enter key handler
    textarea.addEventListener('keydown', async (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            await validateTextareaKey();
        }
    });

    // Save button handler
    saveBtn.addEventListener('click', async () => {
        await validateTextareaKey();
    });
}

document.getElementById('select-file').addEventListener('click', () => {
    document.getElementById('file-input').click();
});

// If server pre-set artwork class, register drop zone click handler immediately
// (processArtwork would normally do this, but the image hasn't loaded yet)
const dropZoneEl = document.getElementById('drop-zone');
if (dropZoneEl && dropZoneEl.classList.contains('drop-zone--has-artwork')) {
    dropZoneEl.addEventListener('click', () => {
        if (dropZoneEl.classList.contains('drop-zone--has-artwork')) {
            document.getElementById('file-input')?.click();
        }
    });
}

document.getElementById('try-another').addEventListener('click', async () => {
    clearQueueState();
    document.getElementById('file-input').value = '';
    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
});

document.getElementById('file-input').addEventListener('change', (e) => {
    const files = Array.from(e.target.files);
    if (files.length === 0) {
        return;
    }
    addFilesToQueue(files);
    e.target.value = '';
});

// Drag and drop support (ready state drop zone)
const dropZone = document.getElementById('drop-zone');

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropZone.classList.add('drag-over');
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
});

dropZone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
    const files = Array.from(e.dataTransfer.files);
    if (files.length === 0) {
        return;
    }
    addFilesToQueue(files);
});

// Queue state: add files button and drop zone
document.getElementById('queue-add-files')?.addEventListener('click', () => {
    document.getElementById('queue-file-input').click();
});

document.getElementById('queue-file-input')?.addEventListener('change', (e) => {
    const files = Array.from(e.target.files);
    if (files.length === 0) {
        return;
    }
    addFilesToQueue(files);
    e.target.value = '';
});

const queueDropZone = document.getElementById('queue-drop-zone');
if (queueDropZone) {
    queueDropZone.addEventListener('dragover', (e) => {
        e.preventDefault();
        queueDropZone.classList.add('drag-over');
    });

    queueDropZone.addEventListener('dragleave', (e) => {
        e.preventDefault();
        queueDropZone.classList.remove('drag-over');
    });

    queueDropZone.addEventListener('drop', (e) => {
        e.preventDefault();
        queueDropZone.classList.remove('drag-over');
        const files = Array.from(e.dataTransfer.files);
        if (files.length === 0) {
            return;
        }
        addFilesToQueue(files);
    });
}

// ============================================================================
// QUEUE MANAGEMENT (Steps 4-8)
// ============================================================================

const Q_MORPH_DURATION = 400;

/**
 * Animate the queue drop zone morphing from the ready-state drop zone dimensions.
 * Mirrors the history section morph pattern: set explicit start → reflow → transition to target.
 */
function animateQueueDropZoneMorph() {
    const queueDZ = document.getElementById('queue-drop-zone');
    if (!queueDZ) return;

    // Measure target height before overriding
    const targetHeight = queueDZ.getBoundingClientRect().height;

    // Set starting height (matching ready-state drop zone)
    queueDZ.classList.add('queue-drop-zone--morphing');
    queueDZ.style.height = getCollapsedHeight() + 'px';

    // Commit starting state, then transition to target
    void queueDZ.offsetHeight;
    queueDZ.style.height = targetHeight + 'px';

    // Clean up after transition
    setTimeout(() => {
        queueDZ.classList.remove('queue-drop-zone--morphing');
        queueDZ.style.height = '';
    }, Q_MORPH_DURATION);
}

/**
 * Prepare the ready-state drop zone for a morph animation before it becomes visible.
 * Sets the morphing class and start height while #drop-zone is still hidden (display: none),
 * so blur-fade-in is suppressed when showState('ready') makes it visible.
 * @param {number} startHeight - The height to start from (queue drop zone height).
 */
function prepareReadyDropZoneMorph(startHeight) {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) return;

    dropZone.classList.add('drop-zone--morphing');
    dropZone.style.height = startHeight + 'px';
}

/**
 * Run the ready-state drop zone morph transition. Must be called after showState('ready')
 * and prepareReadyDropZoneMorph() so the element is visible with its start height committed.
 */
function animateReadyDropZoneMorph() {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) return;

    // Compute target from CSS constants — avoids a reflow that would commit the target
    // value prematurely and cause the transition to snap (280→48→280 in one frame)
    const targetHeight = dropZone.classList.contains('drop-zone--has-artwork')
        ? COLLAPSED_WIDTH
        : COLLAPSED_HEIGHT_DEFAULT;

    // Start height was set by prepareReadyDropZoneMorph while element was hidden.
    // Commit it now that the element is visible.
    void dropZone.offsetHeight;

    // Transition to target
    dropZone.style.height = targetHeight + 'px';

    setTimeout(() => {
        // Keep animation suppressed so blur-fade-in doesn't replay after morph class is removed
        dropZone.style.animation = 'none';
        dropZone.querySelector('.btn-primary')?.style.setProperty('animation', 'none');
        dropZone.querySelector('.hint')?.style.setProperty('animation', 'none');
        dropZone.classList.remove('drop-zone--morphing');
        dropZone.style.height = '';
    }, Q_MORPH_DURATION);
}

/**
 * Add files to the upload queue. Duplicates (same name + size) of active or completed
 * entries are silently skipped. Invalid files are marked as failed immediately.
 * Transitions to queue state and starts processing if idle.
 * When already in queue state, appends new items incrementally to avoid full DOM rebuild
 * (which would interrupt in-progress fade-in animations on recently added items).
 * @param {Array<File>} files
 */
function addFilesToQueue(files) {
    if (files.length === 0) {
        return;
    }

    const newEntries = [];

    for (const file of files) {
        const isDuplicate = uploadQueue.some(e =>
            e.fileName === file.name &&
            e.fileSize === file.size &&
            (e.status === 'queued' || e.status === 'uploading' || e.status === 'normalizing' || e.status === 'completed')
        );
        if (isDuplicate) continue;

        const valid = isValidAudioFile(file);
        const entry = {
            id: generateEntryId(),
            file: file,
            status: valid ? 'queued' : 'failed',
            progress: 0,
            stage: null,
            jobId: null,
            episodeId: null,
            episode: null,
            error: valid ? null : 'Unsupported file type',
            xhr: null,
            eventSource: null,
            fileSize: file.size,
            fileName: file.name,
            validationError: !valid,
            backgroundMonitoring: false,
            startedAt: Date.now(),
            _resolveMonitor: null
        };
        uploadQueue.push(entry);
        newEntries.push(entry);
    }

    if (newEntries.length === 0) {
        return;
    }

    const previousState = getCurrentState();

    if (previousState !== 'queue') {
        showState('queue');
    } else {
        updateQueueTitle();
    }

    if (previousState === 'ready') {
        animateQueueDropZoneMorph();
    }

    if (previousState === 'queue') {
        // Append new items incrementally to avoid full rebuild flicker
        // (full rebuild destroys in-progress fade-in animations on existing items)
        const container = document.getElementById('queue-list');
        if (container) {
            for (const entry of newEntries) {
                const el = createQueueItemElement(entry);
                el.style.animation = 'blur-fade-in 0.3s ease both';
                container.prepend(el);
            }
        }
    } else {
        renderQueueList(false);
    }

    rebindProgressAnimator();
    saveQueueState();

    if (!isUploading) {
        processQueue();
    }
}

/**
 * Remove a queued (not yet started) entry from the queue.
 * Returns to ready state if queue becomes empty.
 * @param {string} entryId
 */
function removeFromQueue(entryId) {
    const index = uploadQueue.findIndex(e => e.id === entryId);
    if (index === -1) {
        return;
    }

    const entry = uploadQueue[index];
    if (entry.status !== 'queued') {
        return;
    }

    uploadQueue.splice(index, 1);
    removeQueueItemFromDOM(entryId);

    saveQueueState();
    checkAllComplete();
}

/**
 * Dismiss a failed or cancelled entry from the queue.
 * Returns to ready state if queue becomes empty.
 * @param {string} entryId
 */
async function dismissEntry(entryId) {
    const index = uploadQueue.findIndex(e => e.id === entryId);
    if (index === -1) {
        return;
    }

    const entry = uploadQueue[index];
    if (entry.status !== 'failed' && entry.status !== 'cancelled') {
        return;
    }

    const jobId = entry.jobId;
    if (jobId) {
        dismissedJobIds.add(jobId);
    }

    uploadQueue.splice(index, 1);
    removeQueueItemFromDOM(entryId);

    saveQueueState();
    checkAllComplete();

    // Mark as cancelled server-side so mergeServerJobs won't re-add it
    if (jobId) {
        try {
            await fetch('/api/jobs/' + jobId + '/cancel', {
                method: 'POST',
                headers: { 'X-API-Key': apiKey }
            });
        } catch {
            // Best-effort
        }
    }
}

/**
 * Find the next queued entry and start processing it.
 * If no queued entries remain, checks if all work is complete.
 */
function processQueue() {
    const nextEntry = uploadQueue.find(e => e.status === 'queued');
    if (!nextEntry) {
        checkAllComplete();

        return;
    }

    isUploading = true;
    activeUploadId = nextEntry.id;
    void processEntry(nextEntry);
}

/**
 * Reset active state and advance to next queued entry.
 */
function advanceQueue() {
    activeUploadId = null;
    isUploading = false;
    processQueue();
}

/**
 * Check if all entries have reached a terminal state.
 * When all are terminal, stays in queue state and animates title to "Pushed".
 * When queue is empty, transitions back to ready state.
 */
function checkAllComplete() {
    if (uploadQueue.length === 0) {
        const queueDZHeight = document.getElementById('queue-drop-zone')?.getBoundingClientRect().height || 0;
        if (queueDZHeight > 0) prepareReadyDropZoneMorph(queueDZHeight);
        clearQueueState();
        showState('ready');
        if (queueDZHeight > 0) animateReadyDropZoneMorph();
        void initHistorySection();

        return;
    }

    const hasActiveWork = uploadQueue.some(e =>
        e.status === 'queued' || e.status === 'uploading' || e.status === 'normalizing'
    );
    if (!hasActiveWork) {
        isUploading = false;
        activeUploadId = null;
        updateQueueTitle();
        saveQueueState();
    }
}

/**
 * Fire-and-forget wrapper around monitorEntryNormalization for background monitoring.
 * Sets entry.backgroundMonitoring = true so progressAnimator is not used.
 * When the promise resolves: updates DOM, saves state, calls checkAllComplete().
 * @param {QueueEntry} entry
 */
function monitorEntryNormalizationInBackground(entry) {
    entry.backgroundMonitoring = true;
    monitorEntryNormalization(entry).then(() => {
        entry.backgroundMonitoring = false;
        if (uploadQueue.includes(entry)) {
            updateQueueItemInDOM(entry);
        }
        saveQueueState();
        checkAllComplete();
    });
}

/**
 * Process a single queue entry — upload file, handle sync/async response.
 * @param {QueueEntry} entry
 */
async function processEntry(entry) {
    entry.status = 'uploading';
    entry.progress = 0;
    updateQueueItemInDOM(entry);

    saveQueueState();

    const progressBar = getEntryProgressBar(entry.id);
    progressAnimator.startWithAssumption('Uploading', progressBar, entry.fileSize);

    const formData = new FormData();
    formData.append('file', entry.file);

    try {
        const response = await new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            entry.xhr = xhr;

            xhr.upload.addEventListener('progress', (e) => {
                if (e.lengthComputable) {
                    const percent = Math.round((e.loaded / e.total) * 100);
                    entry.progress = percent;
                    progressAnimator.setTarget(percent, 'Uploading');
                    updateQueueItemProgress(entry);
                }
            });

            xhr.onload = () => {
                entry.xhr = null;
                progressAnimator.reset();
                resolve({ status: xhr.status, body: xhr.responseText });
            };

            xhr.onerror = () => {
                entry.xhr = null;
                progressAnimator.reset();
                reject(new Error('Network error'));
            };

            xhr.onabort = () => {
                entry.xhr = null;
                progressAnimator.reset();
                reject(new DOMException('Upload cancelled', 'AbortError'));
            };

            let uploadUrl = '/api/feeds/' + FEED_ID + '/episodes?normalize=true&source=Browser';
            xhr.open('POST', uploadUrl);
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            entry.status = 'completed';
            entry.episode = episode;
            entry.progress = 100;
            saveToLocalHistory(episode);
            refreshHistoryList();
        } else if (response.status === 202) {
            const jobResponse = JSON.parse(response.body);
            entry.jobId = jobResponse.jobId;
            entry.status = 'normalizing';
            entry.stage = 'Queued';
            entry.progress = 0;
            updateQueueItemInDOM(entry);
            saveQueueState();
            monitorEntryNormalizationInBackground(entry);
            advanceQueue();

            return;
        } else if (response.status === 401 || response.status === 403) {
            entry.status = 'failed';
            entry.error = response.status === 401 ? STR_INVALID_KEY : STR_NO_FEED_ACCESS;
            // Auth failure: remove all remaining queued entries (they'd fail too)
            const queued = uploadQueue.filter(e => e.status === 'queued');
            for (const q of queued) {
                removeQueueItemFromDOM(q.id);
            }
            uploadQueue = uploadQueue.filter(e => e.status !== 'queued');
        } else {
            const error = tryParseJson(response.body);
            entry.status = 'failed';
            entry.error = error?.error || 'Upload failed';
        }
    } catch (err) {
        if (err.name === 'AbortError') {
            removeQueueItemFromDOM(entry.id);
            uploadQueue.splice(uploadQueue.indexOf(entry), 1);
        
            saveQueueState();
            advanceQueue();

            return;
        }

        entry.status = 'failed';
        entry.error = err.message || 'Upload failed';
    }

    updateQueueItemInDOM(entry);

    saveQueueState();
    advanceQueue();
}

Number.prototype.sigDig = function(minSigDigs) {
    if (this.valueOf() === 0) return '0';
    const magnitude = Math.floor(Math.log10(Math.abs(this)));
    const decimals = Math.max(0, minSigDigs - 1 - magnitude);

    return this.toFixed(decimals);
};

Number.prototype.formatBytes = function formatBytes(sigDigs = 2, unitSuffix = '') {
    const value = this.valueOf();
    const absValue = Math.abs(value);
    const sign = value < 0 ? '-' : '';

    if (absValue === 0) {
        return '0 B' + unitSuffix;
    }

    const k = 1024;
    const units = ['B', 'kB', 'MB', 'GB'];
    const i = Math.floor(Math.log(absValue) / Math.log(k));

    if (i >= units.length) {
        return value + ' B' + unitSuffix;
    }

    const number = parseFloat((absValue / Math.pow(k, i)).toFixed(1)).sigDig(sigDigs);

    return sign + number + ' ' + units[i] + unitSuffix;
};

/**
 * Format a duration from TimeSpan string (HH:MM:SS or similar) to human-readable.
 * @param {string} duration - Duration in TimeSpan format
 * @returns {string}
 */
function formatDuration(duration) {
    if (!duration) {
        return '';
    }

    // Parse TimeSpan format: "HH:MM:SS" or "D.HH:MM:SS"
    const parts = duration.split(':');
    if (parts.length < 2) {
        return '';
    }

    const hours = parseInt(parts[0], 10) || 0;
    const minutes = parseInt(parts[1], 10) || 0;
    const seconds = parseInt(parts[2]?.split('.')[0], 10) || 0;

    if (hours > 0) {
        return hours + 'h ' + minutes + 'm';
    } else if (minutes > 0) {
        return minutes + 'm ' + seconds + 's';
    }

    return seconds + 's';
}

/**
 * Format a date as "28 nov 2025" (day, short month lowercase, year).
 * @param {string|null} dateString - ISO date string or null
 * @returns {string}
 */
function formatDate(dateString) {
    if (!dateString) {
        return '';
    }

    const date = new Date(dateString);
    const day = date.getDate();
    const months = ['jan', 'feb', 'mar', 'apr', 'maj', 'jun', 'jul', 'aug', 'sep', 'okt', 'nov', 'dec'];
    const month = months[date.getMonth()];
    const year = date.getFullYear();

    return `${day} ${month} ${year}`;
}

/**
 * Format a date as relative time (e.g., "2 minutes ago", "3 hours ago").
 * @param {string|null} dateString - ISO date string or null
 * @returns {string}
 */
function formatRelativeTime(dateString) {
    if (!dateString) {
        return '';
    }

    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now - date;

    // Handle future dates gracefully
    if (diffMs < 0) {
        return 'just now';
    }

    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) {
        return 'just now';
    }

    if (diffMins < 60) {
        return diffMins === 1 ? '1 minute ago' : diffMins + ' minutes ago';
    }

    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) {
        return diffHours === 1 ? '1 hour ago' : diffHours + ' hours ago';
    }

    const diffDays = Math.floor(diffHours / 24);

    return diffDays === 1 ? '1 day ago' : diffDays + ' days ago';
}

// ============================================================================
// HISTORY SECTION (Ready/Queue States)
// ============================================================================

/**
 * Load upload history from localStorage for this feed.
 * Returns an empty array if no history exists or if parsing fails.
 * @returns {Array<Episode>} Array of episodes uploaded from this browser, most recent first
 */
function loadLocalHistory() {
    try {
        const stored = localStorage.getItem(HISTORY_STORAGE_KEY);
        if (!stored) {
            return [];
        }

        const history = JSON.parse(stored);

        return Array.isArray(history) ? history : [];
    } catch (e) {
        console.warn('Failed to load history from localStorage:', e);

        return [];
    }
}

/**
 * Save an episode to localStorage history.
 * Removes any existing entry with the same ID and prepends the new episode.
 * Trims history to MAX_LOCAL_HISTORY items. Fails silently on localStorage errors.
 * @param {Episode} episode - Episode to save (must have an id property)
 */
function saveToLocalHistory(episode) {
    if (!episode || !episode.id) {
        return;
    }

    try {
        const history = loadLocalHistory();

        // Remove existing entry with same ID (if re-uploading)
        const filtered = history.filter(e => e.id !== episode.id);

        // Prepend new episode and trim to max size
        const updated = [episode, ...filtered].slice(0, MAX_LOCAL_HISTORY);

        localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(updated));
    } catch (e) {
        console.warn('Failed to save to localStorage:', e);
    }
}

/**
 * Invalidates caches and re-renders the history panel if it's currently visible.
 * Call after any upload completion (sync or async) to keep the history list up to date.
 */
async function refreshHistoryList() {
    cachedBrowserUploads = null;
    cachedAllUploads = null;
    const section = document.getElementById('history-section');
    if (!section || section.style.display === 'none') {
        return;
    }
    const uploads = await fetchHistoryByFilter();
    renderHistoryList(uploads);
}

/**
 * Load saved filter preference from localStorage.
 * Returns 'local' as the default if no preference is saved or if parsing fails.
 * @returns {'local'|'browser'|'all'} The saved filter preference
 */
function loadFilterPreference() {
    try {
        const saved = localStorage.getItem(HISTORY_FILTER_KEY);
        if (saved && ['local', 'browser', 'all'].includes(saved)) {
            return saved;
        }
    } catch (e) {
        // Ignore
    }

    return 'local';
}

/**
 * Save filter preference to localStorage. Fails silently on errors.
 * @param {'local'|'browser'|'all'} filter - The filter mode to save
 */
function saveFilterPreference(filter) {
    try {
        localStorage.setItem(HISTORY_FILTER_KEY, filter);
    } catch (e) {
        // Ignore
    }
}

// Animation timing constants (match CSS --h-* variables)
// Step 1: Drop zone / queue blurs behind panel
const H_BLUR_DELAY = 150;
// Step 2: Pause before swap
const H_PAUSE = 100;
// Step 3: History panel morphs
const H_MORPH = 400;
const HISTORY_TRANSITION_DURATION = H_BLUR_DELAY + H_PAUSE + H_MORPH;

/**
 * Toggle the history section collapsed/expanded state with animation.
 * On desktop: morphs from drop zone size to full width with staggered content reveal.
 * On mobile: slides down as fullscreen overlay.
 * In ready state, drop zone fades out/in via CSS. In queue state, overlays queue content.
 * Pushes a browser history entry when expanding so the back button closes the panel.
 * @param {boolean} [expand] - Force expand (true) or collapse (false). If omitted, toggles.
 */
function toggleHistorySection(expand) {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    const selectFileBtn = document.getElementById('select-file');
    const isMobile = window.matchMedia('(max-width: 768px)').matches;
    const isQueueState = getCurrentState() === 'queue';
    if (!section || !toggle) {
        return;
    }

    const isExpanded = toggle.getAttribute('aria-expanded') === 'true';
    const newState = expand !== undefined ? expand : !isExpanded;

    toggle.setAttribute('aria-expanded', newState.toString());

    // Push browser history entry when expanding so the back button closes the panel
    if (newState && !historyPanelPushedState) {
        history.pushState({ historyPanel: true }, '');
        historyPanelPushedState = true;
    }

    // Animate text change: 1) delay, 2) fade out, 3) resize, 4) fade in, 5) end
    const newText = newState ? '← Back' : 'History';
    const textSpan = toggle.querySelector('.history-toggle-text');
    const TEXT_FADE = 150;
    const WIDTH_ANIM = 150;

    // On mobile, button is full-width so skip width animation
    if (isMobile) {
        // Simple text swap with fade
        const delay = newState ? H_BLUR_DELAY + H_PAUSE : 0;
        setTimeout(() => {
            toggle.classList.add('text-fading');
        }, delay);
        setTimeout(() => {
            textSpan.textContent = newText;
            toggle.classList.remove('text-fading');
        }, delay + TEXT_FADE);
    } else {
        // Desktop: full animation with width change
        const currentWidth = toggle.offsetWidth;

        // Measure target width with new text (while hidden)
        textSpan.style.visibility = 'hidden';
        textSpan.textContent = newText;
        toggle.style.width = 'auto';
        const newWidth = toggle.offsetWidth;
        textSpan.textContent = newState ? 'History' : '← Back'; // restore old text
        textSpan.style.visibility = '';
        toggle.style.width = currentWidth + 'px';

        // Same cadence in both ready and queue states
        const totalDuration = newState
            ? H_BLUR_DELAY + H_PAUSE + H_MORPH
            : H_MORPH;
        const fadeInStart = totalDuration - TEXT_FADE;
        const widthStart = fadeInStart - WIDTH_ANIM;
        const fadeOutStart = Math.max(0, widthStart - TEXT_FADE);

        // Step 2: Fade out old text
        setTimeout(() => {
            toggle.classList.add('text-fading');
        }, fadeOutStart);

        // Step 3: Change text and animate width
        setTimeout(() => {
            textSpan.textContent = newText;
            toggle.style.width = newWidth + 'px';
        }, widthStart);

        // Step 4: Fade in new text
        setTimeout(() => {
            toggle.classList.remove('text-fading');
        }, fadeInStart);

        // Step 5: Clean up after animation ends
        setTimeout(() => {
            toggle.style.width = '';
        }, totalDuration);
    }

    // In ready state, disable select-file button when expanded (hidden via CSS but could still be activated)
    if (!isQueueState && selectFileBtn) {
        selectFileBtn.disabled = newState;
    }

    // Mobile: toggle frosted overlay
    const frostedOverlay = document.getElementById('frosted-overlay');
    if (isMobile && frostedOverlay) {
        if (newState) {
            frostedOverlay.classList.add('frosted-overlay--active');
        }
        // When collapsing, delay removing frosted overlay until after panel fades out
        // (handled below with COLLAPSE_DURATION timeout)
    }

    if (newState) {
        // Expanding: measure natural height, animate from collapsed to expanded

        // Clear blur-fade-in animation so filter property is free for CSS transitions.
        // Force reflow so the base filter: blur(0) is committed before the expanded
        // class triggers blur(12px), ensuring the transition animates.
        const container = document.querySelector('.container');
        const blurTarget = container?.classList.contains('state-queue')
            ? document.getElementById('queue')
            : document.getElementById('drop-zone');
        if (blurTarget) {
            blurTarget.style.animation = 'none';
            void blurTarget.offsetHeight;
        }

        // Use cached values for consistent animations
        const containerWidth = cachedContainerWidth;
        const collapsedMargin = cachedCollapsedMargin + 'px';

        // Measure natural height by temporarily expanding
        section.style.height = 'auto';
        section.classList.add('history-section--expanded');
        const naturalHeight = section.offsetHeight;
        section.classList.remove('history-section--expanded');

        // Set explicit starting state for history section
        // Only on desktop (mobile uses position: fixed fullscreen, no animation needed)
        if (!isMobile) {
            section.style.height = getCollapsedHeight() + 'px';
            section.style.width = COLLAPSED_WIDTH + 'px';
            section.style.marginLeft = collapsedMargin;
            section.style.marginRight = collapsedMargin;
            // Force reflow to ensure starting values are applied
            void section.offsetHeight;
        }

        // Animate to expanded state
        section.classList.add('history-section--expanded');
        if (!isMobile) {
            // Desktop: animate to calculated dimensions
            section.style.height = naturalHeight + 'px';
            section.style.width = containerWidth + 'px';
            section.style.marginLeft = '0';
            section.style.marginRight = '0';
        }

        // After transition, clear inline styles and mark as settled
        setTimeout(() => {
            if (!isMobile) {
                section.style.height = 'auto';
                section.style.width = '';
                section.style.marginLeft = '';
                section.style.marginRight = '';
            }
            // Mark as settled so tab switches don't have expand animation delays
            section.classList.add('history-section--settled');
        }, HISTORY_TRANSITION_DURATION);

        // Focus the selected item (or first item) after transition
        setTimeout(() => {
            const selectedItem = document.querySelector('#history-list .upload-item--selected');
            const firstItem = document.querySelector('#history-list .upload-item');
            const itemToFocus = selectedItem || firstItem;
            if (itemToFocus) {
                itemToFocus.focus();
            }
        }, HISTORY_TRANSITION_DURATION);
    } else {
        // Collapsing: morph back to collapsed dimensions

        // Use cached values for consistent animations
        const containerWidth = cachedContainerWidth;
        const collapsedMargin = cachedCollapsedMargin + 'px';
        const currentHeight = section.offsetHeight;

        // Set explicit starting state (expanded)
        // Only on desktop (mobile uses position: fixed fullscreen, no animation needed)
        if (!isMobile) {
            section.style.height = currentHeight + 'px';
            section.style.width = containerWidth + 'px';
            section.style.marginLeft = '0';
            section.style.marginRight = '0';
            // Force reflow to ensure starting values are applied
            void section.offsetHeight;
        }

        // Step 1: Add collapsing class (removes expand animations, enables collapse transitions)
        section.classList.add('history-section--collapsing');
        section.classList.remove('history-section--expanded', 'history-section--settled');

        // Step 2: Fade out content AND animate panel dimensions simultaneously
        void section.offsetHeight; // Force reflow
        section.classList.add('history-section--fade-out');
        if (!isMobile) {
            section.style.height = getCollapsedHeight() + 'px';
            section.style.width = COLLAPSED_WIDTH + 'px';
            section.style.marginLeft = collapsedMargin;
            section.style.marginRight = collapsedMargin;
        }

        // After transition, clear all inline styles and collapsing classes
        setTimeout(() => {
            section.classList.remove('history-section--collapsing', 'history-section--fade-out');
            if (!isMobile) {
                section.style.height = '';
                section.style.width = '';
                section.style.marginLeft = '';
                section.style.marginRight = '';
            }
            // Remove frosted overlay after panel has faded out
            if (isMobile && frostedOverlay) {
                frostedOverlay.classList.remove('frosted-overlay--active');
            }
        }, H_MORPH);

        // Reset scroll position and selection to first item
        const list = document.getElementById('history-list');
        if (list) {
            list.scrollTop = 0;
        }
        if (historyData && historyData.length > 0) {
            historySelectedId = historyData[0].id;
            list?.querySelectorAll('.upload-item').forEach((item, index) => {
                const isFirst = index === 0;
                item.classList.toggle('upload-item--selected', isFirst);
                item.setAttribute('aria-selected', isFirst.toString());
            });
            updateHistoryInfoCard(historyData[0]);
        }

        // In ready state, return focus to the upload button
        if (!isQueueState && selectFileBtn) {
            selectFileBtn.focus();
        }
    }
}

/**
 * Fetch and cache browser uploads from server.
 * Cache is invalidated in refreshHistoryList() after each upload completes.
 * @returns {Promise<Array<Episode>>} Array of browser uploads
 */
async function fetchBrowserUploads() {
    if (cachedBrowserUploads !== null) {
        return cachedBrowserUploads;
    }

    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/recent-uploads?source=Browser&limit=50', {
            headers: { 'X-API-Key': apiKey }
        });

        if (!response.ok) {
            return cachedBrowserUploads || [];
        }

        cachedBrowserUploads = await response.json();

        return cachedBrowserUploads;
    } catch (err) {
        console.warn('Error fetching browser uploads:', err);

        return cachedBrowserUploads || [];
    }
}

/**
 * Fetch and cache all uploads from server.
 * Cache is invalidated in refreshHistoryList() after each upload completes.
 * @returns {Promise<Array<Episode>>} Array of all uploads
 */
async function fetchAllUploads() {
    if (cachedAllUploads !== null) {
        return cachedAllUploads;
    }

    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/recent-uploads?limit=50', {
            headers: { 'X-API-Key': apiKey }
        });

        if (!response.ok) {
            return cachedAllUploads || [];
        }

        cachedAllUploads = await response.json();

        return cachedAllUploads;
    } catch (err) {
        console.warn('Error fetching all uploads:', err);

        return cachedAllUploads || [];
    }
}

/**
 * Fetch uploads based on current filter mode. Uses cached data when available.
 * - 'local': Returns episodes from localStorage, filtered to only include episodes that still exist on server
 * - 'browser': Returns cached browser uploads from API (source=Browser)
 * - 'all': Returns cached all uploads from API (no source filter)
 * @returns {Promise<Array<Episode>>} Array of episodes matching the current filter
 */
async function fetchHistoryByFilter() {
    if (historyFilter === 'local') {
        // Get local history and validate against cached server data
        const localHistory = loadLocalHistory();
        if (localHistory.length === 0) {
            return [];
        }

        // Use cached browser uploads to check which local episodes still exist
        const serverEpisodes = await fetchBrowserUploads();
        const serverIds = new Set(serverEpisodes.map(e => e.id));

        // Filter local history to only include episodes that exist on server
        const validHistory = localHistory.filter(e => serverIds.has(e.id));

        // Update localStorage to remove deleted episodes
        if (validHistory.length !== localHistory.length) {
            try {
                localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(validHistory));
            } catch (e) {
                // Ignore localStorage errors
            }
        }

        return validHistory;
    } else if (historyFilter === 'browser') {
        return await fetchBrowserUploads();
    } else {
        return await fetchAllUploads();
    }
}

/**
 * Get the appropriate empty state message for the current filter.
 * @returns {string} A user-friendly message explaining why the list is empty
 */
function getHistoryEmptyMessage() {
    switch (historyFilter) {
        case 'local':
            return 'No uploads from this browser yet';
        case 'browser':
            return 'No browser uploads yet';
        case 'all':
            return 'No uploads yet';
        default:
            return 'No uploads yet';
    }
}

/**
 * Update the history info card with episode data.
 * Populates the info card fields if an episode is provided, otherwise hides the card.
 * @param {Episode|null} episode - Episode to display, or null to hide the card
 */
function updateHistoryInfoCard(episode) {
    const infoCard = document.getElementById('history-info');
    if (!infoCard) {
        return;
    }

    if (!episode) {
        infoCard.style.display = 'none';

        return;
    }

    infoCard.style.display = 'grid';
    document.getElementById('history-info-title').textContent = episode.title || episode.fileName;
    document.getElementById('history-info-filename').textContent = episode.fileName || '';
    document.getElementById('history-info-published').textContent = formatDate(episode.publishedDate);

    // Combine duration and size: "16m 40s (31 MB)"
    const duration = formatDuration(episode.duration);
    const size = episode.fileSize ? episode.fileSize.formatBytes() : '';
    let durationText = duration;
    if (duration && size) {
        durationText = duration + ' (' + size + ')';
    } else if (size) {
        durationText = size;
    }
    document.getElementById('history-info-duration').textContent = durationText;

    const uploadedTime = formatRelativeTime(episode.uploadedAt);
    document.getElementById('history-info-uploaded').textContent = uploadedTime;
    document.getElementById('history-info-uploaded-label').style.display = uploadedTime ? '' : 'none';
    document.getElementById('history-info-uploaded').style.display = uploadedTime ? '' : 'none';
}

/**
 * Update the scroll fade mask based on scroll position.
 * Shows fade-top, fade-bottom, fade-both, or none depending on scroll state.
 */
function updateHistoryListScrollState() {
    const list = document.getElementById('history-list');
    if (!list) {
        return;
    }

    const isScrollable = list.scrollHeight > list.clientHeight;
    const isAtTop = list.scrollTop <= 2;
    const isAtBottom = list.scrollTop + list.clientHeight >= list.scrollHeight - 2;

    list.classList.remove('fade-top', 'fade-bottom', 'fade-both');

    if (!isScrollable) {
        // No fade needed
    } else if (isAtTop && !isAtBottom) {
        list.classList.add('fade-bottom');
    } else if (!isAtTop && isAtBottom) {
        list.classList.add('fade-top');
    } else if (!isAtTop && !isAtBottom) {
        list.classList.add('fade-both');
    }
}

/**
 * Render the history list in the ready state.
 * Clears existing list, creates upload items, and selects the first item by default.
 * Shows an empty state message if no uploads are provided.
 * @param {Array<Episode>} uploads - Array of episodes to render
 * @param {boolean} [focusFirst=false] - Whether to focus the first item after rendering
 */
function renderHistoryList(uploads, focusFirst = false) {
    const list = document.getElementById('history-list');
    const emptyState = document.getElementById('history-empty');
    if (!list) {
        return;
    }

    list.innerHTML = '';
    list.classList.remove('fade-top', 'fade-bottom', 'fade-both');

    if (!uploads || uploads.length === 0) {
        historyData = null;
        historySelectedId = null;
        updateHistoryInfoCard(null);
        if (emptyState) {
            emptyState.textContent = getHistoryEmptyMessage();
            emptyState.style.display = 'block';
        }

        return;
    }

    if (emptyState) {
        emptyState.style.display = 'none';
    }

    historyData = uploads;

    // Select first item by default
    historySelectedId = uploads[0].id;
    updateHistoryInfoCard(uploads[0]);

    uploads.forEach((upload, index) => {
        const item = document.createElement('div');
        item.className = 'upload-item';
        item.dataset.id = upload.id;
        item.tabIndex = 0;
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', (upload.id === historySelectedId).toString());

        // Staggered animation delay for each item (via CSS custom property)
        item.style.setProperty('--stagger-index', String(index));

        if (upload.id === historySelectedId) {
            item.classList.add('upload-item--selected');
        }

        const title = document.createElement('span');
        const titleText = upload.title || upload.fileName;
        title.className = 'upload-title';
        title.textContent = titleText;

        const time = document.createElement('span');
        time.className = 'upload-time';
        time.textContent = formatRelativeTime(upload.uploadedAt);

        item.appendChild(title);
        if (upload.uploadedAt) {
            item.appendChild(time);
        }

        item.addEventListener('click', () => selectHistoryUpload(upload.id));

        list.appendChild(item);
    });

    // Check scroll state after render (use requestAnimationFrame to ensure layout is complete)
    requestAnimationFrame(() => {
        updateHistoryListScrollState();

        // Focus first item if requested (e.g., after switching tabs via keyboard)
        if (focusFirst) {
            const firstItem = list.querySelector('.upload-item');
            if (firstItem) {
                firstItem.focus();
            }
        }
    });
}

/**
 * Select an upload in the history list.
 * Updates the visual selection state and populates the info card with the selected episode.
 * @param {string} uploadId - ID of the episode to select
 * @param {boolean} [moveFocus=false] - Whether to move focus to the selected item
 */
function selectHistoryUpload(uploadId, moveFocus = false) {
    if (!historyData) {
        return;
    }

    const upload = historyData.find(u => u.id === uploadId);
    if (!upload) {
        return;
    }

    historySelectedId = uploadId;

    // Update visual selection and aria-selected
    const list = document.getElementById('history-list');
    if (list) {
        list.querySelectorAll('.upload-item').forEach(item => {
            const isSelected = item.dataset.id === uploadId;
            item.classList.toggle('upload-item--selected', isSelected);
            item.setAttribute('aria-selected', isSelected.toString());
            if (isSelected && moveFocus) {
                item.focus();
            }
        });
    }

    updateHistoryInfoCard(upload);
}

/**
 * Update filter tab visual state.
 * Sets the active class, aria-selected attribute on filter tabs, and updates
 * the tabpanel's aria-labelledby to point to the active tab.
 */
function updateFilterTabs() {
    const tabs = document.querySelectorAll('#history-section .filter-tab');
    const tabpanel = document.getElementById('history-tabpanel');
    tabs.forEach(tab => {
        const isActive = tab.dataset.filter === historyFilter;
        tab.classList.toggle('filter-tab--active', isActive);
        tab.setAttribute('aria-selected', isActive.toString());
        if (isActive && tabpanel) {
            tabpanel.setAttribute('aria-labelledby', tab.id);
        }
    });
}

/**
 * Handle filter tab change.
 * Uses request tracking to prevent race conditions when rapidly switching filters.
 * @param {'local'|'browser'|'all'} filter - The filter mode to switch to
 * @param {boolean} [focusFirst=false] - Whether to focus the first list item after loading
 */
async function changeHistoryFilter(filter, focusFirst = false) {
    if (filter === historyFilter) {
        return;
    }

    historyFilter = filter;
    saveFilterPreference(filter);
    updateFilterTabs();

    const requestId = ++pendingFilterRequest;
    const uploads = await fetchHistoryByFilter();

    // Ignore stale response if another filter change happened while fetching
    if (requestId !== pendingFilterRequest) {
        return;
    }

    renderHistoryList(uploads, focusFirst);
}

/**
 * Initialize the history section in the ready or queue state.
 * Loads saved filter preference, fetches uploads, and renders the list.
 * The toggle and section are always shown (even if empty) so users can switch filters.
 */
async function initHistorySection() {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    if (!section) {
        return;
    }

    // Load saved filter preference
    historyFilter = loadFilterPreference();
    updateFilterTabs();

    // Fetch and render history
    const uploads = await fetchHistoryByFilter();

    // Show toggle and section
    if (toggle) {
        toggle.style.display = 'block';
    }
    section.style.display = 'block';
    renderHistoryList(uploads || []);
}

/**
 * Safely parse JSON, returning null if parsing fails.
 * @param {string} text
 * @returns {Object|null}
 */
function tryParseJson(text) {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

/** @param {string} message */
function showError(message) {
    showState('error');
    document.getElementById('error-message').textContent = message;
    document.getElementById('try-another').focus();
}

/**
 * @typedef {Object} JobStatus
 * @property {string} status - Queued, Processing, Completed, Failed, Cancelled
 * @property {string} [stage] - Queued, Analyzing, Normalizing, Finishing, Completed, Failed, Cancelled
 * @property {number} [progressPercent] - Progress percentage (0-100) for Analyzing, Normalizing stages
 * @property {string} [error]
 */

/**
 * Progress animator with velocity smoothing and continuous interpolation.
 * Uses requestAnimationFrame for smooth 60fps updates between SSE events.
 *
 * When PROGRESS_SMOOTHING is disabled (via PushPage:ProgressSmoothing appsetting),
 * the animator bypasses all interpolation and updates the progress bar directly
 * with raw values from SSE events.
 */
const progressAnimator = {
    // Default initial velocities in bytes/second (used if no learned value exists)
    DEFAULT_INITIAL_VELOCITIES: {
        'Uploading': 1024 * 1024, // 1 MB/s
        'Analyzing': 100 * 1024,  // 100 kB/s
        'Normalizing': 100 * 1024 // 100 kB/s
    },
    // Max initial velocities for animation (prevents overshoot before real data arrives)
    MAX_INITIAL_VELOCITIES: {
        'Uploading': 10 * 1024 * 1024 // 10 MB/s
    },
    LEARNED_INITIAL_VELOCITY_STORAGE_KEY: 'featherpod_learned_initial_velocity',
    currentFileSize: 0,

    /**
     * Get learned initial velocity for a stage from localStorage.
     * Stored as bytes/second, converted to %/second using current file size.
     * @param {string} stage
     * @returns {{velocity: number, wasClamped: boolean}} Velocity in %/second and whether it was clamped
     */
    getLearnedInitialVelocity(stage) {
        // Check URL overrides first (dev only)
        if (VELOCITY_OVERRIDES[stage] != null) {
            const bytesPerSec = VELOCITY_OVERRIDES[stage];
            if (this.currentFileSize > 0) {
                return { velocity: (bytesPerSec / this.currentFileSize) * 100, wasClamped: false };
            }

            return { velocity: 1, wasClamped: false };
        }

        let bytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;

        try {
            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            if (stored) {
                const values = JSON.parse(stored);
                if (values[stage] != null) {
                    bytesPerSec = values[stage];
                }
            }
        } catch (e) {
            // Ignore localStorage errors
        }

        // Cap initial velocity for animation (still learn the real value, just don't overshoot)
        const maxBytesPerSec = this.MAX_INITIAL_VELOCITIES[stage];
        let wasClamped = false;
        if (maxBytesPerSec != null && bytesPerSec > maxBytesPerSec) {
            bytesPerSec = maxBytesPerSec;
            wasClamped = true;
        }

        if (this.currentFileSize > 0) {
            return { velocity: (bytesPerSec / this.currentFileSize) * 100, wasClamped };
        }

        return { velocity: 1, wasClamped: false }; // Fallback if no file size set
    },

    /**
     * Update learned initial velocity for a stage using EMA.
     * Receives velocity in %/second, converts to bytes/second for storage.
     * @param {string} stage
     * @param {number} actualVelocity - The actual velocity in %/second
     * @returns {boolean} True if we learned (velocity was meaningful)
     */
    updateLearnedInitialVelocity(stage, actualVelocity) {
        // Skip learning if no file size or near-zero velocity
        if (this.currentFileSize <= 0 || actualVelocity < 0.1) {
            return false;
        }

        try {
            // Convert %/s to B/s
            const actualBytesPerSec = (actualVelocity / 100) * this.currentFileSize;

            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            const values = stored ? JSON.parse(stored) : {};
            const defaultBytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;
            const currentBytesPerSec = values[stage] ?? defaultBytesPerSec;

            // Target 80% of actual velocity, asymmetric EMA (fast to decrease, slow to increase)
            const targetBytesPerSec = actualBytesPerSec * 0.8;
            const alpha = targetBytesPerSec < currentBytesPerSec ? 0.8 : 0.2;
            const updatedBytesPerSec = currentBytesPerSec * (1 - alpha) + targetBytesPerSec * alpha;

            values[stage] = updatedBytesPerSec;
            localStorage.setItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY, JSON.stringify(values));

            const current = currentBytesPerSec.formatBytes(2, '/s');
            const updated = updatedBytesPerSec.formatBytes(2, '/s');
            const actual = actualBytesPerSec.formatBytes(2, '/s');
            console.log(`[${stage}] Initial velocity: ${current} -> ${updated} (actual: ${actual})`);

            return true;
        } catch (e) {
            // Ignore localStorage errors
            return false;
        }
    },

    currentValue: 0,
    targetValue: 0,
    velocity: 0,
    acceleration: 0,
    displayVelocity: 0,
    lastUpdateTime: 0,
    lastFrameTime: 0,
    stageStartTime: 0,
    speedFactor: 1,
    animationId: null,
    progressBar: null,
    ghostBar: null,
    currentStage: null,
    awaitingFirstUpdate: false,
    isRestoring: false,

    /**
     * Start animating with learned initial velocity before real data arrives.
     * @param {string} stage - The stage name
     * @param {HTMLElement} progressBar - The progress bar element
     * @param {number} [fileSize] - File size in bytes (sets currentFileSize if provided)
     */
    startWithAssumption(stage, progressBar, fileSize) {
        const wasRestoring = this.isRestoring;
        this.reset();
        if (fileSize != null) {
            this.currentFileSize = fileSize;
        }
        const now = performance.now();
        this.currentStage = stage;
        this.lastUpdateTime = now;
        this.stageStartTime = now;

        // When smoothing is disabled, just store progressBar for direct updates
        if (!PROGRESS_SMOOTHING) {
            this.progressBar = progressBar;

            return;
        }

        if (wasRestoring) {
            this.progressBar = progressBar;
            this.awaitingFirstUpdate = true;
            this.isRestoring = true;
        } else {
            const { velocity: learnedInitialVelocity, wasClamped } = this.getLearnedInitialVelocity(stage);
            this.targetValue = learnedInitialVelocity;
            this.velocity = learnedInitialVelocity;
            this.displayVelocity = learnedInitialVelocity;
            this.awaitingFirstUpdate = true;
            const bytesPerSec = (learnedInitialVelocity / 100) * this.currentFileSize;
            const clampedSuffix = wasClamped ? ' (clamped)' : '';
            console.log(`[${stage}] Initial velocity: ${bytesPerSec.formatBytes(2, '/s')}${clampedSuffix}`);
            this.start(progressBar);
        }
    },

    /**
     * Set new target from SSE update.
     * @param {number} value - New progress target (0-100)
     * @param {string} stage - Current stage name
     */
    setTarget(value, stage) {
        // When smoothing is disabled, update progress bar directly
        if (!PROGRESS_SMOOTHING) {
            if (this.progressBar) {
                this.progressBar.style.width = value + '%';
            }

            return;
        }

        // Initial calibration phase
        if (this.awaitingFirstUpdate && stage === this.currentStage) {
            const now = performance.now();
            const dt = (now - this.lastUpdateTime) / 1000;

            if (this.isRestoring) {
                const { velocity: learnedVelocity } = this.getLearnedInitialVelocity(stage);
                this.currentValue = value;
                this.targetValue = value;
                this.velocity = learnedVelocity;
                this.displayVelocity = learnedVelocity;
                this.lastUpdateTime = now;
                this.awaitingFirstUpdate = false;
                this.isRestoring = false;
                if (this.progressBar) {
                    this.progressBar.style.width = value + '%';
                    this.start(this.progressBar);
                }

                return;
            }

            // Recalibrate velocity from real data
            if (dt > 0.05) {
                const totalElapsed = (now - this.stageStartTime) / 1000;
                const realVelocity = totalElapsed > 0 ? value / totalElapsed : this.velocity;
                this.velocity = realVelocity;
                this.displayVelocity = realVelocity;
                this.targetValue = value;
                this.acceleration = 0;
                this.lastUpdateTime = now;
            } else {
                this.targetValue = value;
            }

            // Learn velocity for future assumptions
            const totalElapsed = (now - this.stageStartTime) / 1000;
            const learningVelocity = totalElapsed > 0.05 ? value / totalElapsed : 0;
            if (this.updateLearnedInitialVelocity(stage, learningVelocity)) {
                this.awaitingFirstUpdate = false;
            }

            return;
        }

        // Normal updates (after calibration)
        const now = performance.now();
        const dt = (now - this.lastUpdateTime) / 1000;

        if (dt > 0 && dt < 5) {
            const instantVelocity = (value - this.targetValue) / dt;
            const prevVelocity = this.velocity;
            this.velocity = this.velocity * 0.5 + instantVelocity * 0.5;

            const instantAcceleration = (this.velocity - prevVelocity) / dt;
            this.acceleration = this.acceleration * 0.5 + instantAcceleration * 0.5;

            if (SHOW_GHOST) {
                const bytesPerSec = (instantVelocity / 100) * this.currentFileSize;
                const deltaBytesPerSec = ((this.displayVelocity - instantVelocity) / 100) * this.currentFileSize;
                const deltaSign = deltaBytesPerSec >= 0 ? '+' : '';
                console.log(`[${stage}] Instant velocity: ${bytesPerSec.formatBytes(2, '/s')} (${deltaSign}${deltaBytesPerSec.formatBytes(2, '/s')})`);
            }
        }

        this.targetValue = value;
        this.lastUpdateTime = now;

        // Update ghost bar immediately (it shows unfiltered value)
        if (this.ghostBar) {
            this.ghostBar.style.width = value + '%';
        }
    },

    /**
     * Start the animation loop.
     * @param {HTMLElement} progressBar - The progress bar element
     */
    start(progressBar) {
        this.progressBar = progressBar;
        // Skip animation loop when smoothing is disabled
        if (!PROGRESS_SMOOTHING) {
            return;
        }
        // Set up ghost bar if enabled (only useful with smoothing to compare raw vs smoothed)
        if (SHOW_GHOST && progressBar) {
            const ghostId = progressBar.id + '-ghost';
            this.ghostBar = document.getElementById(ghostId);
            if (this.ghostBar) {
                this.ghostBar.parentElement.classList.add('visible');
            }
        }
        if (!this.animationId) {
            this.lastFrameTime = performance.now();
            this.animate();
        }
    },

    stop() {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
    },

    reset() {
        this.stop();
        this.currentValue = 0;
        this.targetValue = 0;
        this.velocity = 0;
        this.acceleration = 0;
        this.displayVelocity = 0;
        this.lastUpdateTime = 0;
        this.stageStartTime = 0;
        this.speedFactor = 1;
        this.currentStage = null;
        this.awaitingFirstUpdate = false;
        if (this.ghostBar) {
            this.ghostBar.parentElement.classList.remove('visible');
            this.ghostBar = null;
        }
        // isRestoring survives reset() - cleared in setTarget() after use
    },

    /** Mark that we're restoring after page refresh (snap to actual value). */
    setRestoring() {
        this.isRestoring = true;
    },

    animate() {
        const now = performance.now();
        const rawDt = (now - this.lastFrameTime) / 1000;
        this.lastFrameTime = now;
        const dt = Math.min(rawDt, 0.1);

        const timeSinceUpdate = (now - this.lastUpdateTime) / 1000;
        const estimatedActual = this.targetValue + this.velocity * timeSinceUpdate;

        // Snap to estimated position if tab was inactive (capped at target to avoid overshooting)
        if (rawDt > 1) {
            this.currentValue = Math.max(this.currentValue, Math.min(estimatedActual, this.targetValue));
            this.displayVelocity = this.velocity;
            this.speedFactor = 1;
        }

        // Ease displayVelocity towards actual velocity
        const velocityEaseRate = 3;
        this.displayVelocity += (this.velocity - this.displayVelocity) * Math.min(1, velocityEaseRate * dt);

        // Predict lag from displayVelocity catching up (integral of velocity gap)
        const velocityGap = this.velocity - this.displayVelocity;
        const baseProjectedLag = velocityGap / velocityEaseRate;

        // Adjust lag prediction based on acceleration
        const accelAdjustment = Math.max(-0.3, Math.min(0.3, this.acceleration * 0.05));
        const projectedLag = baseProjectedLag * (1 - accelAdjustment);

        // Target position with lag compensation
        const compensatedTarget = estimatedActual + projectedLag;
        const error = compensatedTarget - this.currentValue;

        // Ease speed factor based on position error
        // Increase clamp limits and easing rate as target approaches 100% for faster convergence
        const progressFactor = Math.max(0, (this.targetValue - 67) / 33); // 0 at ≤67%, 1 at 100%
        const maxSpeedAdjust = 0.3 + progressFactor * 0.7; // 0.3 normally, up to 1.0 at 100%
        const targetSpeedFactor = 1 + Math.max(-maxSpeedAdjust, Math.min(maxSpeedAdjust, error * 0.3));
        const easingRate = 3 + progressFactor * 3; // 3 normally, up to 6 at 100%
        this.speedFactor += (targetSpeedFactor - this.speedFactor) * Math.min(1, easingRate * dt);

        this.currentValue += this.displayVelocity * dt * this.speedFactor;
        this.currentValue = Math.max(0, Math.min(100, this.currentValue));

        if (this.progressBar) {
            this.progressBar.style.width = this.currentValue + '%';
        }
        if (this.ghostBar) {
            this.ghostBar.style.width = this.targetValue + '%';
        }

        if (this.currentValue < 99.9) {
            this.animationId = requestAnimationFrame(() => this.animate());
        } else {
            this.animationId = null;
        }
    }
};

// ============================================================================
// PER-FILE NORMALIZATION MONITORING (Step 6)
// ============================================================================

/**
 * Update a queue entry's stage and progress from a job status object.
 * Drives the progressAnimator for stages with progress tracking.
 * @param {QueueEntry} entry
 * @param {JobStatus} job
 */
function updateEntryFromJobStatus(entry, job) {
    if (!job.stage) {
        return;
    }

    entry.stage = job.stage;
    const stagesWithProgress = ['Analyzing', 'Normalizing'];
    const isProgressStage = stagesWithProgress.includes(job.stage);
    const progressBar = getEntryProgressBar(entry.id);

    if (entry.backgroundMonitoring) {
        if (isProgressStage) {
            if (progressBar) {
                progressBar.classList.remove('indeterminate');
            }
            if (job.progressPercent != null) {
                entry.progress = job.progressPercent;
                if (progressBar) progressBar.style.width = job.progressPercent + '%';
            }
        } else {
            if (progressBar) {
                progressBar.classList.add('indeterminate');
                progressBar.style.width = '';
            }
        }
    } else {
        if (isProgressStage) {
            if (progressBar) {
                progressBar.classList.remove('indeterminate');
            }

            if (progressAnimator.currentStage !== job.stage) {
                progressAnimator.startWithAssumption(job.stage, progressBar);
            }

            if (job.progressPercent != null) {
                entry.progress = job.progressPercent;
                progressAnimator.setTarget(job.progressPercent, job.stage);
            }

            progressAnimator.start(progressBar);
        } else {
            progressAnimator.reset();
            if (progressBar) {
                progressBar.classList.add('indeterminate');
                progressBar.style.width = '';
            }
        }
    }
}

/**
 * Monitor normalization for a queue entry via SSE with polling fallback.
 * Returns a Promise that resolves when normalization completes/fails/cancels.
 * @param {QueueEntry} entry
 * @returns {Promise<void>}
 */
function monitorEntryNormalization(entry) {
    return new Promise((resolve) => {
        entry._resolveMonitor = resolve;

        const progressBar = getEntryProgressBar(entry.id);
        if (progressBar) {
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
        }
        if (!entry.backgroundMonitoring) {
            progressAnimator.reset();
            progressAnimator.currentFileSize = entry.fileSize;
        }

        const sseUrl = '/api/jobs/' + entry.jobId + '/progress';

        if (typeof EventSource === 'undefined') {
            pollEntryNormalization(entry).then(() => {
                entry._resolveMonitor = null;
                resolve();
            });

            return;
        }

        const eventSource = new EventSource(sseUrl);
        entry.eventSource = eventSource;
        let lastStatus = null;
        let connectionEstablished = false;
        let jobFinished = false;

        function finishMonitoring() {
            entry.eventSource = null;
            entry._resolveMonitor = null;
            resolve();
        }

        const connectionTimeout = setTimeout(() => {
            if (!connectionEstablished) {
                eventSource.close();
                entry.eventSource = null;
                pollEntryNormalization(entry).then(() => {
                    entry._resolveMonitor = null;
                    resolve();
                });
            }
        }, 5000);

        eventSource.onopen = () => {
            connectionEstablished = true;
            clearTimeout(connectionTimeout);
        };

        eventSource.addEventListener('progress', (e) => {
            const parsed = tryParseJson(e.data);
            if (parsed) {
                lastStatus = parsed;
                if (parsed.status === 'Cancelled') {
                    clearTimeout(connectionTimeout);
                    jobFinished = true;
                    eventSource.close();
                    entry.status = 'cancelled';
                    removeQueueItemFromDOM(entry.id);
                    uploadQueue.splice(uploadQueue.indexOf(entry), 1);
                
                    saveQueueState();
                    finishMonitoring();

                    return;
                }
                updateEntryFromJobStatus(entry, parsed);
                updateQueueItemProgress(entry);
            }
        });

        eventSource.addEventListener('done', async () => {
            clearTimeout(connectionTimeout);
            jobFinished = true;
            eventSource.close();

            if (lastStatus?.status === 'Cancelled') {
                entry.status = 'cancelled';
                removeQueueItemFromDOM(entry.id);
                uploadQueue.splice(uploadQueue.indexOf(entry), 1);
            
                saveQueueState();
                finishMonitoring();

                return;
            } else if (lastStatus?.status === 'Completed') {
                cachedBrowserUploads = null;
                const uploads = await fetchBrowserUploads();
                const episode = uploads?.find(ep => ep.fileName === entry.fileName) || null;
                entry.status = 'completed';
                entry.episode = episode;
                entry.progress = 100;
                if (episode) {
                    saveToLocalHistory(episode);
                }
                refreshHistoryList();
            } else {
                entry.status = 'failed';
                entry.error = lastStatus?.error || 'Normalization failed';
            }

            updateQueueItemInDOM(entry);
            finishMonitoring();
        });

        // Named 'error' event from server (e.g., job not found)
        eventSource.addEventListener('error', (e) => {
            if (!e.data) {
                return;
            }
            clearTimeout(connectionTimeout);
            jobFinished = true;
            eventSource.close();
            const data = tryParseJson(e.data);
            entry.status = 'failed';
            entry.error = data?.error || 'An error occurred';
            updateQueueItemInDOM(entry);
            finishMonitoring();
        });

        // Connection error - fall back to polling
        eventSource.onerror = () => {
            if (jobFinished) {
                return;
            }
            clearTimeout(connectionTimeout);
            eventSource.close();
            entry.eventSource = null;
            pollEntryNormalization(entry).then(() => {
                entry._resolveMonitor = null;
                resolve();
            });
        };
    });
}

/**
 * Poll normalization job status for a queue entry (fallback when SSE unavailable).
 * @param {QueueEntry} entry
 * @returns {Promise<void>}
 */
async function pollEntryNormalization(entry) {
    const pollInterval = 2000;

    while (true) {
        // Check if entry was cancelled externally
        if (entry.status === 'cancelled') {
            return;
        }

        try {
            const response = await fetch('/api/jobs/' + entry.jobId, {
                headers: { 'X-API-Key': apiKey }
            });

            if (!response.ok) {
                entry.status = 'failed';
                entry.error = 'Failed to check job status';
                updateQueueItemInDOM(entry);

                return;
            }

            const job = await response.json();

            if (job.status === 'Completed') {
                cachedBrowserUploads = null;
                const uploads = await fetchBrowserUploads();
                const episode = uploads?.find(ep => ep.fileName === entry.fileName) || null;
                entry.status = 'completed';
                entry.episode = episode;
                entry.progress = 100;
                if (episode) {
                    saveToLocalHistory(episode);
                }
                updateQueueItemInDOM(entry);
                refreshHistoryList();

                return;
            } else if (job.status === 'Failed') {
                entry.status = 'failed';
                entry.error = job.error || 'Normalization failed';
                updateQueueItemInDOM(entry);

                return;
            } else if (job.status === 'Cancelled') {
                entry.status = 'cancelled';
                removeQueueItemFromDOM(entry.id);
                uploadQueue.splice(uploadQueue.indexOf(entry), 1);
            
                saveQueueState();

                return;
            }

            updateEntryFromJobStatus(entry, job);
            updateQueueItemProgress(entry);

            await new Promise(r => setTimeout(r, pollInterval));
        } catch (err) {
            entry.status = 'failed';
            entry.error = 'Failed to check job status';
            updateQueueItemInDOM(entry);

            return;
        }
    }
}

// ============================================================================
// CANCEL AND RETRY (Step 8)
// ============================================================================

/**
 * Cancel a single queue entry, removing it from the queue and DOM.
 * Queued: removes immediately. Uploading: aborts XHR. Normalizing: closes SSE, POSTs cancel.
 * @param {string} entryId
 */
async function cancelEntry(entryId) {
    const entry = uploadQueue.find(e => e.id === entryId);
    if (!entry) {
        return;
    }

    if (entry.status === 'queued') {
        removeFromQueue(entryId);

        return;
    }

    if (entry.status === 'uploading') {
        if (entry.xhr) {
            entry.xhr.abort();
        }

        return;
    }

    if (entry.status === 'normalizing') {
        const jobId = entry.jobId;

        if (entry.eventSource) {
            entry.eventSource.close();
            entry.eventSource = null;
        }

        entry.status = 'cancelled';
        if (!entry.backgroundMonitoring) {
            progressAnimator.reset();
        }
        removeQueueItemFromDOM(entryId);
        uploadQueue.splice(uploadQueue.indexOf(entry), 1);

        saveQueueState();

        // Force-resolve the monitoring promise so the promise chain completes
        if (entry._resolveMonitor) {
            entry._resolveMonitor();
            entry._resolveMonitor = null;
        }

        checkAllComplete();

        // Fire-and-forget cancel request to server
        if (jobId) {
            try {
                await fetch('/api/jobs/' + jobId + '/cancel', {
                    method: 'POST',
                    headers: { 'X-API-Key': apiKey }
                });
            } catch {
                // Best-effort
            }
        }
    }
}

/**
 * Retry a failed or cancelled entry. Resets it to queued and moves to end of queue.
 * @param {string} entryId
 */
function retryEntry(entryId) {
    const entry = uploadQueue.find(e => e.id === entryId);
    if (!entry) {
        return;
    }
    if (entry.status !== 'failed' && entry.status !== 'cancelled') {
        return;
    }
    if (!entry.file) {
        return;
    }

    entry.status = 'queued';
    entry.progress = 0;
    entry.stage = null;
    entry.jobId = null;
    entry.episodeId = null;
    entry.episode = null;
    entry.error = null;
    entry.xhr = null;
    entry.eventSource = null;
    entry._resolveMonitor = null;

    // Move to end of queue
    const index = uploadQueue.indexOf(entry);
    if (index > -1) {
        uploadQueue.splice(index, 1);
        uploadQueue.push(entry);
    }

    updateQueueItemInDOM(entry);
    updateQueueTitle();

    saveQueueState();

    if (!isUploading) {
        processQueue();
    }
}

// ============================================================================
// QUEUE DOM RENDERING (Step 7)
// ============================================================================

/**
 * Render the full queue list into #queue-list. Newest items appear at the top.
 * @param {boolean} [animateNew=false] - Whether to animate newly added items with blur-fade-in.
 */
function renderQueueList(animateNew) {
    const container = document.getElementById('queue-list');
    if (!container) {
        return;
    }
    const existingIds = new Set(Array.from(container.children).map(el => el.id));
    container.innerHTML = '';

    for (const entry of uploadQueue) {
        const el = createQueueItemElement(entry);
        if (animateNew && !existingIds.has('queue-item-' + entry.id)) {
            el.style.animation = 'blur-fade-in 0.3s ease both';
        }
        container.prepend(el);
    }
}

/**
 * Create a queue item DOM element for a given entry.
 * @param {QueueEntry} entry
 * @returns {HTMLElement}
 */
function createQueueItemElement(entry) {
    const item = document.createElement('div');
    item.className = 'queue-item queue-item--' + entry.status;
    item.id = 'queue-item-' + entry.id;

    // Icon
    const icon = document.createElement('span');
    icon.className = 'queue-item-icon ' + getIconClass(entry);
    icon.textContent = getIconText(entry);
    item.appendChild(icon);

    // Filename
    const name = document.createElement('span');
    name.className = 'queue-item-name';
    name.textContent = entry.fileName;
    name.title = entry.fileName;
    item.appendChild(name);

    // Status text
    const status = document.createElement('span');
    status.className = 'queue-item-status';
    status.id = 'queue-status-' + entry.id;
    status.textContent = getStatusText(entry);
    item.appendChild(status);

    // Thin progress bar at bottom
    const progressWrap = document.createElement('div');
    progressWrap.className = 'queue-item-progress-wrap';
    const progressBar = document.createElement('div');
    progressBar.className = 'queue-item-progress';
    progressBar.id = 'queue-progress-' + entry.id;

    if (entry.status === 'uploading') {
        progressBar.style.width = entry.progress + '%';
    } else if (entry.status === 'normalizing') {
        if (entry.stage && !['Analyzing', 'Normalizing'].includes(entry.stage)) {
            progressBar.classList.add('indeterminate');
        } else {
            progressBar.style.width = entry.progress + '%';
        }
    }

    progressWrap.appendChild(progressBar);
    item.appendChild(progressWrap);

    // Action button (grid column 4, symmetric with icon)
    const actionBtn = createActionButton(entry);
    if (actionBtn) {
        item.appendChild(actionBtn);
    }

    return item;
}

/**
 * Get the CSS class for a queue item icon.
 * @param {QueueEntry} entry
 * @returns {string}
 */
function getIconClass(entry) {
    switch (entry.status) {
        case 'uploading':
        case 'normalizing':
            return 'queue-item-icon--active';
        case 'completed':
            return 'queue-item-icon--done';
        case 'failed':
            return 'queue-item-icon--failed';
        case 'cancelled':
            return 'queue-item-icon--cancelled';
        default:
            return 'queue-item-icon--queued';
    }
}

/**
 * Get the icon character for a queue item.
 * @param {QueueEntry} entry
 * @returns {string}
 */
function getIconText(entry) {
    switch (entry.status) {
        case 'uploading':
        case 'normalizing':
            return '\u25CF'; // ●
        case 'completed':
            return '\u2713'; // ✓
        case 'failed':
            return '\u2717'; // ✗
        default:
            return '\u2013'; // –
    }
}

/**
 * Get the status text for a queue item.
 * @param {QueueEntry} entry
 * @returns {string}
 */
function getStatusText(entry) {
    switch (entry.status) {
        case 'uploading':
            return 'Uploading';
        case 'normalizing':
            return entry.stage || 'Queued';
        case 'completed':
            return 'Done';
        case 'failed':
            return entry.error || 'Failed';
        case 'cancelled':
            return 'Cancelled';
        default:
            return 'Waiting';
    }
}

/**
 * Create the action button for a queue item (remove/cancel/retry).
 * @param {QueueEntry} entry
 * @returns {HTMLElement|null}
 */
function createActionButton(entry) {
    if (entry.status === 'queued') {
        const btn = document.createElement('button');
        btn.className = 'queue-item-action queue-item-action--cancel';
        btn.type = 'button';
        btn.title = 'Remove from queue';
        btn.textContent = '\u00D7'; // ×
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            removeFromQueue(entry.id);
        });

        return btn;
    }

    if (entry.status === 'uploading' || entry.status === 'normalizing') {
        const btn = document.createElement('button');
        btn.className = 'queue-item-action queue-item-action--cancel';
        btn.type = 'button';
        btn.title = 'Cancel';
        btn.textContent = '\u00D7'; // ×
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            void cancelEntry(entry.id);
        });

        return btn;
    }

    if (entry.status === 'failed') {
        const dismissBtn = document.createElement('button');
        dismissBtn.className = 'queue-item-action queue-item-action--cancel';
        dismissBtn.type = 'button';
        dismissBtn.title = 'Dismiss';
        dismissBtn.textContent = '\u00D7'; // ×
        dismissBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            dismissEntry(entry.id);
        });

        if (!entry.validationError && entry.file) {
            const wrapper = document.createElement('span');
            wrapper.className = 'queue-item-actions';

            const retryBtn = document.createElement('button');
            retryBtn.className = 'queue-item-action queue-item-action--retry';
            retryBtn.type = 'button';
            retryBtn.textContent = 'Retry';
            retryBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                retryEntry(entry.id);
            });
            wrapper.appendChild(retryBtn);
            wrapper.appendChild(dismissBtn);

            return wrapper;
        }

        return dismissBtn;
    }

    return null;
}

/**
 * Replace a queue item element in the DOM with an updated version.
 * @param {QueueEntry} entry
 */
function updateQueueItemInDOM(entry) {
    const existingEl = document.getElementById('queue-item-' + entry.id);
    if (!existingEl) {
        return;
    }
    existingEl.replaceWith(createQueueItemElement(entry));
}

/**
 * Lightweight progress-only update for a queue item (status text only).
 * @param {QueueEntry} entry
 */
function updateQueueItemProgress(entry) {
    const statusEl = document.getElementById('queue-status-' + entry.id);
    if (statusEl) {
        statusEl.textContent = getStatusText(entry);
    }
}

/**
 * Remove a queue item element from the DOM.
 * @param {string} entryId
 */
function removeQueueItemFromDOM(entryId) {
    const el = document.getElementById('queue-item-' + entryId);
    if (el) {
        el.remove();
    }
}

/**
 * Remove completed and cancelled entries older than 1 hour from the upload queue.
 * Called when the tab regains focus or the PWA resumes, so stale terminal entries
 * don't linger until a full page refresh. Recent entries are kept so the user can
 * see completions that happened while the tab was inactive.
 * Adds cleared jobIds to dismissedJobIds to prevent mergeServerJobs from re-adding them.
 */
function clearTerminalEntries() {
    const oneHourAgo = Date.now() - 60 * 60 * 1000;
    const terminalEntries = uploadQueue.filter(e =>
        (e.status === 'completed' || e.status === 'cancelled') && e.startedAt < oneHourAgo
    );
    if (terminalEntries.length === 0) {
        return;
    }
    for (const entry of terminalEntries) {
        if (entry.jobId) {
            dismissedJobIds.add(entry.jobId);
        }
        if (entry.eventSource) {
            entry.eventSource.close();
            entry.eventSource = null;
        }
        if (entry._resolveMonitor) {
            entry._resolveMonitor();
            entry._resolveMonitor = null;
        }
        removeQueueItemFromDOM(entry.id);
        const idx = uploadQueue.indexOf(entry);
        if (idx !== -1) {
            uploadQueue.splice(idx, 1);
        }
    }
    saveQueueState();
    checkAllComplete();
}

/**
 * Get the progress bar element for a queue entry.
 * @param {string} entryId
 * @returns {HTMLElement|null}
 */
function getEntryProgressBar(entryId) {
    return document.getElementById('queue-progress-' + entryId);
}

/**
 * Re-bind progressAnimator's progress bar reference after a full DOM rebuild
 * (e.g. renderQueueList). Without this, the animator updates a detached element.
 */
function rebindProgressAnimator() {
    if (activeUploadId && progressAnimator.progressBar) {
        progressAnimator.progressBar = getEntryProgressBar(activeUploadId);
    }
}

// ============================================================================
// SESSION PERSISTENCE (Step 10)
// ============================================================================

/**
 * Save queue state to localStorage (omits File objects and XHR/EventSource refs).
 */
function saveQueueState() {
    try {
        const serialized = uploadQueue.map(e => ({
            id: e.id,
            fileName: e.fileName,
            fileSize: e.fileSize,
            status: e.status,
            progress: e.progress,
            stage: e.stage,
            jobId: e.jobId,
            episodeId: e.episodeId,
            episode: e.episode,
            error: e.error,
            validationError: e.validationError,
            startedAt: e.startedAt
        }));
        localStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(serialized));
    } catch (e) {
        // Ignore
    }
}

/**
 * Clear in-memory queue and persisted queue state from localStorage.
 */
function clearQueueState() {
    uploadQueue = [];
    try {
        localStorage.removeItem(QUEUE_STORAGE_KEY);
    } catch (e) {
        // Ignore
    }
}


/**
 * Connect to a local file server SSE and fetch initial files.
 * Called after API key validation succeeds when local source params are present in the URL fragment.
 * Idempotent: if already connected (localSourceEvents is set), returns immediately.
 * This matters because init() can be re-invoked via the hashchange listener.
 */
async function connectLocalSource() {
    if (!localSourceConfig || localSourceEvents) {
        return;
    }

    const { host, token } = localSourceConfig;
    const baseUrl = `http://${host}`;

    // Restore seen indices from sessionStorage (survives reloads)
    try {
        const stored = sessionStorage.getItem('localSourceSeen');
        if (stored) {
            localSourceSeen = new Set(JSON.parse(stored));
        }
    } catch { /* ignore corrupt data */ }

    // Fetch initial file list in parallel, skipping files already seen (fetched, cancelled, or completed).
    // Parallel fetch + single addFilesToQueue call avoids staggered additions that interleave
    // with upload status changes and cause visual jumpiness.
    try {
        const resp = await fetch(`${baseUrl}/api/files?token=${token}`);
        if (!resp.ok) {
            throw new Error(`HTTP ${resp.status}`);
        }
        const files = await resp.json();
        const unseen = files.map((f, i) => ({ index: i, name: f.name })).filter(f => !localSourceSeen.has(f.index));

        if (unseen.length > 0) {
            const fetched = await Promise.all(unseen.map(({ index, name }) =>
                fetch(`${baseUrl}/api/files/${index}?token=${token}`)
                    .then(r => r.ok ? r.blob() : null)
                    .then(blob => {
                        if (!blob) return null;
                        localSourceSeen.add(index);

                        return new File([blob], name, { type: blob.type });
                    })
                    .catch(() => null)
            ));
            const validFiles = fetched.filter(f => f !== null);
            if (validFiles.length > 0) {
                addFilesToQueue(validFiles);
            }
            sessionStorage.setItem('localSourceSeen', JSON.stringify([...localSourceSeen]));
        }
    } catch (e) {
        console.error('Failed to fetch local files:', e);
    }

    // Batch handler for SSE events — collects rapid-fire events (e.g., multiple context menu
    // files arriving within milliseconds) and processes them together in a single addFilesToQueue
    // call, avoiding per-file DOM mutations and animation interruptions.
    let pendingFiles = [];
    let batchTimeout = null;

    async function processPendingLocalFiles() {
        batchTimeout = null;
        const batch = pendingFiles.splice(0);
        if (batch.length === 0) {
            return;
        }

        try {
            const fetched = await Promise.all(batch.map(({ index, name }) =>
                fetch(`${baseUrl}/api/files/${index}?token=${token}`)
                    .then(r => r.ok ? r.blob() : null)
                    .then(blob => {
                        if (!blob) return null;
                        localSourceSeen.add(index);

                        return new File([blob], name, { type: blob.type });
                    })
                    .catch(() => null)
            ));
            const validFiles = fetched.filter(f => f !== null);
            if (validFiles.length > 0) {
                addFilesToQueue(validFiles);
            }
            sessionStorage.setItem('localSourceSeen', JSON.stringify([...localSourceSeen]));
        } catch (e) {
            console.error('Failed to fetch local files:', e);
        }
    }

    // Connect SSE for new files (server replays existing files on connect, so skip seen indices)
    localSourceEvents = new EventSource(`${baseUrl}/api/events?token=${token}`);
    localSourceEvents.addEventListener('new-file', (event) => {
        try {
            const data = JSON.parse(event.data);
            if (!localSourceSeen.has(data.index)) {
                pendingFiles.push(data);
                if (!batchTimeout) {
                    batchTimeout = setTimeout(processPendingLocalFiles, 50);
                }
            }
        } catch (e) {
            console.error('Failed to process local source SSE event:', e);
        }
    });

    localSourceEvents.onerror = () => {
        // EventSource fires onerror on temporary disconnects and auto-reconnects.
        // Only close permanently when the connection is truly dead (CLOSED state).
        if (localSourceEvents && localSourceEvents.readyState === EventSource.CLOSED) {
            localSourceEvents.close();
            localSourceEvents = null;
            sessionStorage.removeItem('localSource');
            sessionStorage.removeItem('localSourceSeen');
            localSourceSeen.clear();
        }
    };
}

/**
 * Check for files shared via the PWA Share Target API.
 * The service worker stores shared files in the 'share-target' cache.
 * Consumes all pending files and adds them to the upload queue.
 * @returns {Promise<boolean>} True if shared files were found and queued
 */
async function consumeSharedFiles() {
    if (!('caches' in window)) {
        return false;
    }

    const cache = await caches.open('share-target');
    const keys = await cache.keys();
    if (keys.length === 0) {
        return false;
    }

    const files = [];
    for (const request of keys) {
        const response = await cache.match(request);
        const blob = await response.blob();
        const name = new URL(request.url).pathname.split('/shared/')[1]?.replace(/^\d+-[a-z0-9]+-/, '') || 'shared-audio';
        files.push(new File([blob], name, { type: blob.type }));
        await cache.delete(request);
    }

    addFilesToQueue(files);

    return true;
}

/**
 * Restore queue state from localStorage.
 * Filters out entries older than 1 hour. Entries without startedAt (pre-migration) are treated as expired.
 * @returns {Promise<boolean>} True if state was restored
 */
async function restoreQueueState() {
    const saved = localStorage.getItem(QUEUE_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const entries = tryParseJson(saved);
    if (!entries || !Array.isArray(entries) || entries.length === 0) {
        clearQueueState();

        return false;
    }

    // Filter out entries older than 1 hour (or missing startedAt — pre-migration)
    const oneHourAgo = Date.now() - 60 * 60 * 1000;
    const recentEntries = entries.filter(e => e.startedAt && e.startedAt >= oneHourAgo);
    if (recentEntries.length === 0) {
        clearQueueState();

        return false;
    }

    // Rebuild queue (File/XHR/EventSource are not serializable)
    uploadQueue = recentEntries.map(e => ({
        ...e,
        file: null,
        xhr: null,
        eventSource: null,
        _resolveMonitor: null
    }));

    // Mark uploading entries as failed (can't resume XHR), queued as cancelled (files lost on reload)
    for (const entry of uploadQueue) {
        if (entry.status === 'uploading') {
            entry.status = 'failed';
            entry.error = 'Upload interrupted';
        } else if (entry.status === 'queued') {
            entry.status = 'cancelled';
        }
    }

    // Remove cancelled entries (nothing useful to show for lost files)
    uploadQueue = uploadQueue.filter(e => e.status !== 'cancelled');
    if (uploadQueue.length === 0) {
        clearQueueState();

        return false;
    }

    showState('queue');
    renderQueueList();
    void initHistorySection();

    // Reconnect SSE for normalizing entries
    for (const entry of uploadQueue.filter(e => e.status === 'normalizing' && e.jobId)) {
        monitorEntryNormalizationInBackground(entry);
    }

    // Update title based on whether there's active work
    updateQueueTitle();

    return true;
}

/**
 * Open (or reconnect) the feed-level SSE connection for cross-tab/cross-device sync.
 * Listens for "job-added" (merges new jobs into the local queue) and "episode-added"
 * (refreshes the history panel so new uploads appear without a page reload).
 * If the connection is permanently closed (e.g., server error), sets feedEventsSource to null
 * so it can be reconnected on the next tab reactivation.
 */
function connectFeedEvents() {
    if (feedEventsSource) {
        feedEventsSource.close();
    }
    feedEventsSource = new EventSource('/api/feeds/' + FEED_ID + '/events');
    feedEventsSource.addEventListener('job-added', () => {
        fetchRecentJobs().then(mergeServerJobs).catch(() => {});
    });
    feedEventsSource.addEventListener('episode-added', () => {
        refreshHistoryList();
    });
    feedEventsSource.onerror = () => {
        // readyState CLOSED (2) means the browser won't auto-reconnect (e.g., server returned error).
        // Reconnect on the next tab reactivation via the visibilitychange handler.
        if (feedEventsSource && feedEventsSource.readyState === 2) {
            feedEventsSource.close();
            feedEventsSource = null;
        }
    };
}

/**
 * Fetch recent normalization jobs (last hour) for this feed from the server.
 * Returns all jobs including terminal ones within the time window.
 * Returns null on any error (silent fallback to local-only state).
 * @returns {Promise<Array<Object>|null>} Array of JobStatusResponse objects, or null on error
 */
async function fetchRecentJobs() {
    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/jobs?since=1h', {
            headers: { 'X-API-Key': apiKey }
        });
        if (!response.ok) {
            return null;
        }

        return await response.json();
    } catch (err) {
        return null;
    }
}

/**
 * Merge server-known recent jobs into the local upload queue.
 * The server response contains ALL recent jobs (active + terminal from last hour).
 * Reconciles local state with server data:
 * - Removes stale local entries (normalizing/failed with jobId not in server response)
 * - Updates existing entries with server data (stage, progress, terminal status)
 * - Adds new entries for unknown server jobs (e.g., from CLI or another tab), skipping cancelled jobs,
 *   user-dismissed jobs, and jobs whose fileName matches a currently-uploading entry (dedup for in-flight uploads)
 * If serverJobs is null (fetch failed), skips reconciliation entirely.
 * @param {Array<Object>|null} serverJobs - Array of JobStatusResponse objects, or null on error
 */
function mergeServerJobs(serverJobs) {
    if (serverJobs === null) {
        return; // Server fetch failed — don't reconcile, let SSE handle it
    }

    const serverJobMap = new Map(serverJobs.map(j => [j.jobId, j]));
    const existingJobIds = new Set(uploadQueue.map(e => e.jobId).filter(Boolean));
    const changedEntryIds = new Set();
    let removedEntries = false;

    // Remove stale local entries (normalizing/failed with jobId not in server response —
    // means job was cleaned up or is older than 1 hour)
    const staleEntries = uploadQueue.filter(e => e.jobId && (e.status === 'normalizing' || e.status === 'failed') && !serverJobMap.has(e.jobId));
    for (const stale of staleEntries) {
        if (stale.eventSource) {
            stale.eventSource.close();
            stale.eventSource = null;
        }
        removeQueueItemFromDOM(stale.id);
        uploadQueue.splice(uploadQueue.indexOf(stale), 1);
        if (stale._resolveMonitor) {
            stale._resolveMonitor();
            stale._resolveMonitor = null;
        }
        removedEntries = true;
    }

    // Update existing entries with fresh server data (track which entries actually changed
    // to avoid unnecessary DOM replacements that interrupt blur-fade-in animations)
    for (const serverJob of serverJobs) {
        const existing = uploadQueue.find(e => e.jobId === serverJob.jobId);
        if (!existing) {
            continue;
        }

        const serverStatus = serverJob.status;
        const isServerTerminal = serverStatus === 'Completed' || serverStatus === 'Failed' || serverStatus === 'Cancelled';

        if (existing.status === 'normalizing' && serverStatus === 'Cancelled') {
            // Server says cancelled — remove from queue (consistent with other cancel paths)
            if (existing.eventSource) {
                existing.eventSource.close();
                existing.eventSource = null;
            }
            existing.status = 'cancelled';
            removeQueueItemFromDOM(existing.id);
            uploadQueue.splice(uploadQueue.indexOf(existing), 1);
            if (existing._resolveMonitor) {
                existing._resolveMonitor();
                existing._resolveMonitor = null;
            }
            removedEntries = true;
        } else if (existing.status === 'normalizing' && isServerTerminal) {
            // Server says terminal but local still normalizing — update to terminal
            if (existing.eventSource) {
                existing.eventSource.close();
                existing.eventSource = null;
            }
            existing.status = serverStatus === 'Completed' ? 'completed' : 'failed';
            existing.error = serverJob.error || null;
            existing.episodeId = serverJob.episodeId || existing.episodeId;
            existing.stage = serverJob.stage || existing.stage;
            existing.progress = 100;
            if (existing._resolveMonitor) {
                existing._resolveMonitor();
                existing._resolveMonitor = null;
            }
            changedEntryIds.add(existing.id);
        } else if (existing.status === 'normalizing') {
            const newStage = serverJob.stage || existing.stage;
            const newProgress = serverJob.progressPercent ?? existing.progress;
            if (existing.stage !== newStage || existing.progress !== newProgress) {
                existing.stage = newStage;
                existing.progress = newProgress;
                changedEntryIds.add(existing.id);
            }
        } else if (existing.status === 'failed' && !isServerTerminal) {
            // Recover failed entries that the server says are still active
            existing.status = 'normalizing';
            existing.stage = serverJob.stage || 'Queued';
            existing.progress = serverJob.progressPercent ?? 0;
            existing.error = null;
            changedEntryIds.add(existing.id);
            monitorEntryNormalizationInBackground(existing);
        }
    }

    // Add new entries for server jobs not in local queue
    // Also skip: jobs whose fileName matches a currently-uploading entry (dedup for in-flight uploads
    // where the SSE event arrives before the XHR 202 response sets the jobId), and jobs the user
    // dismissed (cancel POST may still be in flight)
    const uploadingFileNames = new Set(uploadQueue.filter(e => e.status === 'uploading').map(e => e.fileName));
    const newEntries = [];
    for (const serverJob of serverJobs) {
        if (!existingJobIds.has(serverJob.jobId) && !uploadingFileNames.has(serverJob.fileName) && !dismissedJobIds.has(serverJob.jobId)) {
            const serverStatus = serverJob.status;
            const isServerTerminal = serverStatus === 'Completed' || serverStatus === 'Failed' || serverStatus === 'Cancelled';
            if (serverStatus === 'Cancelled') {
                continue;
            }
            newEntries.push({
                id: generateEntryId(),
                file: null,
                status: isServerTerminal ? (serverStatus === 'Completed' ? 'completed' : 'failed') : 'normalizing',
                progress: serverJob.progressPercent ?? (isServerTerminal ? 100 : 0),
                stage: serverJob.stage || 'Queued',
                jobId: serverJob.jobId,
                episodeId: serverJob.episodeId || null,
                episode: null,
                error: serverJob.error || null,
                xhr: null,
                eventSource: null,
                fileSize: 0,
                fileName: serverJob.fileName || 'Unknown',
                validationError: false,
                backgroundMonitoring: false,
                startedAt: serverJob.queuedAt ? new Date(serverJob.queuedAt).getTime() : Date.now(),
                _resolveMonitor: null
            });
        }
    }

    if (newEntries.length === 0 && changedEntryIds.size === 0 && !removedEntries) {
        return;
    }

    uploadQueue.push(...newEntries);

    const currentState = getCurrentState();

    if (newEntries.length > 0 && currentState === 'ready') {
        // Switch to queue state to show server-discovered jobs
        showState('queue');
        renderQueueList();
        void initHistorySection();
    } else if (newEntries.length > 0 && currentState === 'queue') {
        // New entries to add — must rebuild the full list
        renderQueueList(true);
        rebindProgressAnimator();
    } else if (changedEntryIds.size > 0 && currentState === 'queue') {
        // Only update entries that actually changed — avoids replacing all DOM elements
        // which would interrupt blur-fade-in animations on recently-added items
        for (const entryId of changedEntryIds) {
            if (entryId !== activeUploadId) {
                const entry = uploadQueue.find(e => e.id === entryId);
                if (entry) {
                    updateQueueItemInDOM(entry);
                }
            }
        }
    }

    // Start SSE monitoring for new normalizing entries only
    for (const entry of newEntries) {
        if (entry.status === 'normalizing') {
            monitorEntryNormalizationInBackground(entry);
        }
    }

    updateQueueTitle();
    saveQueueState();
    checkAllComplete();
}

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);
window.addEventListener('beforeunload', () => {
    if (localSourceEvents) {
        localSourceEvents.close();
        localSourceEvents = null;
    }
    if (feedEventsSource) {
        feedEventsSource.close();
        feedEventsSource = null;
    }
});

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
        if (progressAnimator.currentStage) {
            progressAnimator.awaitingFirstUpdate = true;
            progressAnimator.isRestoring = true;
        }
        // Reconnect feed events SSE if it was permanently closed while tab was inactive
        if (apiKey && !feedEventsSource) {
            connectFeedEvents();
        }
        // Clear completed/cancelled entries on tab focus
        clearTerminalEntries();
        // Catch any events missed while tab was inactive (browsers may throttle/disconnect SSE)
        if (apiKey) {
            fetchRecentJobs().then(mergeServerJobs).catch(() => {});
            // Only refresh history here if the queue is still showing — when clearTerminalEntries
            // empties the queue, checkAllComplete already calls initHistorySection
            if (uploadQueue.length > 0) {
                refreshHistoryList().catch(() => {});
            }
        }
    }
});

// History section event listeners
// Toggle button - toggles between expanded/collapsed
document.getElementById('history-toggle')?.addEventListener('click', () => {
    const toggle = document.getElementById('history-toggle');
    if (toggle?.getAttribute('aria-expanded') === 'true' && historyPanelPushedState) {
        // Close via history.back() so the popstate handler does the actual collapse
        history.back();
    } else {
        toggleHistorySection();
    }
});

document.querySelectorAll('#history-section .filter-tab').forEach(tab => {
    tab.addEventListener('click', () => {
        const filter = tab.dataset.filter;
        if (filter) {
            void changeHistoryFilter(filter);
        }
    });
});

// Scroll event for fade mask
document.getElementById('history-list')?.addEventListener('scroll', updateHistoryListScrollState);

// Global keyboard shortcuts for history panel
document.addEventListener('keydown', (e) => {
    const section = document.getElementById('history-section');
    if (!section?.classList.contains('history-section--expanded')) {
        return;
    }

    // Escape to close
    if (e.key === 'Escape') {
        e.preventDefault();
        if (historyPanelPushedState) {
            // Close via history.back() so the popstate handler does the actual collapse
            history.back();
        } else {
            toggleHistorySection(false);
        }

        return;
    }

    // Left/Right arrows or Q/E to switch filter tabs
    if (e.key === 'ArrowLeft' || e.key === 'q' || e.key === 'Q' ||
        e.key === 'ArrowRight' || e.key === 'e' || e.key === 'E') {
        const filters = ['local', 'browser', 'all'];
        const currentIndex = filters.indexOf(historyFilter);
        let newIndex;

        if (e.key === 'ArrowLeft' || e.key === 'q' || e.key === 'Q') {
            newIndex = Math.max(currentIndex - 1, 0);
        } else {
            newIndex = Math.min(currentIndex + 1, filters.length - 1);
        }

        if (newIndex !== currentIndex) {
            e.preventDefault();
            void changeHistoryFilter(filters[newIndex], true);
        }

        return;
    }

    // Up/Down arrows to navigate history list
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (!historyData || historyData.length === 0) {
            return;
        }

        const currentIndex = historyData.findIndex(u => u.id === historySelectedId);
        let newIndex;

        if (e.key === 'ArrowDown') {
            newIndex = Math.min(currentIndex + 1, historyData.length - 1);
        } else {
            newIndex = Math.max(currentIndex - 1, 0);
        }

        if (newIndex !== currentIndex) {
            e.preventDefault();
            selectHistoryUpload(historyData[newIndex].id, true);
        }
    }
});

// Browser back button closes history panel instead of navigating away
window.addEventListener('popstate', () => {
    if (historyPanelPushedState) {
        historyPanelPushedState = false;
        const toggle = document.getElementById('history-toggle');
        if (toggle?.getAttribute('aria-expanded') === 'true') {
            toggleHistorySection(false);
        }
    }
});

