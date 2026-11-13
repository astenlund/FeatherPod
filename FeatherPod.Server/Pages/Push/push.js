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

import { IS_DEV, LOCAL_SOURCE_KEY, QUEUE_SYNC_TIMEOUT, STR_PASTE_KEY_BELOW, STR_PASTE_KEY, STR_SAVE_KEY, STR_INVALID_KEY, STR_NO_FEED_ACCESS } from './modules/config.js';
import { isActiveWork, isInUploadPhase, trapFocus } from './modules/utils.js';
import { getApiKey, setApiKey, getStoredApiKey, saveApiKey, clearApiKey, validateApiKey, validateApiKeyWithRetry } from './modules/auth.js';
import { isKeyCheckInconclusive, unverifiedKeyMessage } from './modules/key-check.js';
import { KEY_VERDICT, keyAttempts } from './modules/key-attempts.js';
import { initFeedArtwork } from './modules/artwork.js';
import { initWakeLockToggle, isWakeLockTogglePressed, acquireWakeLock } from './modules/wake-lock.js';
import { initNotificationToggle } from './modules/notifications.js';
import { progressAnimator } from './modules/progress.js';
import { showState, getCurrentState, refreshQueueActivity, showError, showWarningBanner, setNoKeyError, cacheLayoutDimensions } from './modules/state.js';
import { renderQueueList } from './modules/queue-ui.js';
import { getQueue, initQueue, registerUploadKeyRejectedCallback, restoreQueueState, addFilesToQueue, clearQueueState, clearTerminalEntries, dismissEntry, monitorEntryNormalizationInBackground, updateQueueTitleForEpisode } from './modules/queue.js';
import { registerHistoryContextMenuCallback, initHistorySection, collapseHistoryImmediate, toggleHistorySection, changeHistoryFilter, selectHistoryUpload, updateHistoryListScrollState, getHistoryFilter, getHistoryPanelPushedState, setHistoryPanelPushedState, getHistoryData, getHistorySelectedId, refreshHistoryList } from './modules/history.js';
import { showContextMenu, getContextMenuTargetId, hideContextMenu, showRenameModal, hideRenameModal, showDeleteConfirm, hideDeleteConfirm, deleteEpisode, saveEpisodeChanges, updateRenameSaveState, toggleNotePanel, closeNotePanel, commitNoteAndRefreshSuggestion, handleNoteInput, isNotePanelOpen, registerEpisodeRenamedCallback } from './modules/editing.js';
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
        const hasActiveUpload = () => getQueue().some(isInUploadPhase);
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
 * @property {number|null} [jobIdReceivedAt] - performance.now() when this page received jobId, from a 202 response or
 *     from the snapshot merge that discovered the job (not persisted); a recent-jobs snapshot requested earlier can lack
 *     the job, so mergeServerJobs does not treat it as stale
 * @property {string|null} [terminalJobId] - jobId whose terminal status finalizeTerminalEntry already applied (not
 *     persisted); a later terminal answer for the same job is ignored while the entry is not active work again
 * @property {string|null} episodeId - Episode ID after completion
 * @property {Episode|null} episode - Full episode data after completion
 * @property {string|null} error - Error message if failed
 * @property {XMLHttpRequest|null} xhr - Active XHR for upload cancellation
 * @property {EventSource|null} eventSource - Active SSE connection for normalization
 * @property {number} fileSize - File size in bytes
 * @property {string} fileName - Original file name
 * @property {string|null} title - Episode title (from server 202 response or progress updates)
 * @property {number|null} localSourceIndex - File index on LocalFileServer (null for browser-selected files)
 * @property {string|null} [shareCacheUrl] - share-target cache entry the file was claimed from (null for other files;
 *     not persisted); a 401/403 upload puts the file back there
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
 * @property {boolean} networkError - Whether the request failed to reach the server (validity unknown)
 * @property {boolean} serverError - Whether the server answered with a failure other than 401 (validity unknown)
 */

/**
 * @typedef {Object} UserInfo
 * @property {string} id - User ID
 * @property {string} role - User role ('Admin' or 'FeedOwner')
 * @property {string[]} ownedFeeds - Array of feed IDs the user owns (for FeedOwner role)
 */

// Wire up queue-ui callbacks
initQueue();

