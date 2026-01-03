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
let apiKey = null;
const states = ['no-key', 'ready', 'processing', 'success', 'error'];
const JOB_STORAGE_KEY = 'featherpod_job_' + FEED_ID;
const HISTORY_STORAGE_KEY = 'featherpod_history_' + FEED_ID;
const HISTORY_FILTER_KEY = 'featherpod_history_filter_' + FEED_ID;
const API_KEY_SESSION_KEY = 'featherpod_api_key_' + FEED_ID;
const API_KEY_LOCAL_KEY = 'featherpod_api_key_local_' + FEED_ID;
const MAX_LOCAL_HISTORY = 50;

// No-key state UI strings
const STR_PASTE_KEY_BELOW = 'Paste key below';
const STR_PASTE_KEY = 'Paste here';
const STR_SAVE_KEY = 'Save key';
const STR_API_KEY_REQUIRED = 'API key required';
const STR_INVALID_KEY = 'Invalid key';
const STR_NO_ACCESS = 'No access';
const STR_NO_FEED_ACCESS = 'This key does not have access to this feed';

/** @type {Array<Object>|null} */
let recentUploadsData = null;
/** @type {string|null} */
let selectedUploadId = null;

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

/** @param {string} stateName */
function showState(stateName) {
    states.forEach(s => document.getElementById(s).style.display = s === stateName ? '' : 'none');

    // Update container state class for CSS styling
    const container = document.querySelector('.container');
    if (container) {
        states.forEach(s => container.classList.remove('state-' + s));
        container.classList.add('state-' + stateName);
    }

    // Update page title based on state (animate first word only)
    let targetWord;
    if (stateName === 'processing') {
        targetWord = 'Pushing';
    } else if (stateName === 'success') {
        targetWord = 'Pushed';
    } else {
        targetWord = 'Push';
    }

    // Debug mode: set starting word before comparison so animation triggers
    if (isFirstStateChange && DEBUG_TITLE_ANIMATION) {
        let startWord;
        if (stateName === 'processing') {
            startWord = 'Push';
        } else if (stateName === 'success' || stateName === 'error') {
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
        return { valid: false, user: null, feedAccess: false, error: 'API key is empty' };
    }

    try {
        const response = await fetch('/api/users/me', {
            headers: { 'X-API-Key': key.trim() }
        });

        if (!response.ok) {
            if (response.status === 401) {
                return { valid: false, user: null, feedAccess: false, error: STR_INVALID_KEY };
            }

            return { valid: false, user: null, feedAccess: false, error: 'Validation failed' };
        }

        const user = await response.json();

        // Check feed access: Admin has all, FeedOwner needs feed in ownedFeeds
        const feedAccess = user.role === 'Admin' || (user.role === 'FeedOwner' && user.ownedFeeds && user.ownedFeeds.includes(FEED_ID));

        return { valid: true, user, feedAccess, error: null };
    } catch (err) {
        return { valid: false, user: null, feedAccess: false, error: 'Network error' };
    }
}

/**
 * Save API key to both sessionStorage and localStorage, and set the global apiKey.
 * @param {string} key - The API key to save
 */
function saveApiKey(key) {
    const trimmedKey = key.trim();
    sessionStorage.setItem(API_KEY_SESSION_KEY, trimmedKey);
    localStorage.setItem(API_KEY_LOCAL_KEY, trimmedKey);
    apiKey = trimmedKey;
}

/**
 * Clear API key from both sessionStorage and localStorage.
 */
function clearApiKey() {
    sessionStorage.removeItem(API_KEY_SESSION_KEY);
    localStorage.removeItem(API_KEY_LOCAL_KEY);
    apiKey = null;
}

/**
 * Get stored API key with precedence: sessionStorage > localStorage.
 * @returns {string|null} The stored API key, or null if none found
 */
function getStoredApiKey() {
    return sessionStorage.getItem(API_KEY_SESSION_KEY) || localStorage.getItem(API_KEY_LOCAL_KEY);
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

    // Storage precedence: fragment > sessionStorage > localStorage
    const fragment = window.location.hash.slice(1);
    const storedKey = getStoredApiKey();

    if (fragment) {
        // Clear fragment from URL immediately for cleaner UX
        history.replaceState(null, '', window.location.pathname + window.location.search);

        const validation = await validateApiKey(fragment);

        if (validation.valid && validation.feedAccess) {
            // Fragment key is valid with feed access
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
        const validation = await validateApiKey(storedKey);

        if (validation.valid && validation.feedAccess) {
            // Stored key is valid - ensure it's in both storages
            saveApiKey(storedKey);
        } else if (validation.valid && !validation.feedAccess) {
            showNoKeyUI('no-access');

            return;
        } else {
            // Stored key is invalid - no error message, just show paste UI
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

    // Try to restore previous job state (e.g., after page refresh)
    if (await restoreJobState()) {
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

    // Paste button click handler
    pasteBtn.addEventListener('click', async () => {
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            // Clipboard API not available (requires secure context: HTTPS or localhost)
            morphToTextarea();

            return;
        }

        try {
            pasteBtn.disabled = true;
            pasteBtn.textContent = 'Validating...';

            const clipboardText = await navigator.clipboard.readText();

            if (!clipboardText || clipboardText.trim().length === 0) {
                // Clipboard empty, morph to textarea
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea();

                return;
            }

            // Auto-extract fp_ prefixed key if pasted with surrounding text
            // Format: fp_{userId}_{secret} where secret is 22 chars base64url
            let apiKeyToValidate = clipboardText.trim();
            // Match fp_ key - use lookahead for end boundary to avoid issues with \b and special chars
            const fpKeyMatch = clipboardText.match(/fp_[a-zA-Z0-9-]+_[A-Za-z0-9_-]{22}(?=[^A-Za-z0-9_-]|$)/);
            if (fpKeyMatch) {
                apiKeyToValidate = fpKeyMatch[0];
            }

            const validation = await validateApiKey(apiKeyToValidate);

            if (validation.valid && validation.feedAccess) {
                saveApiKey(apiKeyToValidate);
                await transitionToReadyState();
            } else {
                // Key was invalid or no access - just show textarea without error
                // (user may not realize we already tried their clipboard)
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea();
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

document.getElementById('upload-another').addEventListener('click', async () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
});

document.getElementById('try-another').addEventListener('click', async () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
});

document.getElementById('file-input').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');
        return;
    }
    await uploadFile(file);
});

// Drag and drop support
const dropZone = document.getElementById('drop-zone');

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropZone.classList.add('drag-over');
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
});

dropZone.addEventListener('drop', async (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
    const file = e.dataTransfer.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');
        return;
    }
    await uploadFile(file);
});

/** @param {File} file */
async function uploadFile(file) {
    clearJobState();
    showState('processing');
    document.getElementById('processing-filename').textContent = file.name;
    document.getElementById('processing-status').textContent = 'Uploading...';
    const progressBar = document.getElementById('processing-progress');
    const progressContainer = progressBar.parentElement;
    progressContainer.setAttribute('aria-valuenow', '0');
    progressBar.classList.remove('indeterminate');
    progressAnimator.startWithAssumption('Uploading', progressBar, file.size);
    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhr.upload.addEventListener('progress', (e) => {
                if (e.lengthComputable) {
                    const percent = Math.round((e.loaded / e.total) * 100);
                    progressAnimator.setTarget(percent, 'Uploading');
                    progressContainer.setAttribute('aria-valuenow', percent.toString());
                }
            });
            xhr.onload = () => {
                progressAnimator.reset();
                resolve({ status: xhr.status, body: xhr.responseText });
            };
            xhr.onerror = () => {
                progressAnimator.reset();
                reject(new Error('Network error'));
            };
            xhr.open('POST', '/api/feeds/' + FEED_ID + '/episodes?normalize=true&source=Browser');
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            saveJobState({ status: 'success', fileName: file.name, episode: episode });
            await showSuccess(episode);
        } else if (response.status === 202) {
            const jobResponse = JSON.parse(response.body);
            saveJobState({
                status: 'processing',
                jobId: jobResponse.jobId,
                fileName: file.name,
                fileSize: file.size
            });
            monitorNormalizationJob(jobResponse.jobId, file.name, file.size);
        } else if (response.status === 401) {
            showError(STR_INVALID_KEY);
        } else if (response.status === 403) {
            showError(STR_NO_FEED_ACCESS);
        } else {
            const error = tryParseJson(response.body);
            showError(error?.error || 'Upload failed');
        }
    } catch (err) {
        showError(err.message || 'Upload failed');
    }
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
 * Display the success state and fetch recent uploads.
 * @param {Episode|string} episodeOrFileName - Episode object or just filename string
 */
