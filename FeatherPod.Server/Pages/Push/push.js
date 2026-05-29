/**
 * Push page orchestrator -- imports all modules and wires up init, event listeners, and DOM.
 *
 * Dev-only query params (IS_DEV only, combine freely in query string):
 *
 * ?pwa   (FAKE_PWA)               - Fakes PWA standalone mode. Any logic checking
 *                                    standalone display mode must also check this flag.
 * ?ghost (SHOW_GHOST)             - Shows unfiltered "ghost" progress bar and logs
 *                                    velocity deltas. New progress visualization or
 *                                    velocity logging should be gated behind this flag.
 * ?alive (DEBUG_TITLE_ANIMATION)  - Bypasses isFirstStateChange optimization so morph
 *                                    animation triggers on first render. New "skip
 *                                    animation on first load" logic should check this.
 * ?state={name}                   - Forces a visual-only UI state (no-key, no-key-invalid,
 *                                    no-key-no-access, ready, queue, error) via applyDevState().
 * ?vup={kB/s}, ?vanal={kB/s},    - Override localStorage-based learned velocity history.
 *  ?vnorm={kB/s}                    New code reading learned velocities should check
 *  (VELOCITY_OVERRIDES)             VELOCITY_OVERRIDES[stage] first.
 * ?ytcookies                       - Triggers the YouTube cookie upload dialog on load.
 */

import { IS_DEV, QUEUE_SYNC_TIMEOUT, STR_PASTE_KEY_BELOW, STR_PASTE_KEY, STR_SAVE_KEY, STR_INVALID_KEY, STR_NO_FEED_ACCESS } from './modules/config.js';
import './modules/utils.js'; // Number.prototype extensions (side-effect)
import { isActiveWork, trapFocus } from './modules/utils.js';
import { getApiKey, setApiKey, getStoredApiKey, saveApiKey, clearApiKey, validateApiKey, validateApiKeyWithRetry } from './modules/auth.js';
import { initFeedArtwork } from './modules/artwork.js';
import { initWakeLockToggle, isWakeLockTogglePressed, acquireWakeLock } from './modules/wake-lock.js';
import { initNotificationToggle } from './modules/notifications.js';
import { progressAnimator } from './modules/progress.js';
import { showState, getCurrentState, updateQueueTitle, showError, showWarningBanner, setNoKeyError, cacheLayoutDimensions } from './modules/state.js';
import { renderQueueList } from './modules/queue-ui.js';
import { getQueue, initQueue, restoreQueueState, addFilesToQueue, clearQueueState, clearTerminalEntries, monitorEntryNormalizationInBackground } from './modules/queue.js';
import { initHistorySection, collapseHistoryImmediate, toggleHistorySection, changeHistoryFilter, selectHistoryUpload, updateHistoryListScrollState, getHistoryFilter, getHistoryPanelPushedState, setHistoryPanelPushedState, getHistoryData, getHistorySelectedId, refreshHistoryList } from './modules/history.js';
import { getContextMenuTargetId, hideContextMenu, showRenameModal, hideRenameModal, showDeleteConfirm, hideDeleteConfirm, deleteEpisode, saveEpisodeChanges, updateRenameSaveState, toggleNotePanel, closeNotePanel, commitNoteAndRefreshSuggestion, handleNoteInput, isNotePanelOpen } from './modules/editing.js';
import { loadDismissedJobIds, connectFeedEvents, fetchRecentJobs, mergeServerJobs, connectLocalSource, consumeSharedFiles, getLocalSourceConfig, setLocalSourceConfig, getFeedEventsSource, getLocalSourceEvents, setLocalSourceEvents } from './modules/server-sync.js';
import { handlePaste as handleYouTubePaste, handleDrop as handleYouTubeDrop, handleLongPressClipboard, consumeLongPressFlag, initYouTubeImport, registerYouTubeJobCallback, showYouTubeCookieDialog } from './modules/youtube.js';