// The queue raises the key prompt with the reason when an upload's 401/403 rejects the page's accepted key (see
// queue.js rejectQueueForKey, which also hands CLI and shared files back to intake)
registerUploadKeyRejectedCallback(showNoKeyUI);

/** Wire history actions before initialization, including first-visit key entry. */
registerHistoryContextMenuCallback(showContextMenu);

// Propagate history-panel renames to matching queue entries
registerEpisodeRenamedCallback(updateQueueTitleForEpisode);

// Wire up YouTube import
initYouTubeImport();
registerYouTubeJobCallback((jobResponse, { replacesEntryId = null } = {}) => {
    // Create a queue entry from the YouTube job 202 response
    const entry = {
        id: 'yt_' + jobResponse.jobId,
        file: null,
        status: 'normalizing',
        progress: 0,
        stage: 'Queued',
        jobId: jobResponse.jobId,
        jobIdReceivedAt: performance.now(),
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
        refreshQueueActivity(hasActive);
    }

    renderQueueList(true);
    monitorEntryNormalizationInBackground(entry);

    if (replacesEntryId) {
        // The failed entry this import retries after a cookie upload. Dismissed only after
        // its replacement is queued so the queue never transiently empties.
        dismissEntry(replacesEntryId);
    }
});

// Document-level paste listener for YouTube URLs (runs before API key paste detection)
document.addEventListener('paste', (e) => {
    handleYouTubePaste(e);
});

const API_KEY_REGEX = /fp_[a-zA-Z0-9-]+_[A-Za-z0-9_-]{22}(?=[^A-Za-z0-9_-]|$)/;

/** Whether initNoKeyState has already been called (prevents duplicate listeners) */
let noKeyStateInitialized = false;

/**
 * Whether a newer key attempt (see key-attempts.js) began since `attempt` was captured: init ran again (on a
 * hashchange) or raised the key prompt. The newer attempt then owns the page, so a continuation that captured
 * `attempt` (init's key check, a startup run, a key entry) must leave it alone.
 * @param {number} attempt
 * @returns {boolean}
 */
function isKeyAttemptSuperseded(attempt) {
    return keyAttempts.isSuperseded(attempt);
}

/**
 * Key attempt current when the typed key now being validated was entered, or null. Repeat Enter/Save presses are ignored
 * while the current attempt's key is being validated; a newer attempt (a newer prompt, or an init run) is not blocked by
 * an older validation that is still finishing.
 */
let validatingAttempt = null;

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
            refreshQueueActivity(hasActive);

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

/**
 * Raise the key prompt, a new key attempt that owns the page whatever view is showing. Used by init when a key is
 * rejected or missing, and by the queue when an upload answered 401 or 403 rejects the page's accepted key
 * (registerUploadKeyRejectedCallback).
 * @param {string|null} [errorType] - 'invalid' or 'no-access' to clear the current key and say why; null keeps it
 */
function showNoKeyUI(errorType = null) {
    keyAttempts.begin();
    if (errorType) {
        clearApiKey();
    }
    // A prompt raised while an earlier key entry is still starting up must not inherit its disabled controls
    resetNoKeyState();
    setNoKeyError(errorType);
    showState('no-key');
    initNoKeyState();
}