async function showSuccess(episodeOrFileName) {
    showState('success');

    // Invalidate cache since we just uploaded
    cachedBrowserUploads = null;
    cachedAllUploads = null;

    // Save to localStorage if we have full episode data
    if (typeof episodeOrFileName === 'object' && episodeOrFileName !== null) {
        saveToLocalHistory(episodeOrFileName);
    }

    // Determine the selected episode ID (if we have full episode data)
    const currentEpisodeId = (typeof episodeOrFileName === 'object' && episodeOrFileName !== null)
        ? episodeOrFileName.id
        : null;

    // Update the info card with the current episode
    updateInfoCard(episodeOrFileName);

    // Fetch and display recent uploads (errors shouldn't break success state)
    try {
        const recentUploads = await fetchRecentUploads();
        renderRecentUploads(recentUploads, currentEpisodeId);
    } catch (err) {
        console.error('Failed to display recent uploads:', err);
    }

    document.getElementById('upload-another').focus();
}

/**
 * Fetch recent browser uploads for this feed (uses cache, returns first 5).
 * @returns {Promise<Array<{id: string, title: string, fileName: string, fileSize: number, duration: string, uploadedAt: string|null}>>}
 */
async function fetchRecentUploads() {
    const uploads = await fetchBrowserUploads();

    return uploads.slice(0, 5);
}