// CSS hot-swap: service worker notifies when app.css has changed via background revalidation
if (navigator.serviceWorker) {
    navigator.serviceWorker.addEventListener('message', (event) => {
        if (event.data?.type !== 'css-updated') return;
        const oldLink = document.querySelector('link[rel="stylesheet"][href*="/push/app.css"]');
        if (!oldLink) return;
        const newLink = document.createElement('link');
        newLink.rel = 'stylesheet';
        newLink.href = oldLink.getAttribute('href');
        newLink.onload = () => {
            oldLink.remove();
            const backdrop = document.getElementById('artwork-backdrop');
            if (backdrop) {
                backdrop.style.transition = 'filter 0.3s ease';
                backdrop.style.filter = 'brightness(1.3)';
                setTimeout(() => {
                    backdrop.style.filter = '';
                    setTimeout(() => { backdrop.style.transition = ''; }, 300);
                }, 300);
            }
        };
        oldLink.parentNode.insertBefore(newLink, oldLink.nextSibling);
    });
}

// Force SW update check on page load (browser heuristics are unreliable,
// especially after hard refresh which bypasses the SW without triggering an update check).
// Subsequent checks are triggered by feed SSE reconnect (server restart) and tab reactivation.
if (navigator.serviceWorker) {
    navigator.serviceWorker.ready.then(reg => reg.update()).catch(() => {});
}

// Auto-reload on SW update: new deployment detected, reload once uploads finish
if (navigator.serviceWorker) {
    const hadController = !!navigator.serviceWorker.controller;
    navigator.serviceWorker.addEventListener('controllerchange', () => {
        if (!hadController) return;
        const flashAndReload = () => {
            const backdrop = document.getElementById('artwork-backdrop');
            if (backdrop) {
                backdrop.style.transition = 'filter 0.3s ease';
                backdrop.style.filter = 'brightness(1.3)';
                setTimeout(() => location.reload(), 300);
            } else {
                location.reload();
            }
        };
        const hasActiveUpload = () => getQueue().some(e => e.status === 'uploading' || e.status === 'saving');
        if (!hasActiveUpload()) {
            flashAndReload();

            return;
        }
        const check = setInterval(() => {
            if (!hasActiveUpload()) {
                clearInterval(check);
                flashAndReload();
            }
        }, 1000);
    });
}

/**
 * @typedef {Object} Episode
 * @property {string} id - Episode ID
 * @property {string} title - Episode title
 * @property {string} fileName - Audio file name
 * @property {number} [fileSize] - File size in bytes
 * @property {string} [duration] - Duration string (e.g. "1:23:45")
 * @property {string} [publishedDate] - ISO date string
 * @property {string} [uploadedAt] - ISO date string
 * @property {string} [note] - User note for AI title suggestion guidance
 */

/**
 * @typedef {Object} QueueEntry
 * @property {string} id - Unique entry ID
 * @property {File|null} file - File object (null after session restore)
 * @property {'queued'|'uploading'|'saving'|'normalizing'|'completed'|'failed'|'cancelled'} status
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
 * @property {string|null} title - Episode title (from server 202 response or progress updates)
 * @property {number|null} localSourceIndex - File index on LocalFileServer (null for browser-selected files)
 * @property {boolean} validationError - Whether failure is due to validation (no retry)
 * @property {number} startedAt - Epoch ms when entry was created (for 1-hour localStorage filtering)
 * @property {Function|null} _resolveMonitor - Internal: resolve function for normalization promise
 */

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

// Wire up queue-ui callbacks
initQueue();

// Wire up YouTube import
initYouTubeImport();
registerYouTubeJobCallback((jobResponse) => {
    // Create a queue entry from the YouTube job 202 response
    const entry = {
        id: 'yt_' + jobResponse.jobId,
        file: null,
        status: 'normalizing',
        progress: 0,
        stage: 'Queued',
        jobId: jobResponse.jobId,
        episodeId: jobResponse.episodeId,
        episode: null,
        error: null,
        xhr: null,
        eventSource: null,
        fileSize: 0,
        fileName: jobResponse.fileName || 'YouTube import',
        title: jobResponse.title || null,
        validationError: false,
        startedAt: Date.now(),
        _resolveMonitor: null,
        source: 'youtube'
    };
    const queue = getQueue();
    queue.push(entry);

    const hasActive = queue.some(e => isActiveWork(e));
    if (getCurrentState() !== 'queue') {
        showState('queue', hasActive, collapseHistoryImmediate);
    } else {
        updateQueueTitle(hasActive);
    }

    renderQueueList(true);
    monitorEntryNormalizationInBackground(entry);
});

