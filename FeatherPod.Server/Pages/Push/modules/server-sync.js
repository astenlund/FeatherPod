import { FEED_ID, DISMISSED_STORAGE_KEY, JOB_TTL_MS, TERMINAL_JOB_STATUSES } from './config.js';
import { isActiveWork, isInUploadPhase, tryParseJson } from './utils.js';
import { getApiKey } from './auth.js';
import { keyAttempts } from './key-attempts.js';
import { getQueue, getActiveUploadId, generateEntryId, addFilesToQueue, monitorEntryNormalizationInBackground, saveQueueState, checkAllComplete, finalizeTerminalEntry } from './queue.js';
import { renderQueueList, updateQueueItemInDOM, removeQueueItemFromDOM } from './queue-ui.js';
import { progressAnimator } from './progress.js';
import { showState, getCurrentState, refreshQueueActivity } from './state.js';
import { refreshHistoryList, initHistorySection, collapseHistoryImmediate } from './history.js';
import { syncPushSession } from './notifications.js';

/** @type {EventSource|null} */
let feedEventsSource = null;
/**
 * @type {Map<string, number>} dismissedAt by jobId, for jobs recent-jobs merges must not add; changed only through
 * loadDismissedJobIds, recordDismissedJobs and dropDismissedJob
 */
let dismissedJobIds = new Map();
/** @type {number} requestedAt of the newest recent-jobs snapshot merged so far (performance.now()) */
let newestMergedSnapshotRequestedAt = -Infinity;
/** @type {{host: string, token: string}|null} */
let localSourceConfig = null;
/** @type {EventSource|null} */
let localSourceEvents = null;
/** @type {Promise<void>|null} The open or opening local source connection, shared by concurrent connectLocalSource calls */
let localSourceConnection = null;
/** @type {Set<number>} */
let localSourceSeen = new Set();
/** @type {number|null} */
let localSourceHeartbeatInterval = null;

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

/**
 * Replace the local source event stream; null drops the connection (push.js closes it on beforeunload), so a later
 * connectLocalSource connects again.
 * @param {EventSource|null} value
 */
