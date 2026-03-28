import { FEED_ID, QUEUE_STORAGE_KEY, STR_INVALID_KEY, STR_NO_FEED_ACCESS } from './config.js';
import { isValidAudioFile, isActiveWork, tryParseJson } from './utils.js';
import { getApiKey } from './auth.js';
import { showState, getCurrentState, updateQueueTitle, getCollapsedHeight, COLLAPSED_WIDTH } from './state.js';
import { progressAnimator } from './progress.js';
import { renderQueueList, updateQueueItemInDOM, updateQueueItemProgress, removeQueueItemFromDOM, getEntryProgressBar, rebindProgressAnimator, registerQueueCallbacks, createQueueItemElement } from './queue-ui.js';
import { resetNotificationToggle, syncPushSession, notifyQueueComplete, setNotificationToggleVisible } from './notifications.js';
import { resetWakeLockToggle, setWakeLockToggleVisible } from './wake-lock.js';
import { collapseHistoryImmediate, saveToLocalHistory, refreshHistoryList, fetchBrowserUploads, initHistorySection, invalidateBrowserUploadsCache } from './history.js';
import { getDismissedJobIds, saveDismissedJobIds } from './server-sync.js';
import { showYouTubeCookieDialog } from './youtube.js';

/** @type {Array<import('../push.js').QueueEntry>} */
let uploadQueue = [];
/** @type {string|null} */
let activeUploadId = null;
/** @type {boolean} */
let isUploading = false;
/** @type {number} */
let nextEntryId = 0;

const Q_MORPH_DURATION = 400;

const COLLAPSED_HEIGHT_DEFAULT = 280;

export function getQueue() {
    return uploadQueue;
}

export function getActiveUploadId() {
    return activeUploadId;
}

export function getIsUploading() {
    return isUploading;
}

/**
 * Generate a unique entry ID for queue items.
 * @returns {string}
 */
export function generateEntryId() {
    return 'q' + (nextEntryId++) + '_' + Date.now().toString(36);
}

/**
 * Animate the queue drop zone morphing from the ready-state drop zone dimensions.
 * Mirrors the history section morph pattern: set explicit start -> reflow -> transition to target.
 */
function animateQueueDropZoneMorph() {
    const queueDZ = document.getElementById('queue-drop-zone');
    if (!queueDZ) {
        return;
    }

    const targetHeight = queueDZ.getBoundingClientRect().height;

    queueDZ.classList.add('queue-drop-zone--morphing');
    queueDZ.style.height = getCollapsedHeight() + 'px';

    void queueDZ.offsetHeight;
    queueDZ.style.height = targetHeight + 'px';

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
export function prepareReadyDropZoneMorph(startHeight) {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) {
        return;
    }

    dropZone.classList.add('drop-zone--morphing');
    dropZone.style.height = startHeight + 'px';
}

/**
 * Run the ready-state drop zone morph transition. Must be called after showState('ready')
 * and prepareReadyDropZoneMorph() so the element is visible with its start height committed.
 */