// Document-level paste listener for YouTube URLs (runs before API key paste detection)
document.addEventListener('paste', (e) => {
    handleYouTubePaste(e);
});

const API_KEY_REGEX = /fp_[a-zA-Z0-9-]+_[A-Za-z0-9_-]{22}(?=[^A-Za-z0-9_-]|$)/;

/** Whether initNoKeyState has already been called (prevents duplicate listeners) */
let noKeyStateInitialized = false;

// ============================================================================
// applyDevState -- dev-only forced UI state
// ============================================================================

/**
 * Apply a dev-only forced UI state (purely visual, no real init).
 * @param {string} devState - The state value from ?state= query param
 * @returns {boolean} True if the state was recognized
 */
function applyDevState(devState) {
    const queue = getQueue();
    switch (devState) {
        case 'no-key':
            setNoKeyError(null);
            showState('no-key');
            initNoKeyState();

            return true;
        case 'no-key-invalid':
            setNoKeyError('invalid');
            showState('no-key');
            initNoKeyState();

            return true;
        case 'no-key-no-access':
            setNoKeyError('no-access');
            showState('no-key');
            initNoKeyState();

            return true;
        case 'ready':
            showState('ready');

            return true;
        case 'queue':
            queue.length = 0;
            queue.push(
                {
                    id: 'dev-1', file: null, status: 'completed', progress: 100,
                    stage: 'Completed', jobId: null, episodeId: 'abc123def456',
                    episode: { id: 'abc123def456', title: 'Morning Thoughts on Architecture', fileName: 'morning-thoughts.m4a', fileSize: 15728640, duration: '0:42:15', publishedDate: '2026-03-22T08:00:00Z' },
                    error: null, xhr: null, eventSource: null,
                    fileSize: 15728640, fileName: 'morning-thoughts.m4a',
                    validationError: false, startedAt: Date.now() - 300000, _resolveMonitor: null,
                },
                {
                    id: 'dev-2', file: null, status: 'uploading', progress: 45,
                    stage: null, jobId: null, episodeId: null, episode: null,
                    error: null, xhr: null, eventSource: null,
                    fileSize: 52428800, fileName: 'interview-with-special-guest.m4a',
                    validationError: false, startedAt: Date.now() - 60000, _resolveMonitor: null,
                },
                {
                    id: 'dev-3', file: null, status: 'failed', progress: 0,
                    stage: null, jobId: null, episodeId: null, episode: null,
                    error: 'File exceeds maximum size of 200 MB', xhr: null, eventSource: null,
                    fileSize: 209715200, fileName: 'uncompressed-recording.wav',
                    validationError: true, startedAt: Date.now() - 120000, _resolveMonitor: null,
                },
                {
                    id: 'dev-4', file: null, status: 'queued', progress: 0,
                    stage: null, jobId: null, episodeId: null, episode: null,
                    error: null, xhr: null, eventSource: null,
                    fileSize: 8388608, fileName: 'quick-update.m4a',
                    validationError: false, startedAt: Date.now() - 10000, _resolveMonitor: null,
                },
            );
            const hasActive = queue.some(e => isActiveWork(e));
            showState('queue', hasActive, collapseHistoryImmediate);
            renderQueueList();
            updateQueueTitle(hasActive);

            return true;
        case 'error':
            showError('This is a test error message');

            return true;
        default:
            return false;
    }
}

// ============================================================================
// init -- main initialization
// ============================================================================