async function init() {
    initFeedArtwork();
    cacheLayoutDimensions();

    // Every run is a new key attempt: it supersedes a key entry, key check or startup run an earlier run or prompt began
    const attempt = keyAttempts.begin();

    /**
     * Put `key` in use as this run's accepted key.
     * @param {string} key
     */
    function acceptKey(key) {
        saveApiKey(key);
        keyAttempts.settle(attempt, KEY_VERDICT.ACCEPTED);
    }

    /**
     * Switch to the saved key after the URL key failed its check. A saved key whose own check is inconclusive is kept
     * unchecked rather than dropped, since dropping it would raise the prompt that erases it.
     * @param {string|null} fallbackKey - The saved key
     * @param {string} urlKeyProblem - Why the URL key is not used, e.g. 'Invalid URL key'
     * @returns {Promise<boolean>} Whether the URL key's rejection is dealt with: the saved key is now in use, or during
     *     the saved key's check a newer key attempt began or a key entry decided this one, and that owns the page
     */
    async function tryFallbackKey(fallbackKey, urlKeyProblem) {
        if (!fallbackKey) {
            return false;
        }

        const fallbackValidation = await validateApiKeyWithRetry(fallbackKey);
        if (!keyAttempts.isOpen(attempt)) {
            return true;
        }

        if (fallbackValidation.valid && fallbackValidation.feedAccess) {
            showWarningBanner(urlKeyProblem + '. Using saved key.');
            acceptKey(fallbackKey);

            return true;
        }

        if (isKeyCheckInconclusive(fallbackValidation)) {
            showWarningBanner(urlKeyProblem + '. ' + unverifiedKeyMessage(fallbackValidation, 'saved key') + '.');
            acceptKey(fallbackKey);

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
                sessionStorage.setItem(LOCAL_SOURCE_KEY, JSON.stringify({ host: source, token }));
            }
        }
    }

    if (!getLocalSourceConfig()) {
        try {
            const stored = sessionStorage.getItem(LOCAL_SOURCE_KEY);
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
            // While this key was being checked, a newer key attempt began, or a key entered on a prompt still showing
            // decided this attempt: either owns the page, so drop this result
            if (!keyAttempts.isOpen(attempt)) {
                return;
            }

            if (validation.valid && validation.feedAccess) {
                acceptKey(primaryKey);
            } else if (isKeyCheckInconclusive(validation)) {
                showWarningBanner(unverifiedKeyMessage(validation, extractedKey ? 'URL key' : 'saved key'));
                acceptKey(primaryKey);
            } else if (extractedKey) {
                const urlKeyProblem = validation.valid ? 'URL key does not have access to this feed' : 'Invalid URL key';
                if (await tryFallbackKey(storedKey, urlKeyProblem)) {
                    return;
                }

                keyAttempts.settle(attempt, KEY_VERDICT.REJECTED);
                showNoKeyUI(validation.valid ? 'no-access' : 'invalid');
            } else {
                keyAttempts.settle(attempt, KEY_VERDICT.REJECTED);
                showNoKeyUI(validation.valid ? 'no-access' : null);
            }
        }).catch(() => {});
    } else {
        showNoKeyUI(null);
        // Keep the CLI local source connected behind the prompt (as after a reload once a rejected upload cleared the
        // key): it holds the offered files until a key is accepted, and its heartbeat keeps the CLI's file server from
        // shutting down while the user finds a key. The startup run of the key entry joins this connection.
        if (getLocalSourceConfig()) {
            void connectLocalSource(keyAttempts.current);
        }

        return;
    }

    await startSession();
}

// ============================================================================
// startSession -- everything startup does once an API key is set
// ============================================================================

/**
 * Run the startup that needs an API key: dismissed-job ids, the feed event stream, local-source or
 * shared-file intake, the saved queue, the initial view, both toggles and history. Shared by init()
 * and key entry (acceptEnteredKey), so entering a key leaves the page as a reload with that key
 * would. Safe to re-run (hashchange re-invokes init): restoreQueueState leaves a live queue alone, and
 * the DOM deduplicates the toggle initializers' click listeners. The run belongs to the key attempt current when it
 * begins (see key-attempts.js): intake waits for that attempt's key to be accepted, and showInitialView does not choose
 * a view once a newer attempt began (init ran again or raised the key prompt). Local-source or shared-file intake can
 * still switch to the queue view on its own. The recent-jobs snapshot requested at the start is handed to
 * mergeServerJobs once: by showInitialView when it gets it in time, otherwise after the history fetch. mergeServerJobs
 * ignores it when a snapshot requested later (on a job-added event, on tab reactivation or by a later startup run) has
 * already been merged, since that one has already reconciled the queue.
 */
async function startSession() {
    const attempt = keyAttempts.current;
    loadDismissedJobIds();
    connectFeedEvents();
    const serverJobsPromise = fetchRecentJobs();
    let snapshotMerged = false;

    if (getLocalSourceConfig()) {
        await restoreQueueState();
        await connectLocalSource(attempt);

        // connectLocalSource shows the queue itself when the CLI offers files
        if (isInitialViewPending()) {
            snapshotMerged = await showInitialView(serverJobsPromise, false, attempt);
        }
    } else if (!(await consumeSharedFiles(attempt))) {
        await restoreQueueState();
        snapshotMerged = await showInitialView(serverJobsPromise, true, attempt);
    }

    // Wire the toggles once the view (and so their visibility) is settled, before the network-bound history fetch
    initNotificationToggle();
    initWakeLockToggle();
    await initHistorySection();

    // Merge the startup snapshot when showInitialView did not (skipped, or its sync wait timed out). Merging it a
    // second time would roll back stage and progress that SSE advanced since.
    if (!snapshotMerged) {
        serverJobsPromise.then(mergeServerJobs).catch(() => {});
    }
}