/**
 * Fetch the most recent browser upload (used after normalization completes).
 * Invalidates cache to ensure fresh data is fetched.
 * @returns {Promise<Object|null>}
 */
async function fetchMostRecentEpisode() {
    // Invalidate cache to ensure we get fresh data including the just-uploaded episode
    cachedBrowserUploads = null;

    try {
        const uploads = await fetchRecentUploads();
        if (uploads && uploads.length > 0) {
            return uploads[0];
        }

        return null;
    } catch (err) {
        console.warn('Error fetching most recent episode:', err);

        return null;
    }
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

/**
 * Update the episode info card with the given episode data.
 * @param {Episode|string} episode - Episode object or just filename string
 */
function updateInfoCard(episode) {
    const infoCard = document.getElementById('episode-info');
    const fallbackFilename = document.getElementById('ep-filename');

    if (episode && typeof episode === 'object') {
        fallbackFilename.style.display = 'none';
        infoCard.style.display = 'grid';

        document.getElementById('info-title').textContent = episode.title || episode.fileName;
        document.getElementById('info-filename').textContent = episode.fileName || '';

        // Combine duration and size: "16m 40s (31 MB)"
        const duration = formatDuration(episode.duration);
        const size = episode.fileSize ? episode.fileSize.formatBytes() : '';
        let durationText = duration;
        if (duration && size) {
            durationText = duration + ' (' + size + ')';
        } else if (size) {
            durationText = size;
        }
        document.getElementById('info-duration').textContent = durationText;

        document.getElementById('info-published').textContent = formatDate(episode.publishedDate);

        // Uploaded time (shown on mobile only via CSS)
        const uploadedTime = formatRelativeTime(episode.uploadedAt);
        document.getElementById('info-uploaded').textContent = uploadedTime;
        document.getElementById('info-uploaded-label').style.display = uploadedTime ? '' : 'none';
        document.getElementById('info-uploaded').style.display = uploadedTime ? '' : 'none';
    } else {
        infoCard.style.display = 'none';
        fallbackFilename.style.display = 'block';
        fallbackFilename.textContent = episode || '';
    }
}

/**
 * Render the recent uploads list in the success state.
 * @param {Array<Object>} uploads - Array of episode objects
 * @param {string|null} [initialSelectedId] - ID of the initially selected episode
 */
function renderRecentUploads(uploads, initialSelectedId = null) {
    const container = document.getElementById('recent-uploads');
    if (!container) {
        return;
    }

    if (!uploads || !Array.isArray(uploads) || uploads.length === 0) {
        container.style.display = 'none';
        recentUploadsData = null;

        return;
    }

    recentUploadsData = uploads;
    selectedUploadId = initialSelectedId;
    container.style.display = 'block';
    const list = container.querySelector('.upload-list');
    list.innerHTML = '';

    uploads.forEach(upload => {
        const item = document.createElement('div');
        item.className = 'upload-item';
        item.dataset.id = upload.id;
        item.tabIndex = 0;
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', (upload.id === selectedUploadId).toString());

        if (upload.id === selectedUploadId) {
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

        item.addEventListener('click', () => selectUpload(upload.id));

        list.appendChild(item);
    });
}

/**
 * Select an upload from the recent uploads list and update the info card.
 * @param {string} uploadId - The ID of the upload to select
 */
function selectUpload(uploadId) {
    if (!recentUploadsData) {
        return;
    }

    const upload = recentUploadsData.find(u => u.id === uploadId);
    if (!upload) {
        return;
    }

    selectedUploadId = uploadId;

    // Update visual selection and aria-selected (target success state's list specifically)
    const list = document.querySelector('#recent-uploads .upload-list');
    if (list) {
        list.querySelectorAll('.upload-item').forEach(item => {
            const isSelected = item.dataset.id === uploadId;
            item.classList.toggle('upload-item--selected', isSelected);
            item.setAttribute('aria-selected', isSelected.toString());
        });
    }

    // Update info card
    updateInfoCard(upload);

    // Scroll to top so the info card is visible
    window.scrollTo({ top: 0, behavior: 'smooth' });
}

// ============================================================================
// HISTORY SECTION (Ready State)
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
 * Drop zone fades out/in via CSS, history section handles its own animated border.
 * @param {boolean} [expand] - Force expand (true) or collapse (false). If omitted, toggles.
 */
function toggleHistorySection(expand) {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    const selectFileBtn = document.getElementById('select-file');
    const isMobile = window.matchMedia('(max-width: 768px)').matches;
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

        // Calculate timing: work backwards from end
        // Expanding: long delay before text animation (full expand sequence)
        // Collapsing: short animation matching panel shrink (H_MORPH)
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

    // Disable select-file button when expanded (it's hidden via CSS but could still be activated)
    if (selectFileBtn) {
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
        // Expanding: measure natural height, animate to it, then switch to auto

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
        // Drop zone fades out via CSS - no dimension animation needed

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
        // Collapsing: set current state explicitly, then animate to collapsed

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

        // Return focus to the upload button
        if (selectFileBtn) {
            selectFileBtn.focus();
        }
    }
}

/**
 * Fetch and cache browser uploads from server.
 * Cache is invalidated in showSuccess() after each upload.
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
 * Cache is invalidated in showSuccess() after each upload.
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
 * Initialize the history section in the ready state.
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
 * @property {string} status - Queued, Processing, Completed, Failed
 * @property {string} [stage] - Queued, Analyzing, Normalizing, Finishing, Completed, Failed
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

/**
 * Update the processing status display and progress bar.
 * @param {JobStatus} job
 */
function updateProcessingStatus(job) {
    const statusEl = document.getElementById('processing-status');
    const progressBar = document.getElementById('processing-progress');
    const progressContainer = progressBar.parentElement;

    if (job.stage) {
        const stagesWithProgress = ['Analyzing', 'Normalizing'];
        const isProgressStage = stagesWithProgress.includes(job.stage);

        const ellipsis = job.stage.endsWith('ing') ? '...' : '';
        statusEl.textContent = job.stage + ellipsis;

        if (isProgressStage) {
            progressBar.classList.remove('indeterminate');

            if (progressAnimator.currentStage !== job.stage) {
                progressAnimator.startWithAssumption(job.stage, progressBar);
            }

            if (job.progressPercent != null) {
                progressAnimator.setTarget(job.progressPercent, job.stage);
                progressContainer.setAttribute('aria-valuenow', job.progressPercent.toString());
            }

            progressAnimator.start(progressBar);
        } else {
            progressAnimator.reset();
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
            progressContainer.setAttribute('aria-valuenow', '0');

            // Show ghost bar at 100% during indeterminate stages to prevent layout jump
            if (SHOW_GHOST) {
                const ghostBar = document.getElementById('processing-progress-ghost');
                if (ghostBar) {
                    ghostBar.style.width = '100%';
                    ghostBar.parentElement.classList.add('visible');
                }
            }
        }
    }
}

/**
 * Monitor normalization job via SSE with polling fallback.
 * The processing state is already shown - this function just updates status.
 * @param {string} jobId
 * @param {string} fileName
 * @param {number} fileSize - File size in bytes for velocity calculations
 */
function monitorNormalizationJob(jobId, fileName, fileSize) {
    document.getElementById('processing-status').textContent = 'Initializing...';

    const progressBar = document.getElementById('processing-progress');
    progressBar.classList.add('indeterminate');
    progressBar.style.width = '';
    progressAnimator.reset();
    progressAnimator.currentFileSize = fileSize;

    const sseUrl = '/api/jobs/' + jobId + '/progress';

    if (typeof EventSource === 'undefined') {
        void pollNormalizationJobFallback(jobId, fileName, fileSize);

        return;
    }

    const eventSource = new EventSource(sseUrl);
    let lastStatus = null;
    let connectionEstablished = false;
    let jobFinished = false;

    const connectionTimeout = setTimeout(() => {
        if (!connectionEstablished) {
            eventSource.close();
            void pollNormalizationJobFallback(jobId, fileName, fileSize);
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
            updateProcessingStatus(lastStatus);
        }
    });

    eventSource.addEventListener('done', async () => {
        clearTimeout(connectionTimeout);
        jobFinished = true;
        eventSource.close();
        if (lastStatus?.status === 'Completed') {
            // Fetch the episode details for the info card
            const episode = await fetchMostRecentEpisode();
            saveJobState({ status: 'success', fileName, episode: episode });
            await showSuccess(episode || fileName);
        } else {
            const errorMsg = lastStatus?.error || 'Normalization failed';
            saveJobState({ status: 'error', fileName, error: errorMsg });
            showError(errorMsg);
        }
    });

    // Named 'error' event from server (e.g., job not found)
    // Only handle if it has data (server-sent), otherwise let onerror handle it
    eventSource.addEventListener('error', (e) => {
        if (!e.data) {
            // This is a connection error, not a server-sent error event
            // Let onerror handle it (falls back to polling)
            return;
        }
        clearTimeout(connectionTimeout);
        jobFinished = true;
        eventSource.close();
        const data = tryParseJson(e.data);
        const errorMsg = data?.error || 'An error occurred';
        saveJobState({ status: 'error', fileName, error: errorMsg });
        showError(errorMsg);
    });

    // Connection error (network failure or unexpected disconnect) - fall back to polling
    eventSource.onerror = () => {
        if (jobFinished) {
            return;
        }
        clearTimeout(connectionTimeout);
        eventSource.close();
        void pollNormalizationJobFallback(jobId, fileName, fileSize);
    };
}

/**
 * Poll normalization job status (fallback when SSE unavailable).
 * The processing state is already shown - this function just updates status.
 * @param {string} jobId
 * @param {string} fileName
 * @param {number} fileSize - File size in bytes for velocity calculations
 */
async function pollNormalizationJobFallback(jobId, fileName, fileSize) {
    progressAnimator.currentFileSize = fileSize;

    const pollInterval = 2000;

    while (true) {
        try {
            const response = await fetch('/api/jobs/' + jobId, {
                headers: { 'X-API-Key': apiKey }
            });

            if (!response.ok) {
                saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
                showError('Failed to check job status');

                return;
            }

            const job = await response.json();

            if (job.status === 'Completed') {
                const episode = await fetchMostRecentEpisode();
                saveJobState({ status: 'success', fileName, episode: episode });
                await showSuccess(episode || fileName);

                return;
            } else if (job.status === 'Failed') {
                const errorMsg = job.error || 'Normalization failed';
                saveJobState({ status: 'error', fileName, error: errorMsg });
                showError(errorMsg);

                return;
            }

            updateProcessingStatus(job);

            await new Promise(resolve => setTimeout(resolve, pollInterval));
        } catch (err) {
            saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
            showError('Failed to check job status');

            return;
        }
    }
}

/** @param {object} state */
function saveJobState(state) {
    sessionStorage.setItem(JOB_STORAGE_KEY, JSON.stringify(state));
}

function clearJobState() {
    sessionStorage.removeItem(JOB_STORAGE_KEY);
}

async function restoreJobState() {
    const saved = sessionStorage.getItem(JOB_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const job = tryParseJson(saved);
    if (!job) {
        clearJobState();

        return false;
    }

    if (job.status === 'success') {
        await showSuccess(job.episode || job.fileName);

        return true;
    } else if (job.status === 'error') {
        showError(job.error);
        return true;
    } else if (job.status === 'processing') {
        showState('processing');
        document.getElementById('processing-filename').textContent = job.fileName;
        progressAnimator.setRestoring();
        monitorNormalizationJob(job.jobId, job.fileName, job.fileSize || 0);
        document.getElementById('processing-progress').style.width = '0%';

        return true;
    }

    return false;
}

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && progressAnimator.currentStage) {
        // Tab became visible while progress is being tracked - snap to actual value on next SSE update
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

// Global keyboard shortcuts for success state
document.addEventListener('keydown', (e) => {
    const successState = document.getElementById('success');
    if (!successState || successState.style.display === 'none') {
        return;
    }

    if (!recentUploadsData || recentUploadsData.length === 0) {
        return;
    }

    const currentIndex = recentUploadsData.findIndex(u => u.id === selectedUploadId);
    let newIndex = currentIndex;

    if (e.key === 'ArrowDown') {
        e.preventDefault();
        newIndex = Math.min(currentIndex + 1, recentUploadsData.length - 1);
    } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        newIndex = Math.max(currentIndex - 1, 0);
    }

    if (newIndex !== currentIndex) {
        selectUpload(recentUploadsData[newIndex].id);
    }
});
