import { FEED_ID, DISMISSED_STORAGE_KEY } from './config.js';
import { isActiveWork, tryParseJson } from './utils.js';
import { getApiKey } from './auth.js';
import { getQueue, getActiveUploadId, generateEntryId, addFilesToQueue, monitorEntryNormalizationInBackground, saveQueueState, checkAllComplete } from './queue.js';
import { renderQueueList, updateQueueItemInDOM, removeQueueItemFromDOM, rebindProgressAnimator } from './queue-ui.js';
import { progressAnimator } from './progress.js';
import { showState, getCurrentState, updateQueueTitle } from './state.js';
import { refreshHistoryList, initHistorySection, collapseHistoryImmediate } from './history.js';
import { syncPushSession } from './notifications.js';

/** @type {EventSource|null} */
let feedEventsSource = null;
/** @type {Map<string, number>} */
let dismissedJobIds = new Map();
/** @type {{host: string, token: string}|null} */
let localSourceConfig = null;
/** @type {EventSource|null} */
let localSourceEvents = null;
/** @type {Set<number>} */
let localSourceSeen = new Set();
/** @type {number|null} */
let localSourceHeartbeatInterval = null;

export function getDismissedJobIds() {
    return dismissedJobIds;
}

export function getLocalSourceConfig() {
    return localSourceConfig;
}

export function setLocalSourceConfig(value) {
    localSourceConfig = value;
}

export function getFeedEventsSource() {
    return feedEventsSource;
}

export function setFeedEventsSource(value) {
    feedEventsSource = value;
}

export function getLocalSourceEvents() {
    return localSourceEvents;
}

export function setLocalSourceEvents(value) {
    localSourceEvents = value;
}

/**
 * Notify the local file server that a file was successfully uploaded.
 * Fire-and-forget -- errors are silently ignored since the local server
 * may have already shut down.
 * @param {number} localSourceIndex - File index on the local server
 */
export function notifyLocalSourceUploaded(localSourceIndex) {
    if (!localSourceConfig) {
        return;
    }
    const { host, token } = localSourceConfig;
    fetch(`http://${host}/api/files/${localSourceIndex}/uploaded?token=${token}`, { method: 'POST' }).catch(() => {});
}

/**
 * Save dismissedJobIds to localStorage for persistence across page reloads.
 * Each entry stores the original dismissal timestamp for 1-hour TTL filtering.
 */
export function saveDismissedJobIds() {
    try {
        const entries = Array.from(dismissedJobIds, ([jobId, dismissedAt]) => ({ jobId, dismissedAt }));
        localStorage.setItem(DISMISSED_STORAGE_KEY, JSON.stringify(entries));
    } catch (e) {
        // Ignore
    }
}

/**
 * Load dismissedJobIds from localStorage, filtering out entries older than 1 hour.
 */
export function loadDismissedJobIds() {
    const saved = localStorage.getItem(DISMISSED_STORAGE_KEY);
    if (!saved) {
        return;
    }
    const entries = tryParseJson(saved);
    if (!entries || !Array.isArray(entries)) {
        return;
    }
    const oneHourAgo = Date.now() - 60 * 60 * 1000;
    let pruned = false;
    for (const entry of entries) {
        if (entry.jobId && entry.dismissedAt && entry.dismissedAt >= oneHourAgo) {
            dismissedJobIds.set(entry.jobId, entry.dismissedAt);
        } else {
            pruned = true;
        }
    }
    if (pruned) {
        saveDismissedJobIds();
    }
}

/**
 * Open (or reconnect) the feed-level SSE connection for cross-tab/cross-device sync.
 * Listens for "job-added" (merges new jobs into the local queue) and "episode-added"
 * (refreshes the history panel so new uploads appear without a page reload).
 * If the connection is permanently closed (e.g., server error), sets feedEventsSource to null
 * so it can be reconnected on the next tab reactivation.
 */