/**
 * Whether startup still has to choose a view: nothing is shown yet, or the key prompt is showing (key entry
 * leaves it this way; showInitialView keeps a prompt that was raised after the run began).
 * @returns {boolean}
 */
function isInitialViewPending() {
    const state = getCurrentState();

    return state === null || state === 'no-key';
}

/**
 * Merge the server's recent jobs (waiting at most QUEUE_SYNC_TIMEOUT), then show the queue if it holds
 * entries, otherwise the ready view. Leaves the view alone when a newer key attempt began after the calling run did.
 * @param {Promise<import('./modules/server-sync.js').RecentJobsSnapshot|null>} serverJobsPromise
 * @param {boolean} focusReady - Focus the file picker button when showing the ready view
 * @param {number} attempt - Key attempt current when the calling startup run began
 * @returns {Promise<boolean>} Whether the snapshot arrived within the sync wait and was handed to mergeServerJobs here
 *     (which ignores a null snapshot, and one requested before a snapshot it has already merged)
 */
async function showInitialView(serverJobsPromise, focusReady, attempt) {
    let merged = false;
    try {
        const snapshot = await Promise.race([
            serverJobsPromise,
            new Promise((_, reject) => setTimeout(() => reject(), QUEUE_SYNC_TIMEOUT))
        ]);
        mergeServerJobs(snapshot);
        merged = true;
    } catch {
        // Sync wait timed out -- proceed with local-only state; startSession merges the late response
    }

    // A newer key attempt began while this run was starting (a later init run, or the key prompt): it owns the view
    if (isKeyAttemptSuperseded(attempt)) {
        return merged;
    }

    const queue = getQueue();
    if (queue.length > 0) {
        const hasActive = queue.some(e => isActiveWork(e));
        showState('queue', hasActive, collapseHistoryImmediate);
        renderQueueList();
        refreshQueueActivity(hasActive);
    } else {
        showState('ready');
        if (focusReady) {
            document.getElementById('select-file').focus();
        }
    }

    return merged;
}

// ============================================================================
// initNoKeyState -- paste-key flow
// ============================================================================

/**
 * Banner text for a key the prompt did not save. A server error did not judge the key, so it asks for another try.
 * @param {ApiKeyValidationResult} validation
 * @returns {string}
 */
function rejectedKeyMessage(validation) {
    if (validation.serverError) {
        return validation.error + ', try again';
    }

    if (validation.valid && !validation.feedAccess) {
        return STR_NO_FEED_ACCESS;
    }

    return validation.error || STR_INVALID_KEY;
}

/** Put the key prompt's controls back in their idle state and clear its error text. */
function resetNoKeyState() {
    const pasteBtn = document.getElementById('paste-key-btn');
    const textarea = document.getElementById('key-textarea');
    const saveBtn = document.getElementById('save-key-btn');
    pasteBtn.style.display = 'block';
    pasteBtn.disabled = false;
    pasteBtn.textContent = STR_PASTE_KEY;
    document.getElementById('key-textarea-container').style.display = 'none';
    textarea.value = '';
    textarea.disabled = false;
    saveBtn.disabled = false;
    saveBtn.textContent = STR_SAVE_KEY;
    setNoKeyError(null);
}