export function setLocalSourceEvents(value) {
    localSourceEvents = value;
    if (!value) {
        localSourceConnection = null;
    }
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
 * Read the dismissals saved in localStorage, without the ones older than JOB_TTL_MS.
 * @returns {Map<string, number>} dismissedAt by jobId
 */
function readSavedDismissals() {
    const saved = new Map();
    const entries = tryParseJson(localStorage.getItem(DISMISSED_STORAGE_KEY));
    if (!Array.isArray(entries)) {
        return saved;
    }
    const oneHourAgo = Date.now() - JOB_TTL_MS;
    for (const entry of entries) {
        if (entry?.jobId && entry.dismissedAt && entry.dismissedAt >= oneHourAgo) {
            saved.set(entry.jobId, entry.dismissedAt);
        }
    }

    return saved;
}

/**
 * Apply a change to the dismissals saved in localStorage, dropping expired ones on the way. The saved list is read
 * back and changed job by job rather than replaced with this tab's map, so dismissals that other tabs of this browser
 * saved after this tab loaded survive.
 * @param {(saved: Map<string, number>) => void} change - Mutates the saved dismissals (dismissedAt by jobId)
 */
function updateSavedDismissals(change) {
    try {
        const saved = readSavedDismissals();
        change(saved);
        localStorage.setItem(DISMISSED_STORAGE_KEY, JSON.stringify(Array.from(saved, ([jobId, dismissedAt]) => ({ jobId, dismissedAt }))));
    } catch {
        // Storage unavailable or full: the saved list keeps its previous contents
    }
}

/**
 * Load the dismissals saved in localStorage into dismissedJobIds, dropping expired ones from storage.
 */
export function loadDismissedJobIds() {
    updateSavedDismissals(saved => {
        for (const [jobId, dismissedAt] of saved) {
            dismissedJobIds.set(jobId, dismissedAt);
        }
    });
}

/**
 * Record jobs as dismissed in dismissedJobIds and in localStorage, so recent-jobs merges skip them for JOB_TTL_MS,
 * reloads included.
 * @param {string[]} jobIds
 * @returns {number} The time recorded as their dismissal, which dropDismissedJob takes to drop one again
 */
export function recordDismissedJobs(jobIds) {
    const dismissedAt = Date.now();
    if (jobIds.length > 0) {
        for (const jobId of jobIds) {
            dismissedJobIds.set(jobId, dismissedAt);
        }
        updateSavedDismissals(saved => {
            for (const jobId of jobIds) {
                saved.set(jobId, dismissedAt);
            }
        });
    }

    return dismissedAt;
}

/**
 * Drop a job's dismissal from dismissedJobIds and from localStorage, from each only while it still holds dismissedAt,
 * so a dismissal that another path or tab recorded since is kept.
 * @param {string} jobId
 * @param {number} dismissedAt - The timestamp of the dismissal to drop
 */
export function dropDismissedJob(jobId, dismissedAt) {
    if (dismissedJobIds.get(jobId) === dismissedAt) {
        dismissedJobIds.delete(jobId);
    }
    updateSavedDismissals(saved => {
        if (saved.get(jobId) === dismissedAt) {
            saved.delete(jobId);
        }
    });
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
    let feedEventsConnected = false;
    feedEventsSource.onopen = () => {
        if (feedEventsConnected && navigator.serviceWorker) {
            // Reconnection after drop (likely server restart from deployment) -- check for SW update
            navigator.serviceWorker.ready.then(reg => reg.update()).catch(() => {});
        }
        feedEventsConnected = true;
    };
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
 * @typedef {Object} RecentJobsSnapshot
 * @property {Array<Object>} jobs - The server's recent jobs, terminal ones included
 * @property {number} requestedAt - performance.now() when the request was sent; jobs whose ids the page received later
 *     (see QueueEntry.jobIdReceivedAt) can be missing from the snapshot without being stale, and mergeServerJobs ignores
 *     a snapshot requested before the newest one it has merged
 */

/**
 * Fetch recent normalization jobs (last hour) for this feed from the server.
 * Returns all jobs including terminal ones within the time window.
 * Returns null on any error (silent fallback to local-only state).
 * @returns {Promise<RecentJobsSnapshot|null>}
 */
export async function fetchRecentJobs() {
    const requestedAt = performance.now();
    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/jobs?since=1h', {
            headers: { 'X-API-Key': getApiKey() }
        });
        if (!response.ok) {
            return null;
        }

        return { jobs: await response.json(), requestedAt };
    } catch (err) {
        return null;
    }
}

/**
 * Tear down a queue entry and remove it from the queue: closes any SSE, clears the
 * animator slot, removes the DOM node, splices the entry out of the queue, and resolves
 * a pending monitor. Every step is guarded, so entries without an active SSE or monitor
 * pass through those steps as no-ops.
 * @param {Array<Object>} uploadQueue
 * @param {Object} entry
 */
function removeEntry(uploadQueue, entry) {
    if (entry.eventSource) {
        entry.eventSource.close();
        entry.eventSource = null;
    }
    progressAnimator.removeSlot(entry.id);

    removeQueueItemFromDOM(entry.id);
    const index = uploadQueue.indexOf(entry);
    if (index !== -1) {
        uploadQueue.splice(index, 1);
    }
    if (entry._resolveMonitor) {
        entry._resolveMonitor();
        entry._resolveMonitor = null;
    }
}

/**
 * Remove stale local entries: normalizing/failed entries whose jobId is absent from the
 * server response (job cleaned up or older than one hour). Entries that received their jobId
 * after the snapshot was requested are kept: the snapshot can predate their job. Closes any SSE,
 * clears the animator slot, removes the DOM node, and resolves a pending monitor.
 * @param {Array<Object>} uploadQueue
 * @param {Map<string, Object>} serverJobMap - Server jobs keyed by jobId
 * @param {number} requestedAt - When the snapshot behind serverJobMap was requested (performance.now())
 * @returns {boolean} True if any entry was removed
 */
function removeStaleEntries(uploadQueue, serverJobMap, requestedAt) {
    let removed = false;
    // Entries restored from storage have no jobIdReceivedAt: they received their job ids before any snapshot of this page
    const staleEntries = uploadQueue.filter(e => e.jobId && (e.status === 'normalizing' || e.status === 'failed') && !serverJobMap.has(e.jobId)
        && (e.jobIdReceivedAt ?? -Infinity) < requestedAt);
    for (const stale of staleEntries) {
        removeEntry(uploadQueue, stale);
        removed = true;
    }

    return removed;
}

