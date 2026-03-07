const FEED_ID = '{{FEED_ID}}';
const IS_DEV = '{{IS_DEV}}' === 'true';
const PROGRESS_SMOOTHING = '{{PROGRESS_SMOOTHING}}' === 'true';
const SHOW_GHOST = IS_DEV && window.location.search.includes('ghost');
const DEBUG_TITLE_ANIMATION = IS_DEV && window.location.search.includes('alive');
const VELOCITY_OVERRIDES = IS_DEV ? parseVelocityOverrides() : {};
const ALLOWED_EXTENSIONS = ['.mp3', '.m4a', '.wav', '.ogg', '.flac', '.aac'];

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
 * @property {Function|null} _resolveMonitor - Internal: resolve function for normalization promise
 */

let apiKey = null;
const states = ['no-key', 'ready', 'queue', 'batch-complete', 'error'];
const JOB_STORAGE_KEY = 'featherpod_job_' + FEED_ID;
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
            textSpan.textContent = 'Recent uploads';
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
        targetWord = 'Pushing';
    } else if (stateName === 'batch-complete') {
        targetWord = 'Pushed';
    } else {
        targetWord = 'Push';
    }

    // Debug mode: set starting word before comparison so animation triggers
    if (isFirstStateChange && DEBUG_TITLE_ANIMATION) {
        let startWord;
        if (stateName === 'queue') {
            startWord = 'Push';
        } else if (stateName === 'batch-complete' || stateName === 'error') {
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

/** @param {File} file */
function isValidAudioFile(file) {
    const extension = '.' + file.name.split('.').pop().toLowerCase();

    return ALLOWED_EXTENSIONS.includes(extension);
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


/** @type {number} - Cached container width for consistent animations */
let cachedContainerWidth = 0;
/** @type {number} - Cached collapsed margin for consistent animations */
let cachedCollapsedMargin = 0;
/** @type {number} - Width of the collapsed drop zone / history section */
const COLLAPSED_WIDTH = 500;
/** @type {number} - Height of the collapsed drop zone / history section */
const COLLAPSED_HEIGHT = 280;

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

async function init() {
    // Cache layout dimensions for consistent animations
    cacheLayoutDimensions();
    // Recalculate on resize
    window.addEventListener('resize', cacheLayoutDimensions);

    // Show validating state while we check the key
    showState('no-key');
    setNoKeyValidating(true);

    /**
     * Show the no-key UI with an optional error state.
     * @param {'invalid'|'no-access'|null} [errorType=null] - Type of error to display
     */
    function showNoKeyUI(errorType = null) {
        if (errorType) {
            clearApiKey();
        }
        setNoKeyValidating(false);
        setNoKeyError(errorType);
        initNoKeyState();
    }

    /**
     * Try to validate and use a fallback key when the primary key fails.
     * @param {string|null} fallbackKey - The fallback key to try
     * @param {string} warningMessage - Message to show if fallback succeeds
     * @param {'invalid'|'no-access'} errorType - Error type if both keys fail
     * @returns {Promise<boolean>} True if fallback succeeded, false if should show no-key UI
     */
    async function tryFallbackKey(fallbackKey, warningMessage, errorType) {
        if (!fallbackKey) {
            showNoKeyUI(errorType);

            return false;
        }

        const fallbackValidation = await validateApiKey(fallbackKey);
        if (fallbackValidation.valid && fallbackValidation.feedAccess) {
            showWarningBanner(warningMessage);
            saveApiKey(fallbackKey);

            return true;
        }

        showNoKeyUI(errorType);

        return false;
    }

    // Storage precedence: fragment > sessionStorage > localStorage > cookie
    const fragment = window.location.hash.slice(1);
    const storedKey = getStoredApiKey();

    if (fragment) {
        // Clear fragment from URL immediately for cleaner UX
        history.replaceState(null, '', window.location.pathname + window.location.search);

        const validation = await validateApiKeyWithRetry(fragment);

        if (validation.valid && validation.feedAccess) {
            // Fragment key is valid with feed access
            saveApiKey(fragment);
        } else if (validation.networkError) {
            // Server unreachable - use fragment key optimistically and persist it
            showWarningBanner('Server unreachable \u2014 using URL key');
            saveApiKey(fragment);
        } else if (validation.valid && !validation.feedAccess) {
            // Fragment key is valid but no feed access - try fallback
            if (!await tryFallbackKey(storedKey, 'URL key does not have access to this feed. Using saved key.', 'no-access')) {
                return;
            }
        } else {
            // Fragment key is invalid - try fallback
            if (!await tryFallbackKey(storedKey, 'Invalid URL key. Using saved key.', 'invalid')) {
                return;
            }
        }
    } else if (storedKey) {
        // No fragment, try stored key
        const validation = await validateApiKeyWithRetry(storedKey);

        if (validation.valid && validation.feedAccess) {
            // Stored key is valid - ensure it's in all storage layers
            saveApiKey(storedKey);
        } else if (validation.valid && !validation.feedAccess) {
            showNoKeyUI('no-access');

            return;
        } else if (validation.networkError) {
            // Server unreachable - use stored key optimistically
            showWarningBanner('Server unreachable \u2014 using saved key');
            apiKey = storedKey;
        } else {
            // Stored key is genuinely invalid - show paste UI without error
            showNoKeyUI(null);

            return;
        }
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

    // Try to restore previous queue state (e.g., after page refresh)
    if (await restoreQueueState()) {
        return;
    }

    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
}

/** @type {boolean} - Whether initNoKeyState has already been called (prevents duplicate listeners) */
let noKeyStateInitialized = false;

/**
 * Set the no-key state to validating mode (shows loading spinner, hides other elements).
 * @param {boolean} validating - Whether currently validating
 */
function setNoKeyValidating(validating) {
    const validatingEl = document.getElementById('no-key-validating');
    const contentEl = document.getElementById('no-key-content');
    const noKeyTitleEl = document.getElementById('no-key-title');

    if (validating) {
        validatingEl.style.display = 'block';
        contentEl.style.display = 'none';
        if (noKeyTitleEl) {
            noKeyTitleEl.textContent = 'Validating...';
        }
    } else {
        validatingEl.style.display = 'none';
        contentEl.style.display = 'block';
        if (noKeyTitleEl) {
            noKeyTitleEl.textContent = STR_API_KEY_REQUIRED;
        }
    }
}

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

// Queue state: add more files button and drop zone
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

// Upload more button (batch-complete state)
document.getElementById('upload-more')?.addEventListener('click', async () => {
    uploadQueue = [];
    activeUploadId = null;
    isUploading = false;
    clearQueueState();
    document.getElementById('file-input').value = '';
    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
});

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
    queueDZ.style.height = COLLAPSED_HEIGHT + 'px';

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

    // Commit the start height, then transition to target
    void dropZone.offsetHeight;
    dropZone.style.height = COLLAPSED_HEIGHT + 'px';

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
 * @param {Array<File>} files
 */
function addFilesToQueue(files) {
    if (files.length === 0) {
        return;
    }

    for (const file of files) {
        const isDuplicate = uploadQueue.some(e =>
            e.fileName === file.name &&
            e.fileSize === file.size &&
            (e.status === 'queued' || e.status === 'uploading' || e.status === 'normalizing' || e.status === 'completed')
        );
        if (isDuplicate) continue;

        const valid = isValidAudioFile(file);
        uploadQueue.push({
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
            _resolveMonitor: null
        });
    }

    const previousState = getCurrentState();

    if (previousState !== 'queue') {
        showState('queue');
    }

    if (previousState === 'ready') {
        animateQueueDropZoneMorph();
    }

    const animateItems = previousState === 'queue';
    renderQueueList(animateItems);

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
 * Find the next queued entry and start processing it.
 * If no queued entries remain, transitions to batch-complete.
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
 * Transition to batch-complete state and show summary.
 */
function onBatchComplete() {
    isUploading = false;
    activeUploadId = null;

    // If nothing completed or failed, go back to ready (e.g. everything was cancelled)
    const hasResults = uploadQueue.some(e => e.status === 'completed' || e.status === 'failed');
    if (!hasResults) {
        const queueDZHeight = document.getElementById('queue-drop-zone')?.getBoundingClientRect().height || 0;
        if (queueDZHeight > 0) prepareReadyDropZoneMorph(queueDZHeight);
        clearQueueState();
        showState('ready');
        if (queueDZHeight > 0) animateReadyDropZoneMorph();
        void initHistorySection();

        return;
    }

    saveQueueState();

    // Invalidate caches for history
    cachedBrowserUploads = null;
    cachedAllUploads = null;

    showState('batch-complete');
    renderBatchList();
    updateBatchSummary();
    document.getElementById('upload-more')?.focus();
}

/**
 * Check if all entries have reached a terminal state and transition to batch-complete if so.
 * Called from multiple places where background work finishes (normalization, cancellation).
 */
function checkAllComplete() {
    if (getCurrentState() === 'batch-complete') return;

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
        onBatchComplete();
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

            xhr.open('POST', '/api/feeds/' + FEED_ID + '/episodes?normalize=true&source=Browser');
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            entry.status = 'completed';
            entry.episode = episode;
            entry.progress = 100;
            saveToLocalHistory(episode);
            cachedBrowserUploads = null;
            cachedAllUploads = null;
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
// Step 1: CTA + border fall/blur together
const H_CTA_FALL = 150;
// Step 2: Pause before swap
const H_PAUSE = 100;
// Step 3: History panel morphs
const H_MORPH = 400;
const HISTORY_TRANSITION_DURATION = H_CTA_FALL + H_PAUSE + H_MORPH;

/**
 * Toggle the history section collapsed/expanded state with animation.
 * On desktop: morphs from drop zone size to full width with staggered content reveal.
 * On mobile: slides down as fullscreen overlay.
 * In ready state, drop zone fades out/in via CSS. In queue state, overlays queue content.
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

    // Animate text change: 1) delay, 2) fade out, 3) resize, 4) fade in, 5) end
    const newText = newState ? '← Back' : 'Recent uploads';
    const textSpan = toggle.querySelector('.history-toggle-text');
    const TEXT_FADE = 150;
    const WIDTH_ANIM = 150;

    // On mobile, button is full-width so skip width animation
    if (isMobile) {
        // Simple text swap with fade
        const delay = newState ? H_CTA_FALL + H_PAUSE : 0;
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
        textSpan.textContent = newState ? 'Recent uploads' : '← Back'; // restore old text
        textSpan.style.visibility = '';
        toggle.style.width = currentWidth + 'px';

        // Same cadence in both ready and queue states
        const totalDuration = newState
            ? H_CTA_FALL + H_PAUSE + H_MORPH
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
            section.style.height = COLLAPSED_HEIGHT + 'px';
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
            section.style.height = COLLAPSED_HEIGHT + 'px';
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
 * Cache is invalidated in processEntry() after each upload completes.
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
 * Cache is invalidated in processEntry() after each upload completes.
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
                cachedAllUploads = null;
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
                cachedAllUploads = null;
                updateQueueItemInDOM(entry);

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

    if (getCurrentState() === 'batch-complete') {
        showState('queue');
        renderQueueList();
    
    } else {
        updateQueueItemInDOM(entry);
    
    }

    saveQueueState();

    if (!isUploading) {
        processQueue();
    }
}

// ============================================================================
// QUEUE DOM RENDERING (Step 7)
// ============================================================================

/**
 * Render the full queue list into #queue-list.
 */
/**
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
        container.appendChild(el);
    }
}

/**
 * Render the batch-complete list into #batch-list.
 */
function renderBatchList() {
    const container = document.getElementById('batch-list');
    if (!container) {
        return;
    }
    container.innerHTML = '';

    for (const entry of uploadQueue) {
        if (entry.status === 'cancelled') continue;
        container.appendChild(createQueueItemElement(entry));
    }
}

/**
 * Update the batch summary text.
 */
function updateBatchSummary() {
    const summary = document.getElementById('batch-summary');
    if (!summary) {
        return;
    }

    const completed = uploadQueue.filter(e => e.status === 'completed').length;
    const failed = uploadQueue.filter(e => e.status === 'failed').length;

    const parts = [];
    if (completed > 0) {
        parts.push(completed + ' uploaded');
    }
    if (failed > 0) {
        parts.push(failed + ' failed');
    }

    summary.textContent = parts.join(', ') || 'No files processed';
    summary.style.display = parts.length > 0 ? '' : 'none';
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
            return 'Uploading...';
        case 'normalizing': {
            const stage = entry.stage || 'Queued';
            const ellipsis = stage.endsWith('ing') ? '...' : '';
            return stage + ellipsis;
        }
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
        btn.className = 'queue-item-action queue-item-action--remove';
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

    if (entry.status === 'failed' && !entry.validationError && entry.file) {
        const btn = document.createElement('button');
        btn.className = 'queue-item-action queue-item-action--retry';
        btn.type = 'button';
        btn.textContent = 'Retry';
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            retryEntry(entry.id);
        });

        return btn;
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
 * Get the progress bar element for a queue entry.
 * @param {string} entryId
 * @returns {HTMLElement|null}
 */
function getEntryProgressBar(entryId) {
    return document.getElementById('queue-progress-' + entryId);
}

// ============================================================================
// SESSION PERSISTENCE (Step 10)
// ============================================================================

/**
 * Save queue state to sessionStorage (omits File objects and XHR/EventSource refs).
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
            validationError: e.validationError
        }));
        sessionStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(serialized));
    } catch (e) {
        // Ignore
    }
}

/**
 * Clear in-memory queue and persisted queue state from sessionStorage.
 */
function clearQueueState() {
    uploadQueue = [];
    try {
        sessionStorage.removeItem(QUEUE_STORAGE_KEY);
        sessionStorage.removeItem(JOB_STORAGE_KEY);
    } catch (e) {
        // Ignore
    }
}

/**
 * Restore queue state from sessionStorage.
 * Handles migration from old single-job format.
 * @returns {Promise<boolean>} True if state was restored
 */
async function restoreQueueState() {
    // Migration: check for old job state format
    const oldState = sessionStorage.getItem(JOB_STORAGE_KEY);
    if (oldState) {
        const job = tryParseJson(oldState);
        if (job) {
            sessionStorage.removeItem(JOB_STORAGE_KEY);

            if (job.status === 'success') {
                uploadQueue = [{
                    id: generateEntryId(), file: null,
                    status: 'completed', progress: 100, stage: null,
                    jobId: null, episodeId: null, episode: job.episode || null,
                    error: null, xhr: null, eventSource: null,
                    fileSize: 0, fileName: job.fileName || 'Unknown',
                    validationError: false, _resolveMonitor: null
                }];
                showState('batch-complete');
                renderBatchList();
                updateBatchSummary();

                return true;
            } else if (job.status === 'error') {
                showError(job.error);

                return true;
            } else if (job.status === 'processing') {
                const entry = {
                    id: generateEntryId(), file: null,
                    status: 'normalizing', progress: 0, stage: 'Queued',
                    jobId: job.jobId, episodeId: null, episode: null,
                    error: null, xhr: null, eventSource: null,
                    fileSize: job.fileSize || 0, fileName: job.fileName || 'Unknown',
                    validationError: false, backgroundMonitoring: false, _resolveMonitor: null
                };
                uploadQueue = [entry];
                showState('queue');
                renderQueueList();
                void initHistorySection();

                monitorEntryNormalizationInBackground(entry);

                return true;
            }
        }
    }

    // Restore queue state
    const saved = sessionStorage.getItem(QUEUE_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const entries = tryParseJson(saved);
    if (!entries || !Array.isArray(entries) || entries.length === 0) {
        clearQueueState();

        return false;
    }

    // Rebuild queue (File/XHR/EventSource are not serializable)
    uploadQueue = entries.map(e => ({
        ...e,
        file: null,
        xhr: null,
        eventSource: null,
        _resolveMonitor: null
    }));

    // Mark uploading entries as failed (can't resume XHR)
    for (const entry of uploadQueue) {
        if (entry.status === 'uploading') {
            entry.status = 'failed';
            entry.error = 'Upload interrupted';
        }
    }

    const hasNormalizing = uploadQueue.filter(e => e.status === 'normalizing' && e.jobId);
    const hasQueued = uploadQueue.some(e => e.status === 'queued');

    if (hasNormalizing.length > 0) {
        // Reconnect to all normalizing entries; mark remaining queued entries as cancelled (files lost on reload)
        for (const e of uploadQueue) {
            if (e.status === 'queued') {
                e.status = 'cancelled';
            }
        }
        showState('queue');
        renderQueueList();
        void initHistorySection();
        for (const entry of hasNormalizing) {
            monitorEntryNormalizationInBackground(entry);
        }

        return true;
    } else if (hasQueued) {
        // Files lost on reload — mark queued entries as cancelled
        for (const entry of uploadQueue) {
            if (entry.status === 'queued') {
                entry.status = 'cancelled';
            }
        }
        showState('batch-complete');
        renderBatchList();
        updateBatchSummary();

        return true;
    } else {
        // All terminal
        showState('batch-complete');
        renderBatchList();
        updateBatchSummary();

        return true;
    }
}

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && progressAnimator.currentStage) {
        progressAnimator.awaitingFirstUpdate = true;
        progressAnimator.isRestoring = true;
    }
});

// History section event listeners
// Toggle button - toggles between expanded/collapsed
document.getElementById('history-toggle')?.addEventListener('click', () => {
    toggleHistorySection();
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
        toggleHistorySection(false);

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