export function connectFeedEvents() {
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
    feedEventsSource.addEventListener('episode-updated', () => {
        refreshHistoryList();
    });
    feedEventsSource.addEventListener('episode-deleted', () => {
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
 * @returns {Promise<Array<Object>|null>}
 */
export async function fetchRecentJobs() {
    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/jobs?since=1h', {
            headers: { 'X-API-Key': getApiKey() }
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
 * - Recovers locally-failed entries when server reports a different status (completed, active, cancelled)
 * - Adds new entries for unknown server jobs (e.g., from CLI or another tab), skipping cancelled jobs,
 *   user-dismissed jobs, and jobs whose fileName matches a currently-uploading entry (dedup for in-flight uploads)
 * If serverJobs is null (fetch failed), skips reconciliation entirely.
 * @param {Array<Object>|null} serverJobs
 */
export function mergeServerJobs(serverJobs) {
    if (serverJobs === null) {
        return; // Server fetch failed -- don't reconcile, let SSE handle it
    }

    const uploadQueue = getQueue();
    const serverJobMap = new Map(serverJobs.map(j => [j.jobId, j]));
    const existingJobIds = new Set(uploadQueue.map(e => e.jobId).filter(Boolean));
    const changedEntryIds = new Set();
    let removedEntries = false;

    // Remove stale local entries (normalizing/failed with jobId not in server response --
    // means job was cleaned up or is older than 1 hour)
    const staleEntries = uploadQueue.filter(e => e.jobId && (e.status === 'normalizing' || e.status === 'failed') && !serverJobMap.has(e.jobId));
    for (const stale of staleEntries) {
        if (stale.eventSource) {
            stale.eventSource.close();
            stale.eventSource = null;
        }
        progressAnimator.removeSlot(stale.id);
        progressAnimator.removeSlot(stale.id + '-trans');
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
            // Server says cancelled -- remove from queue (consistent with other cancel paths)
            if (existing.eventSource) {
                existing.eventSource.close();
                existing.eventSource = null;
            }
            existing.status = 'cancelled';
            progressAnimator.removeSlot(existing.id);
            progressAnimator.removeSlot(existing.id + '-trans');
            removeQueueItemFromDOM(existing.id);
            uploadQueue.splice(uploadQueue.indexOf(existing), 1);
            if (existing._resolveMonitor) {
                existing._resolveMonitor();
                existing._resolveMonitor = null;
            }
            removedEntries = true;
        } else if (existing.status === 'normalizing' && isServerTerminal) {
            // Server says terminal but local still normalizing -- update to terminal
            if (existing.eventSource) {
                existing.eventSource.close();
                existing.eventSource = null;
            }
            existing.status = serverStatus === 'Completed' ? 'completed' : 'failed';
            existing.error = serverJob.error || null;
            existing.authRequired = serverJob.authRequired || false;
            existing.episodeId = serverJob.episodeId || existing.episodeId;
            existing.title = serverJob.title || existing.title;
            existing.stage = serverJob.stage || existing.stage;
            existing.progress = 100;
            existing.transcriptionStatus = serverJob.transcriptionStatus || null;
            existing.transcriptionProgress = serverJob.transcriptionProgress ?? null;
            existing.transcriptionError = serverJob.transcriptionError || null;
            if (existing.status === 'completed' && existing.localSourceIndex != null) {
                notifyLocalSourceUploaded(existing.localSourceIndex);
            }
            if (existing._resolveMonitor) {
                existing._resolveMonitor();
                existing._resolveMonitor = null;
            }
            progressAnimator.removeSlot(existing.id);
            progressAnimator.removeSlot(existing.id + '-trans');
            changedEntryIds.add(existing.id);
        } else if (existing.status === 'normalizing') {
            existing.title = serverJob.title || existing.title;
            const newStage = serverJob.stage || existing.stage;
            const newProgress = serverJob.progressPercent ?? existing.progress;
            if (existing.stage !== newStage || existing.progress !== newProgress) {
                existing.stage = newStage;
                existing.progress = newProgress;
                changedEntryIds.add(existing.id);
            }
            // Reconcile transcription state (independent track)
            if (serverJob.transcriptionStatus && serverJob.transcriptionStatus !== existing.transcriptionStatus) {
                existing.transcriptionStatus = serverJob.transcriptionStatus;
                existing.transcriptionProgress = serverJob.transcriptionProgress ?? null;
                existing.transcriptionError = serverJob.transcriptionError || null;
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
        } else if (existing.status === 'failed' && serverStatus === 'Completed') {
            // Recover locally-failed entries that actually completed on server
            existing.status = 'completed';
            existing.error = null;
            existing.episodeId = serverJob.episodeId || existing.episodeId;
            existing.stage = serverJob.stage || existing.stage;
            existing.progress = 100;
            existing.transcriptionStatus = serverJob.transcriptionStatus || null;
            existing.transcriptionProgress = serverJob.transcriptionProgress ?? null;
            existing.transcriptionError = serverJob.transcriptionError || null;
            if (existing.localSourceIndex != null) {
                notifyLocalSourceUploaded(existing.localSourceIndex);
            }
            progressAnimator.removeSlot(existing.id);
            progressAnimator.removeSlot(existing.id + '-trans');
            changedEntryIds.add(existing.id);
        } else if (existing.status === 'failed' && serverStatus === 'Cancelled') {
            // Server says cancelled - remove from queue
            existing.status = 'cancelled';
            progressAnimator.removeSlot(existing.id);
            progressAnimator.removeSlot(existing.id + '-trans');
            removeQueueItemFromDOM(existing.id);
            uploadQueue.splice(uploadQueue.indexOf(existing), 1);
            removedEntries = true;
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
                title: serverJob.title || null,
                validationError: false,
                startedAt: serverJob.queuedAt ? new Date(serverJob.queuedAt).getTime() : Date.now(),
                transcriptionStatus: serverJob.transcriptionStatus || null,
                transcriptionProgress: serverJob.transcriptionProgress ?? null,
                transcriptionError: serverJob.transcriptionError || null,
                _resolveMonitor: null
            });
        }
    }

    if (newEntries.length === 0 && changedEntryIds.size === 0 && !removedEntries) {
        return;
    }

    uploadQueue.push(...newEntries);

    // Sync newly discovered active jobs with the server's push session
    const newActiveJobIds = newEntries.filter(e => isActiveWork(e) && e.jobId).map(e => e.jobId);
    if (newActiveJobIds.length > 0) {
        syncPushSession(newActiveJobIds, uploadQueue);
    }

    const currentState = getCurrentState();
    const hasActive = uploadQueue.some(e => isActiveWork(e));

    if (newEntries.length > 0 && currentState === 'ready') {
        // Switch to queue state to show server-discovered jobs
        showState('queue', hasActive, collapseHistoryImmediate);
        renderQueueList();
        void initHistorySection();
    } else if (newEntries.length > 0 && currentState === 'queue') {
        // New entries to add -- must rebuild the full list
        renderQueueList(true);
        rebindProgressAnimator();
    } else if (changedEntryIds.size > 0 && currentState === 'queue') {
        // Only update entries that actually changed -- avoids replacing all DOM elements
        // which would interrupt blur-fade-in animations on recently-added items
        const activeUploadId = getActiveUploadId();
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

    updateQueueTitle(hasActive);
    saveQueueState();
    checkAllComplete();
}

/**
 * Connect to a local file server SSE and fetch initial files.
 * Called after API key validation succeeds when local source params are present in the URL fragment.
 * Idempotent: if already connected (localSourceEvents is set), returns immediately.
 * This matters because init() can be re-invoked via the hashchange listener.
 */
export async function connectLocalSource() {
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
            await fetchAndEnqueueLocalFiles(unseen);
        }
    } catch (e) {
        console.error('Failed to fetch local files:', e);
    }

    // Start heartbeat to keep the local file server alive during uploads
    localSourceHeartbeatInterval = setInterval(() => {
        fetch(`${baseUrl}/api/heartbeat?token=${token}`, { method: 'POST' }).catch(() => {});
    }, 30_000);

    /**
     * Fetch files from the local server by index, convert to File objects, and enqueue.
     * @param {Array<{index: number, name: string}>} items
     */
    async function fetchAndEnqueueLocalFiles(items) {
        const fetched = await Promise.all(items.map(({ index, name }) =>
            fetch(`${baseUrl}/api/files/${index}?token=${token}`)
                .then(r => r.ok ? r.blob() : null)
                .then(blob => {
                    if (!blob) {
                        return null;
                    }
                    localSourceSeen.add(index);
                    const file = new File([blob], name, { type: blob.type });
                    file.localSourceIndex = index;

                    return file;
                })
                .catch(() => null)
        ));
        const validFiles = fetched.filter(f => f !== null);
        if (validFiles.length > 0) {
            addFilesToQueue(validFiles);
        }
        sessionStorage.setItem('localSourceSeen', JSON.stringify([...localSourceSeen]));
    }

    // Batch handler for SSE events -- collects rapid-fire events (e.g., multiple context menu
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
            await fetchAndEnqueueLocalFiles(batch);
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
            if (localSourceHeartbeatInterval) {
                clearInterval(localSourceHeartbeatInterval);
                localSourceHeartbeatInterval = null;
            }
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
export async function consumeSharedFiles() {
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