/**
 * Copy a terminal server job's result fields onto its queue entry and finalize the entry through the shared terminal
 * handling (finalizeTerminalEntry), without awaiting its episode lookup. Used by both reconcileExistingEntries branches
 * that finalize an entry in place.
 * @param {Object} entry
 * @param {Object} serverJob - Terminal job from a recent-jobs snapshot
 */
function finalizeFromServerJob(entry, serverJob) {
    entry.episodeId = serverJob.episodeId || entry.episodeId;
    entry.stage = serverJob.stage || entry.stage;
    entry.normalizationComplete = serverJob.normalizationComplete || false;
    entry.transcriptionStatus = serverJob.transcriptionStatus || null;
    entry.transcriptionError = serverJob.transcriptionError || null;
    finalizeTerminalEntry(entry, serverJob).catch(e => console.error('Failed to finalize entry:', e));
}

/**
 * Reconcile existing local entries against fresh server data, mutating each in place.
 * Applies the status ladder: server cancellation removes the entry; a server-terminal
 * status finalizes a still-normalizing entry; ongoing normalization updates stage,
 * progress, and transcription; and the locally-failed branches recover entries the
 * server reports as active, completed, or cancelled. Ids of entries that changed (but
 * were not removed) are added to changedEntryIds for a targeted DOM update.
 * @param {Array<Object>} uploadQueue
 * @param {Array<Object>} serverJobs
 * @param {Set<number>} changedEntryIds - Mutated in place with ids of changed entries
 * @returns {boolean} True if any entry was removed
 */
function reconcileExistingEntries(uploadQueue, serverJobs, changedEntryIds) {
    let removed = false;
    for (const serverJob of serverJobs) {
        const existing = uploadQueue.find(e => e.jobId === serverJob.jobId);
        if (!existing) {
            continue;
        }

        const serverStatus = serverJob.status;
        const isServerTerminal = TERMINAL_JOB_STATUSES.has(serverStatus);

        if (existing.status === 'normalizing' && serverStatus === 'Cancelled') {
            // Server says cancelled -- remove from queue (consistent with other cancel paths)
            existing.status = 'cancelled';
            removeEntry(uploadQueue, existing);
            removed = true;
        } else if (existing.status === 'normalizing' && isServerTerminal) {
            // Server says terminal but local still normalizing -- finalize in place through the shared terminal
            // handling, since closing the stream means its done handler never runs
            if (existing.eventSource) {
                existing.eventSource.close();
                existing.eventSource = null;
            }
            existing.title = serverJob.title || existing.title;
            existing.progress = 100;
            finalizeFromServerJob(existing, serverJob);
            if (existing._resolveMonitor) {
                existing._resolveMonitor();
                existing._resolveMonitor = null;
            }

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
            if (serverJob.normalizationComplete && !existing.normalizationComplete) {
                existing.normalizationComplete = true;
                changedEntryIds.add(existing.id);
            }
            if (serverJob.transcriptionStatus && serverJob.transcriptionStatus !== existing.transcriptionStatus) {
                existing.transcriptionStatus = serverJob.transcriptionStatus;
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
            monitorEntryNormalizationInBackground(existing, { isResuming: true });
        } else if (existing.status === 'failed' && serverStatus === 'Completed') {
            // Recover locally-failed entries that actually completed on server, through the shared terminal handling
            // so the episode reaches the local history
            existing.error = null;
            finalizeFromServerJob(existing, serverJob);

            changedEntryIds.add(existing.id);
        } else if (existing.status === 'failed' && serverStatus === 'Cancelled') {
            // Server says cancelled - remove from queue
            existing.status = 'cancelled';
            removeEntry(uploadQueue, existing);
            removed = true;
        }
    }

    return removed;
}

/**
 * Build entries for server jobs not already present in the local queue. Skips cancelled
 * jobs, jobs in dismissedJobIds (dismissed, cleared or cancelled entries, including ones saved
 * by other tabs), and jobs whose fileName matches a currently-uploading entry
 * (dedup for in-flight uploads where the SSE event arrives before the XHR 202 response
 * sets the jobId). Entries are returned but not yet pushed onto the queue. Each is stamped
 * with jobIdReceivedAt, so an older snapshot merged later cannot remove it as stale.
 * @param {Array<Object>} uploadQueue
 * @param {Array<Object>} serverJobs
 * @param {Set<string>} existingJobIds - jobIds present in the queue before reconciliation
 * @returns {Array<Object>} Newly built queue entries
 */
function addNewServerEntries(uploadQueue, serverJobs, existingJobIds) {
    const uploadingFileNames = new Set(uploadQueue.filter(isInUploadPhase).map(e => e.fileName));
    const newEntries = [];
    for (const serverJob of serverJobs) {
        if (!existingJobIds.has(serverJob.jobId) && !uploadingFileNames.has(serverJob.fileName) && !dismissedJobIds.has(serverJob.jobId)) {
            const serverStatus = serverJob.status;
            const isServerTerminal = TERMINAL_JOB_STATUSES.has(serverStatus);
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
                jobIdReceivedAt: performance.now(),
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
                normalizationComplete: serverJob.normalizationComplete || false,
                transcriptionStatus: serverJob.transcriptionStatus || null,
                transcriptionError: serverJob.transcriptionError || null,
                _resolveMonitor: null
            });
        }
    }

    return newEntries;
}