async function init() {
    initFeedArtwork();
    cacheLayoutDimensions();

    function showNoKeyUI(errorType = null) {
        if (errorType) {
            clearApiKey();
        }
        setNoKeyError(errorType);
        showState('no-key');
        initNoKeyState();
    }

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

    // Dev: force a specific UI state via ?state= param (purely visual, no init)
    if (IS_DEV) {
        const devState = new URLSearchParams(window.location.search).get('state');
        if (devState) {
            if (!applyDevState(devState)) {
                showError(`Unknown state: ${devState}`);
            }

            return;
        }

        if (window.location.search.includes('ytcookies')) {
            setTimeout(() => showYouTubeCookieDialog(), 500);
        }
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
                setLocalSourceConfig({ host: source, token });
                sessionStorage.setItem('localSource', JSON.stringify({ host: source, token }));
            }
        }
    }

    if (!getLocalSourceConfig()) {
        try {
            const stored = sessionStorage.getItem('localSource');
            if (stored) {
                setLocalSourceConfig(JSON.parse(stored));
            }
        } catch { /* ignore corrupt data */ }
    }

    const storedKey = getStoredApiKey();
    const primaryKey = extractedKey || storedKey;

    if (extractedKey) {
        history.replaceState(null, '', window.location.pathname + window.location.search);
    }

    if (primaryKey) {
        setApiKey(primaryKey);

        validateApiKeyWithRetry(primaryKey).then(async (validation) => {
            if (validation.valid && validation.feedAccess) {
                saveApiKey(primaryKey);
            } else if (validation.networkError) {
                showWarningBanner(extractedKey ? 'Server unreachable \u2014 using URL key' : 'Server unreachable \u2014 using saved key');
                saveApiKey(primaryKey);
            } else if (extractedKey) {
                if (validation.valid && !validation.feedAccess) {
                    if (await tryFallbackKey(storedKey, 'URL key does not have access to this feed. Using saved key.')) {
                        return;
                    }
                } else {
                    if (await tryFallbackKey(storedKey, 'Invalid URL key. Using saved key.')) {
                        return;
                    }
                }

                if (document.getElementById('ready').style.display !== 'none') {
                    const errorType = (validation.valid && !validation.feedAccess) ? 'no-access' : (validation.valid ? null : 'invalid');
                    showNoKeyUI(errorType);
                }
            } else {
                if (document.getElementById('ready').style.display !== 'none') {
                    const errorType = (validation.valid && !validation.feedAccess) ? 'no-access' : null;
                    showNoKeyUI(errorType);
                }
            }
        }).catch(() => {});
    } else {
        showNoKeyUI(null);

        return;
    }

    loadDismissedJobIds();
    connectFeedEvents();
    const serverJobsPromise = fetchRecentJobs();

    // Local source mode
    if (getLocalSourceConfig()) {
        await restoreQueueState();
        await connectLocalSource();

        if (getCurrentState() === null) {
            try {
                const serverJobs = await Promise.race([
                    serverJobsPromise,
                    new Promise((_, reject) => setTimeout(() => reject(), QUEUE_SYNC_TIMEOUT))
                ]);
                mergeServerJobs(serverJobs);
            } catch {
                // Timeout -- proceed with local-only state
            }

            const queue = getQueue();
            if (queue.length > 0) {
                const hasActive = queue.some(e => isActiveWork(e));
                showState('queue', hasActive, collapseHistoryImmediate);
                renderQueueList();
                updateQueueTitle(hasActive);
            } else {
                showState('ready');
            }
        }
        await initHistorySection();
        initNotificationToggle();
        initWakeLockToggle();
        serverJobsPromise.then(mergeServerJobs).catch(() => {});

        return;
    }

    // Consume any files shared via PWA Share Target
    if (await consumeSharedFiles()) {
        await initHistorySection();
        initNotificationToggle();
        initWakeLockToggle();
        serverJobsPromise.then(mergeServerJobs).catch(() => {});

        return;
    }

    // Restore previous queue state (data only -- no display yet)
    await restoreQueueState();

    // Wait for server sync before deciding which state to show
    try {
        const serverJobs = await Promise.race([
            serverJobsPromise,
            new Promise((_, reject) => setTimeout(() => reject(), QUEUE_SYNC_TIMEOUT))
        ]);
        mergeServerJobs(serverJobs);
    } catch {
        // Timeout or fetch error -- proceed with local-only state; late response handled below
    }

    const queue = getQueue();
    if (queue.length > 0) {
        const hasActive = queue.some(e => isActiveWork(e));
        showState('queue', hasActive, collapseHistoryImmediate);
        renderQueueList();
        updateQueueTitle(hasActive);
    } else {
        showState('ready');
        document.getElementById('select-file').focus();
    }
    await initHistorySection();
    initNotificationToggle();
    initWakeLockToggle();

    // If the sync timed out, merge the late response when it arrives
    serverJobsPromise.then(mergeServerJobs).catch(() => {});
}