function initNoKeyState() {
    if (noKeyStateInitialized) {
        return;
    }
    noKeyStateInitialized = true;

    const pasteBtn = document.getElementById('paste-key-btn');
    const textareaContainer = document.getElementById('key-textarea-container');
    const textarea = document.getElementById('key-textarea');
    const saveBtn = document.getElementById('save-key-btn');

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
     * Save a key entered on the prompt as the accepted key of `attempt`, then leave the prompt by running the same
     * startup a reload would. The prompt keeps its "Validating..." state while startup runs (it is hidden as soon as
     * startup shows a view) and is reset for a later visit after startSession completes, history fetch included, unless a
     * newer key attempt began meanwhile: if that is a prompt, it reset its own controls and owns its error text.
     * @param {string} key - The entered key
     * @param {number} attempt - Key attempt current when the key was entered: usually the prompt's own, but an init run's
     *     when a hashchange began one while the prompt still showed, in which case the entered key decides that attempt
     */
    async function acceptEnteredKey(key, attempt) {
        saveApiKey(key);
        keyAttempts.settle(attempt, KEY_VERDICT.ACCEPTED);
        try {
            await startSession();
        } finally {
            if (!isKeyAttemptSuperseded(attempt)) {
                resetNoKeyState();
            }
        }
    }

    async function validateTextareaKey() {
        if (validatingAttempt === keyAttempts.current) {
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

        const attempt = keyAttempts.current;
        validatingAttempt = attempt;
        saveBtn.disabled = true;
        saveBtn.textContent = 'Validating...';
        textarea.disabled = true;

        try {
            const validation = await validateApiKey(key);
            // A newer key attempt began while this key was being validated (a hashchange, or a newer prompt): it owns
            // the page, so drop this result
            if (isKeyAttemptSuperseded(attempt)) {
                return;
            }

            if (validation.valid && validation.feedAccess) {
                await acceptEnteredKey(key, attempt);
            } else if (validation.networkError) {
                showWarningBanner(unverifiedKeyMessage(validation, 'entered key'));
                await acceptEnteredKey(key, attempt);
            } else {
                saveBtn.disabled = false;
                saveBtn.textContent = STR_SAVE_KEY;
                textarea.disabled = false;
                showWarningBanner(rejectedKeyMessage(validation));
            }
        } finally {
            if (validatingAttempt === attempt) {
                validatingAttempt = null;
            }
        }
    }

    pasteBtn.addEventListener('click', async () => {
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            morphToTextarea();

            return;
        }

        // A key attempt begun while this paste is reading or validating (a hashchange, or a newer prompt) owns the page;
        // each await below drops a stale result
        const attempt = keyAttempts.current;
        try {
            pasteBtn.disabled = true;
            pasteBtn.textContent = 'Reading...';

            const clipboardText = await navigator.clipboard.readText();
            if (isKeyAttemptSuperseded(attempt)) {
                return;
            }
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
            if (isKeyAttemptSuperseded(attempt)) {
                return;
            }

            if (validation.valid && validation.feedAccess) {
                await acceptEnteredKey(apiKeyToValidate, attempt);
            } else if (validation.networkError) {
                showWarningBanner(unverifiedKeyMessage(validation, 'pasted key'));
                await acceptEnteredKey(apiKeyToValidate, attempt);
            } else if (validation.serverError) {
                // The key was not judged: keep the Paste button so the user can try again
                pasteBtn.disabled = false;
                pasteBtn.textContent = STR_PASTE_KEY;
                showWarningBanner(rejectedKeyMessage(validation));
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
            if (isKeyAttemptSuperseded(attempt)) {
                return;
            }
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

/**
 * Route a drop on either drop zone. A YouTube URL in the text payload wins over
 * dropped files: a linked-thumbnail drag from YouTube search results carries both
 * the image file and the watch URL, and the image must not reach the upload gate.
 * Only when no YouTube URL is present do the dropped files go to the upload queue.
 * @param {DragEvent} e
 * @param {HTMLElement} zone
 */
function routeDrop(e, zone) {
    e.preventDefault();
    zone.classList.remove('drag-over');
    if (handleYouTubeDrop(e)) {
        return;
    }
    const files = Array.from(e.dataTransfer.files);
    if (files.length > 0) {
        addFilesToQueue(files);
    }
}

/**
 * Attach drag-over highlighting and drop routing to a drop zone.
 * @param {HTMLElement} zone
 */
function wireDropZone(zone) {
    zone.addEventListener('dragover', (e) => {
        e.preventDefault();
        zone.classList.add('drag-over');
    });
    zone.addEventListener('dragleave', (e) => {
        e.preventDefault();
        zone.classList.remove('drag-over');
    });
    zone.addEventListener('drop', (e) => routeDrop(e, zone));
}

wireDropZone(document.getElementById('drop-zone'));

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
    wireDropZone(queueDropZone);
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