/**
 * Merge server-known recent jobs into the local upload queue.
 * The server response contains ALL recent jobs (active + terminal from last hour).
 * Coordinates three behavior-preserving phases over the shared queue:
 * - removeStaleEntries: drops normalizing/failed entries whose jobId is gone from the server,
 *   unless the entry received its jobId after the snapshot was requested
 * - reconcileExistingEntries: applies the status ladder to entries the server still knows
 * - addNewServerEntries: appends unknown server jobs (e.g., from CLI or another tab)
 * then repaints the affected DOM, resumes SSE monitoring, refreshes the queue title and toggles
 * (so work discovered after the queue finished gets its toggles back), and persists queue state.
 * If the snapshot is null (fetch failed), skips reconciliation entirely. A snapshot requested before
 * the newest one already merged is outdated (responses can arrive out of order) and is ignored, so it
 * cannot roll a job's stage and progress back.
 * @param {RecentJobsSnapshot|null} snapshot
 */
export function mergeServerJobs(snapshot) {
    if (snapshot === null) {
        return; // Server fetch failed -- don't reconcile, let SSE handle it
    }
    if (snapshot.requestedAt < newestMergedSnapshotRequestedAt) {
        return;
    }
    newestMergedSnapshotRequestedAt = snapshot.requestedAt;

    const serverJobs = snapshot.jobs;
    const uploadQueue = getQueue();
    const serverJobMap = new Map(serverJobs.map(j => [j.jobId, j]));
    const existingJobIds = new Set(uploadQueue.map(e => e.jobId).filter(Boolean));
    const changedEntryIds = new Set();

    const removedStale = removeStaleEntries(uploadQueue, serverJobMap, snapshot.requestedAt);
    const removedReconciled = reconcileExistingEntries(uploadQueue, serverJobs, changedEntryIds);
    const removedEntries = removedStale || removedReconciled;
    const newEntries = addNewServerEntries(uploadQueue, serverJobs, existingJobIds);

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

    // Start SSE monitoring for new normalizing entries only.
    // isResuming=true: these jobs were already in progress on the server when we
    // discovered them, so the animator must snap to the live value rather than
    // treat the first SSE update as a fresh stage start.
    for (const entry of newEntries) {
        if (entry.status === 'normalizing') {
            monitorEntryNormalizationInBackground(entry, { isResuming: true });
        }
    }

    refreshQueueActivity(hasActive);
    saveQueueState();
    checkAllComplete();
}

/**
 * Connect to a local file server SSE and fetch initial files.
 * Called by push.js startSession (on load, on hashchange re-runs of init, and after key entry) whenever a local
 * source config is set, from the URL fragment or restored from sessionStorage. An API key is set by then but not
 * necessarily validated: init validates concurrently. Offered files are fetched, marked seen and queued only once
 * a key is accepted (see key-attempts.js); until then they are held, so a rejected key never consumes them, and
 * they are queued when a later key (entered on the prompt, or installed by a hashchange) is accepted.
 * Idempotent from the first call: a call made while a connection is open or still being set up gets that connection's
 * promise instead of connecting again, which matters because startSession runs again on every hashchange and key
 * entry, and two runs can overlap while the first waits for its key's verdict.
 * @param {number} attempt - Key attempt of the calling startup run; the initial files wait for its verdict, so on
 *     acceptance they are queued before this resolves. A call that joins an existing connection waits for the attempt
 *     of the call that opened it.
 * @returns {Promise<void>}
 */