// ============================================================================
// initNoKeyState -- paste-key flow
// ============================================================================

function initNoKeyState() {
    if (noKeyStateInitialized) {
        return;
    }
    noKeyStateInitialized = true;

    const pasteBtn = document.getElementById('paste-key-btn');
    const textareaContainer = document.getElementById('key-textarea-container');
    const textarea = document.getElementById('key-textarea');
    const saveBtn = document.getElementById('save-key-btn');

    let isValidating = false;

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

    async function transitionToReadyState() {
        resetNoKeyState();
        if (getLocalSourceConfig()) {
            await connectLocalSource();
            if (getQueue().length === 0) {
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

    async function validateTextareaKey() {
        if (isValidating) {
            return;
        }

        let key = textarea.value.trim();
        if (!key) {
            showWarningBanner('Please enter an API key');

            return;
        }

        const fpKeyMatch = key.match(API_KEY_REGEX);
        if (fpKeyMatch) {
            key = fpKeyMatch[0];
        }

        isValidating = true;
        saveBtn.disabled = true;
        saveBtn.textContent = 'Validating...';
        textarea.disabled = true;

        try {
            const validation = await validateApiKey(key);

            if (validation.valid && validation.feedAccess) {
                saveApiKey(key);
                await transitionToReadyState();
            } else if (validation.networkError) {
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

    pasteBtn.addEventListener('click', async () => {
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            morphToTextarea();

            return;
        }

        try {
            pasteBtn.disabled = true;
            pasteBtn.textContent = 'Reading...';

            const clipboardText = await navigator.clipboard.readText();
            const trimmed = clipboardText ? clipboardText.trim() : '';
            const fpKeyMatch = trimmed.match(API_KEY_REGEX);

            if (!fpKeyMatch) {
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea();

                return;
            }

            pasteBtn.textContent = 'Validating...';
            const apiKeyToValidate = fpKeyMatch[0];
            const validation = await validateApiKey(apiKeyToValidate);

            if (validation.valid && validation.feedAccess) {
                saveApiKey(apiKeyToValidate);
                await transitionToReadyState();
            } else if (validation.networkError) {
                showWarningBanner('Server unreachable \u2014 using pasted key');
                saveApiKey(apiKeyToValidate);
                await transitionToReadyState();
            } else if (validation.valid && !validation.feedAccess) {
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea(true);
                setNoKeyError('no-access');
            } else {
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                morphToTextarea(true);
                setNoKeyError('invalid');
            }
        } catch {
            pasteBtn.disabled = false;
            pasteBtn.textContent = STR_PASTE_KEY;
            morphToTextarea();
        }
    });

    textarea.addEventListener('input', () => {
        const noKeyTitleEl = document.getElementById('no-key-title');
        if (noKeyTitleEl) {
            noKeyTitleEl.textContent = STR_PASTE_KEY_BELOW;
        }
    });

    textarea.addEventListener('paste', async () => {
        setTimeout(async () => {
            if (textarea.value.trim()) {
                await validateTextareaKey();
            }
        }, 0);
    });

    textarea.addEventListener('keydown', async (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            await validateTextareaKey();
        }
    });

    saveBtn.addEventListener('click', async () => {
        await validateTextareaKey();
    });
}

// ============================================================================
// Global event listeners
// ============================================================================

// ES modules are deferred -- DOM is already parsed when this runs
init();
window.addEventListener('hashchange', init);

window.addEventListener('beforeunload', () => {
    const localSourceEvents = getLocalSourceEvents();
    if (localSourceEvents) {
        localSourceEvents.close();
        setLocalSourceEvents(null);
    }
    const feedEventsSource = getFeedEventsSource();
    if (feedEventsSource) {
        feedEventsSource.close();
    }
});

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
        // Check for SW update on tab reactivation (catches deploys while tab was backgrounded)
        if (navigator.serviceWorker) {
            navigator.serviceWorker.ready.then(reg => reg.update()).catch(() => {});
        }
        if (progressAnimator.hasActiveSlots()) {
            progressAnimator.setRestoring();
        }
        if (isWakeLockTogglePressed()) {
            acquireWakeLock();
        }
        if (getApiKey() && !getFeedEventsSource()) {
            connectFeedEvents();
        }
        clearTerminalEntries();
        if (getApiKey()) {
            fetchRecentJobs().then(mergeServerJobs).catch(() => {});
            refreshHistoryList().catch(() => {});
        }
    }
});

window.addEventListener('resize', cacheLayoutDimensions);
window.addEventListener('resize', hideContextMenu);

// ============================================================================
// DOM event listeners
// ============================================================================

// File input and drop zones
document.getElementById('select-file').addEventListener('click', () => {
    if (consumeLongPressFlag()) {
        return;
    }
    document.getElementById('file-input').click();
});

const dropZoneEl = document.getElementById('drop-zone');
if (dropZoneEl && dropZoneEl.classList.contains('drop-zone--has-artwork')) {
    dropZoneEl.addEventListener('click', () => {
        if (consumeLongPressFlag()) {
            return;
        }
        if (dropZoneEl.classList.contains('drop-zone--has-artwork')) {
            document.getElementById('file-input')?.click();
        }
    });
}

// Long-press clipboard import (iOS) -- reads clipboard for YouTube URLs
// Attach to button, drop zone, AND artwork image (drop zone is display:contents on mobile)
handleLongPressClipboard(document.getElementById('select-file'));
handleLongPressClipboard(dropZoneEl);
handleLongPressClipboard(document.getElementById('artwork-backdrop-img'));

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
    if (files.length > 0) {
        addFilesToQueue(files);

        return;
    }
    // No files -- check for YouTube URL (e.g. dragged from address bar)
    handleYouTubeDrop(e);
});