export function animateReadyDropZoneMorph() {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) {
        return;
    }

    const targetHeight = dropZone.classList.contains('drop-zone--has-artwork')
        ? COLLAPSED_WIDTH
        : COLLAPSED_HEIGHT_DEFAULT;

    void dropZone.offsetHeight;

    dropZone.style.height = targetHeight + 'px';

    setTimeout(() => {
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
export function addFilesToQueue(files) {
    if (files.length === 0) {
        return;
    }

    const newEntries = [];

    for (const file of files) {
        const isDuplicate = uploadQueue.some(e =>
            e.fileName === file.name &&
            e.fileSize === file.size &&
            (isActiveWork(e) || e.status === 'completed')
        );
        if (isDuplicate) {
            continue;
        }

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
            title: null,
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

    // Reset notification toggle when starting a new queue or adding items with no active work
    const hadActiveWork = uploadQueue.some(e => !newEntries.includes(e) && isActiveWork(e));
    if (!hadActiveWork) {
        resetNotificationToggle();
    }

    const hasActive = uploadQueue.some(e => isActiveWork(e));
    const previousState = getCurrentState();

    if (previousState !== 'queue') {
        showState('queue', hasActive, collapseHistoryImmediate);
    } else {
        updateQueueTitle(hasActive);
        setNotificationToggleVisible(hasActive);
        setWakeLockToggleVisible(hasActive);
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
export function removeFromQueue(entryId) {
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
 * Dismiss a completed, failed, or cancelled entry from the queue.
 * Returns to ready state if queue becomes empty.
 * @param {string} entryId
 */
export async function dismissEntry(entryId) {
    const index = uploadQueue.findIndex(e => e.id === entryId);
    if (index === -1) {
        return;
    }

    const entry = uploadQueue[index];
    if (entry.status !== 'completed' && entry.status !== 'failed' && entry.status !== 'cancelled') {
        return;
    }

    const jobId = entry.jobId;
    if (jobId) {
        getDismissedJobIds().set(jobId, Date.now());
        saveDismissedJobIds();
    }

    uploadQueue.splice(index, 1);
    removeQueueItemFromDOM(entryId);

    saveQueueState();
    checkAllComplete();

    // Mark as cancelled server-side so mergeServerJobs won't re-add it
    if (jobId && entry.status !== 'completed') {
        try {
            await fetch('/api/jobs/' + jobId + '/cancel', {
                method: 'POST',
                headers: { 'X-API-Key': getApiKey() }
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
export function processQueue() {
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
export function checkAllComplete() {
    if (uploadQueue.length === 0) {
        const queueDZHeight = document.getElementById('queue-drop-zone')?.getBoundingClientRect().height || 0;
        if (queueDZHeight > 0) {
            prepareReadyDropZoneMorph(queueDZHeight);
        }
        clearQueueState();
        resetWakeLockToggle();
        showState('ready', false, collapseHistoryImmediate);
        if (queueDZHeight > 0) {
            animateReadyDropZoneMorph();
        }
        void initHistorySection();

        return;
    }

    const hasActiveWork = uploadQueue.some(e => isActiveWork(e));
    if (!hasActiveWork) {
        isUploading = false;
        activeUploadId = null;
        updateQueueTitle(false);
        saveQueueState();
        const completed = uploadQueue.filter(e => e.status === 'completed').length;
        const failed = uploadQueue.filter(e => e.status === 'failed' && !e.validationError).length;
        notifyQueueComplete({ completed, failed });
        resetNotificationToggle();
        setNotificationToggleVisible(false);
        resetWakeLockToggle();
        setWakeLockToggleVisible(false);
    }
}

/**
 * Fire-and-forget wrapper around monitorEntryNormalization for background monitoring.
 * Sets entry.backgroundMonitoring = true so progressAnimator is not used.
 * When the promise resolves: updates DOM, saves state, calls checkAllComplete().
 * @param {QueueEntry} entry
 */
export function monitorEntryNormalizationInBackground(entry) {
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
 * Process a single queue entry -- upload file, handle sync/async response.
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
            xhr.setRequestHeader('X-API-Key', getApiKey());
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            entry.status = 'completed';
            entry.episode = episode;
            entry.progress = 100;
            saveToLocalHistory(episode);
            refreshHistoryList(episode?.id);
        } else if (response.status === 202) {
            const jobResponse = JSON.parse(response.body);
            entry.jobId = jobResponse.jobId;
            entry.title = jobResponse.title || null;
            entry.status = 'normalizing';
            entry.stage = 'Queued';
            entry.progress = 0;
            updateQueueItemInDOM(entry);
            saveQueueState();
            syncPushSession([entry.jobId], uploadQueue);
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

/**
 * Update a queue entry's stage and progress from a job status object.
 * Drives the progressAnimator for stages with progress tracking.
 * @param {QueueEntry} entry
 * @param {Object} job
 */
export function updateEntryFromJobStatus(entry, job) {
    if (!job.stage) {
        return;
    }

    if (job.title && job.title !== entry.title) {
        entry.title = job.title;
    }

    entry.stage = job.stage;
    const stagesWithProgress = ['Analyzing', 'Normalizing', 'Downloading'];
    const isProgressStage = stagesWithProgress.includes(job.stage);
    const progressBar = getEntryProgressBar(entry.id);

    if (entry.backgroundMonitoring) {
        if (isProgressStage) {
            if (progressBar) {
                progressBar.classList.remove('indeterminate');
            }
            if (job.progressPercent != null) {
                entry.progress = job.progressPercent;
                if (progressBar) {
                    progressBar.style.width = job.progressPercent + '%';
                }
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
                invalidateBrowserUploadsCache();
                const uploads = await fetchBrowserUploads();
                const episode = uploads?.find(ep => ep.fileName === entry.fileName) || null;
                entry.status = 'completed';
                entry.episode = episode;
                entry.progress = 100;
                if (episode) {
                    saveToLocalHistory(episode);
                }
                refreshHistoryList(episode?.id);
            } else {
                entry.status = 'failed';
                entry.error = lastStatus?.error || 'Normalization failed';
                if (lastStatus?.authRequired) {
                    showYouTubeCookieDialog();
                }
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
 * Retries transient errors (network failures, 5xx responses) up to maxAttempts times
 * with exponential backoff before marking as failed. This handles mobile browsers where
 * the network may not be immediately available after resuming from background.
 * @param {QueueEntry} entry
 * @returns {Promise<void>}
 */
async function pollEntryNormalization(entry) {
    const pollInterval = 2000;
    const maxAttempts = 5;
    const baseRetryDelay = 2000;
    let consecutiveErrors = 0;

    while (true) {
        // Check if entry was cancelled externally
        if (entry.status === 'cancelled') {
            return;
        }

        try {
            const response = await fetch('/api/jobs/' + entry.jobId, {
                headers: { 'X-API-Key': getApiKey() }
            });

            if (!response.ok) {
                consecutiveErrors++;
                if (consecutiveErrors < maxAttempts) {
                    await new Promise(r => setTimeout(r, baseRetryDelay * Math.pow(2, consecutiveErrors - 1)));
                    continue;
                }
                // Stop polling but don't mark as failed - mergeServerJobs on
                // visibility change will resolve the actual status from the server

                return;
            }

            consecutiveErrors = 0;
            const job = await response.json();

            if (job.status === 'Completed') {
                invalidateBrowserUploadsCache();
                const uploads = await fetchBrowserUploads();
                const episode = uploads?.find(ep => ep.fileName === entry.fileName) || null;
                entry.status = 'completed';
                entry.episode = episode;
                entry.progress = 100;
                if (episode) {
                    saveToLocalHistory(episode);
                }
                updateQueueItemInDOM(entry);
                refreshHistoryList(episode?.id);

                return;
            } else if (job.status === 'Failed') {
                entry.status = 'failed';
                entry.error = job.error || 'Normalization failed';
                if (job.authRequired) {
                    showYouTubeCookieDialog();
                }
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
            // Network errors - retry with backoff (handles mobile resume where
            // network isn't immediately available after returning from background)
            consecutiveErrors++;
            if (consecutiveErrors < maxAttempts) {
                await new Promise(r => setTimeout(r, baseRetryDelay * Math.pow(2, consecutiveErrors - 1)));
                continue;
            }
            // Stop polling but don't mark as failed - mergeServerJobs on
            // visibility change will resolve the actual status from the server

            return;
        }
    }
}

/**
 * Cancel a single queue entry, removing it from the queue and DOM.
 * Queued: removes immediately. Uploading: aborts XHR. Normalizing: closes SSE, POSTs cancel.
 * @param {string} entryId
 */
export async function cancelEntry(entryId) {
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
                    headers: { 'X-API-Key': getApiKey() }
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
export function retryEntry(entryId) {
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
    const hasActive = uploadQueue.some(e => isActiveWork(e));
    updateQueueTitle(hasActive);
    setNotificationToggleVisible(hasActive);
    setWakeLockToggleVisible(hasActive);

    saveQueueState();

    if (!isUploading) {
        processQueue();
    }
}

/**
 * Remove completed and cancelled entries older than 1 hour from the upload queue.
 * Called when the tab regains focus or the PWA resumes, so stale terminal entries
 * don't linger until a full page refresh. Recent entries are kept so the user can
 * see completions that happened while the tab was inactive.
 * Adds cleared jobIds to dismissedJobIds to prevent mergeServerJobs from re-adding them.
 */
export function clearTerminalEntries() {
    const oneHourAgo = Date.now() - 60 * 60 * 1000;
    const terminalEntries = uploadQueue.filter(e =>
        (e.status === 'completed' || e.status === 'cancelled') && e.startedAt < oneHourAgo
    );
    if (terminalEntries.length === 0) {
        return;
    }
    const dismissedJobIds = getDismissedJobIds();
    for (const entry of terminalEntries) {
        if (entry.jobId) {
            dismissedJobIds.set(entry.jobId, Date.now());
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
    saveDismissedJobIds();
    saveQueueState();
    checkAllComplete();
}

/**
 * Save queue state to localStorage (omits File objects and XHR/EventSource refs).
 */
export function saveQueueState() {
    try {
        const serialized = uploadQueue.map(e => ({
            id: e.id,
            fileName: e.fileName,
            title: e.title,
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
export function clearQueueState() {
    uploadQueue = [];
    try {
        localStorage.removeItem(QUEUE_STORAGE_KEY);
    } catch (e) {
        // Ignore
    }
}

/**
 * Restore queue state from localStorage (data only -- does not show or render any UI).
 * Filters out entries older than 1 hour. Entries without startedAt (pre-migration) are treated as expired.
 * Starts SSE monitors for normalizing entries so progress updates arrive during the sync wait.
 * Caller is responsible for showing the appropriate state after server sync completes.
 * @returns {Promise<boolean>} True if entries were restored into uploadQueue
 */
export async function restoreQueueState() {
    const saved = localStorage.getItem(QUEUE_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const entries = tryParseJson(saved);
    if (!entries || !Array.isArray(entries) || entries.length === 0) {
        clearQueueState();

        return false;
    }

    // Filter out entries older than 1 hour (or missing startedAt -- pre-migration)
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

    // Data restored -- caller decides when to show state (after server sync completes)
    // Start SSE monitors now so progress updates arrive during the sync wait
    for (const entry of uploadQueue.filter(e => e.status === 'normalizing' && e.jobId)) {
        monitorEntryNormalizationInBackground(entry);
    }

    return true;
}

/**
 * Initialize queue module by registering callbacks with queue-ui.
 */
export function initQueue() {
    registerQueueCallbacks({
        removeFromQueue,
        cancelEntry,
        retryEntry,
        dismissEntry,
        getActiveId: getActiveUploadId,
        getQueue,
    });
}