export function connectLocalSource(attempt) {
    if (!localSourceConfig) {
        return Promise.resolve();
    }

    if (!localSourceConnection) {
        localSourceConnection = openLocalSource(attempt);
    }

    return localSourceConnection;
}

/**
 * Open the local source connection for connectLocalSource: fetch the initial files and listen for new ones.
 * @param {number} attempt - Key attempt of the startup run that opens it
 */
async function openLocalSource(attempt) {
    const { host, token } = localSourceConfig;
    const baseUrl = `http://${host}`;

    // Restore seen indices from sessionStorage (survives reloads)
    try {
        const stored = sessionStorage.getItem('localSourceSeen');
        if (stored) {
            localSourceSeen = new Set(JSON.parse(stored));
        }
    } catch { /* ignore corrupt data */ }

    /** @type {Map<number, {index: number, name: string}>} Offered files waiting for an accepted key, by index */
    const heldFiles = new Map();
    let drainScheduled = false;

    /** @param {Array<{index: number, name: string}>} items */
    function holdLocalFiles(items) {
        for (const item of items) {
            if (!localSourceSeen.has(item.index)) {
                heldFiles.set(item.index, item);
            }
        }
    }

    /** Fetch and queue the held files once a key is accepted (at once while the current key is accepted). */
    function drainWhenKeyAccepted() {
        if (drainScheduled) {
            return;
        }
        drainScheduled = true;

        keyAttempts.whenAccepted().then(async () => {
            drainScheduled = false;
            const batch = [...heldFiles.values()];
            heldFiles.clear();
            if (batch.length > 0) {
                await fetchAndEnqueueLocalFiles(batch);
            }
        }).catch(e => console.error('Failed to fetch local files:', e));
    }

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
            if (await keyAttempts.waitForVerdict(attempt)) {
                await fetchAndEnqueueLocalFiles(unseen);
            } else {
                holdLocalFiles(unseen);
                drainWhenKeyAccepted();
            }
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

    // Batch window for SSE events -- collects rapid-fire events (e.g., multiple context menu files arriving within
    // milliseconds) so they are queued together in a single addFilesToQueue call, avoiding per-file DOM mutations and
    // animation interruptions.
    let batchTimeout = null;

    // Connect SSE for new files (server replays existing files on connect, so skip seen indices)
    localSourceEvents = new EventSource(`${baseUrl}/api/events?token=${token}`);
    localSourceEvents.addEventListener('new-file', (event) => {
        try {
            const data = JSON.parse(event.data);
            if (!localSourceSeen.has(data.index)) {
                holdLocalFiles([data]);
                if (!batchTimeout) {
                    batchTimeout = setTimeout(() => {
                        batchTimeout = null;
                        drainWhenKeyAccepted();
                    }, 50);
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
            localSourceConnection = null;
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
 * The service worker stores shared files in the 'share-target' cache. Once the key of the calling startup run is
 * accepted (see key-attempts.js), consumes all pending files and adds them to the upload queue. When that key is
 * rejected or superseded, the files stay cached for the startup run of the key that follows (key entry or a hashchange).
 * Startup runs can overlap (two released by one accepted verdict), so each entry is claimed by deleting it before its
 * body is read: only the run whose delete removed it takes the file.
 * @param {number} attempt - Key attempt of the calling startup run
 * @returns {Promise<boolean>} True if this run claimed and queued shared files
 */
export async function consumeSharedFiles(attempt) {
    if (!('caches' in window)) {
        return false;
    }

    const cache = await caches.open('share-target');
    if ((await cache.keys()).length === 0 || !(await keyAttempts.waitForVerdict(attempt))) {
        return false;
    }

    const files = [];
    for (const request of await cache.keys()) {
        const response = await cache.match(request);
        if (!response || !(await cache.delete(request))) {
            // Another startup run claimed this entry
            continue;
        }

        const blob = await response.blob();
        const name = new URL(request.url).pathname.split('/shared/')[1]?.replace(/^\d+-[a-z0-9]+-/, '') || 'shared-audio';
        files.push(new File([blob], name, { type: blob.type }));
    }

    if (files.length === 0) {
        return false;
    }

    addFilesToQueue(files);

    return true;
}