// Queue state file inputs
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
        if (files.length > 0) {
            addFilesToQueue(files);

            return;
        }
        handleYouTubeDrop(e);
    });
}

// History section
document.getElementById('history-toggle')?.addEventListener('click', () => {
    const toggle = document.getElementById('history-toggle');
    if (toggle?.getAttribute('aria-expanded') === 'true' && getHistoryPanelPushedState()) {
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

document.getElementById('history-list')?.addEventListener('scroll', updateHistoryListScrollState);

document.getElementById('history-info-title')?.addEventListener('click', function() {
    this.classList.toggle('expanded');
});
document.getElementById('history-info-filename')?.addEventListener('click', function() {
    this.classList.toggle('expanded');
});

// Browser back button closes history panel
window.addEventListener('popstate', () => {
    if (getHistoryPanelPushedState()) {
        setHistoryPanelPushedState(false);
        hideContextMenu();
        window.getSelection()?.removeAllRanges();
        const toggle = document.getElementById('history-toggle');
        if (toggle?.getAttribute('aria-expanded') === 'true') {
            toggleHistorySection(false);
        }
    }
});

// Global keyboard shortcuts
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
        const renameOverlay = document.getElementById('rename-modal-overlay');
        if (renameOverlay && !renameOverlay.hidden) {
            e.preventDefault();
            if (isNotePanelOpen()) {
                closeNotePanel(true);
            } else {
                hideRenameModal(true);
            }

            return;
        }

        const deleteOverlay = document.getElementById('delete-confirm-overlay');
        if (deleteOverlay && !deleteOverlay.hidden) {
            e.preventDefault();
            hideDeleteConfirm();

            return;
        }

        if (getContextMenuTargetId() !== null) {
            e.preventDefault();
            hideContextMenu();

            return;
        }
    }

    const renameOpen = document.getElementById('rename-modal-overlay')?.hidden === false;
    const deleteOpen = document.getElementById('delete-confirm-overlay')?.hidden === false;
    if (renameOpen || deleteOpen) {
        return;
    }

    const section = document.getElementById('history-section');
    if (!section?.classList.contains('history-section--expanded')) {
        return;
    }

    if (e.key === 'Escape') {
        e.preventDefault();
        if (getHistoryPanelPushedState()) {
            history.back();
        } else {
            toggleHistorySection(false);
        }

        return;
    }

    if (e.key === 'ArrowLeft' || e.key === 'q' || e.key === 'Q' ||
        e.key === 'ArrowRight' || e.key === 'e' || e.key === 'E') {
        const filters = ['local', 'browser', 'all'];
        const currentIndex = filters.indexOf(getHistoryFilter());
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

    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        const historyData = getHistoryData();
        if (!historyData || historyData.length === 0) {
            return;
        }

        const currentIndex = historyData.findIndex(u => u.id === getHistorySelectedId());
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

// Context menu
document.addEventListener('click', (e) => {
    if (getContextMenuTargetId() !== null) {
        const menu = document.getElementById('context-menu');
        if (menu && !menu.contains(e.target)) {
            hideContextMenu();
        }
    }
});
document.getElementById('history-list')?.addEventListener('scroll', hideContextMenu);

document.getElementById('context-menu')?.addEventListener('keydown', (e) => {
    const items = document.querySelectorAll('#context-menu .context-menu-item');
    const current = Array.from(items).indexOf(document.activeElement);

    if (e.key === 'ArrowDown') {
        e.preventDefault();
        const next = Math.min(current + 1, items.length - 1);
        items[next].focus();
    } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        const prev = Math.max(current - 1, 0);
        items[prev].focus();
    } else if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        document.activeElement?.click();
    }
});

document.querySelectorAll('#context-menu .context-menu-item').forEach(item => {
    item.addEventListener('click', () => {
        const episodeId = getContextMenuTargetId();
        const action = item.dataset.action;
        hideContextMenu();
        if (!episodeId) {
            return;
        }
        if (action === 'rename') {
            showRenameModal(episodeId);
        } else if (action === 'delete') {
            showDeleteConfirm(episodeId);
        }
    });
});

// Delete confirmation
document.getElementById('delete-cancel')?.addEventListener('click', hideDeleteConfirm);
document.getElementById('delete-confirm')?.addEventListener('click', () => {
    const overlay = document.getElementById('delete-confirm-overlay');
    const episodeId = overlay?.dataset.episodeId;
    if (episodeId) {
        deleteEpisode(episodeId);
    }
});

// Rename modal
document.getElementById('rename-cancel')?.addEventListener('click', () => hideRenameModal(true));
document.getElementById('rename-save')?.addEventListener('click', () => {
    const overlay = document.getElementById('rename-modal-overlay');
    const episodeId = overlay?.dataset.episodeId;
    const input = document.getElementById('rename-input');
    if (episodeId && input) {
        saveEpisodeChanges(episodeId, input.value);
    }
});
document.getElementById('rename-input')?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
        e.preventDefault();
        const saveBtn = document.getElementById('rename-save');
        if (saveBtn && !saveBtn.disabled) {
            saveBtn.click();
        }
    }
});
document.getElementById('rename-input')?.addEventListener('input', updateRenameSaveState);
document.getElementById('rename-accept')?.addEventListener('click', () => {
    const textEl = document.getElementById('rename-suggestion-text');
    const input = document.getElementById('rename-input');
    if (textEl && input && textEl.textContent.trim()) {
        input.value = textEl.textContent.trim();
        updateRenameSaveState();
    }
});

// Note panel
document.getElementById('rename-note')?.addEventListener('click', toggleNotePanel);
document.getElementById('rename-note-input')?.addEventListener('input', function() {
    handleNoteInput(this);
});
document.getElementById('rename-note-input')?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        closeNotePanel(false, true);
        commitNoteAndRefreshSuggestion();
    }
});

// Modal overlays -- click-outside-to-close
document.getElementById('rename-modal-overlay')?.addEventListener('click', (e) => {
    if (e.target === e.currentTarget) {
        hideRenameModal(true);
    }
});
document.getElementById('delete-confirm-overlay')?.addEventListener('click', (e) => {
    if (e.target === e.currentTarget) {
        hideDeleteConfirm();
    }
});

// Focus traps for modals
document.getElementById('rename-modal-overlay')?.addEventListener('keydown', (e) => {
    const modal = document.getElementById('rename-modal-overlay')?.querySelector('.modal');
    if (modal) {
        trapFocus(e, modal);
    }
});
document.getElementById('delete-confirm-overlay')?.addEventListener('keydown', (e) => {
    const modal = document.getElementById('delete-confirm-overlay')?.querySelector('.modal');
    if (modal) {
        trapFocus(e, modal);
    }
});
