import { FEED_ID, JOB_TTL_MS, QUEUE_STORAGE_KEY, STAGES_WITH_PROGRESS, STR_INVALID_KEY, STR_NO_FEED_ACCESS, TERMINAL_JOB_STATUSES, TRANSCRIPTION_ACTIVE_STATUSES, UPLOAD_INTERRUPTED_ERROR } from './config.js';
import { isValidMediaFile, isActiveWork, isInUploadPhase, showToast, tryParseJson } from './utils.js';
import { ATTEMPT_ORDER, SOURCE_GONE_REASON, TRANSCRIPT_RETRY_KIND, attemptOf, compareJobAttempt, isEarlierAttemptAnswer, isNormalizationFailure, isServerRetryable, isTranscriptRetrySettled, isUploadRetryable, transcriptRetryEpisodeId } from './job-attempts.js';
import { getApiKey } from './auth.js';
import { KEY_VERDICT, keyAttempts } from './key-attempts.js';
import { showState, getCurrentState, refreshQueueActivity } from './state.js';
import { progressAnimator } from './progress.js';
import { renderQueueList, updateQueueItemInDOM, updateQueueItemProgress, removeQueueItemFromDOM, getEntryProgressBar, registerQueueCallbacks, createQueueItemElement, animateQueueDropZoneMorph, prepareReadyDropZoneMorph, animateReadyDropZoneMorph, updateQueueItemName } from './queue-ui.js';
import { resetNotificationToggle, syncPushSession, notifyQueueComplete } from './notifications.js';
import { resetWakeLockToggle } from './wake-lock.js';
import { collapseHistoryImmediate, saveToLocalHistory, refreshHistoryList, fetchBrowserUploads, initHistorySection, invalidateBrowserUploadsCache } from './history.js';
import { isJobDismissed, recordCancelledUpload, recordDismissedJobs, confirmDismissedJob, dropDismissedJob, requestJobCancel, notifyLocalSourceUploaded, returnLocalFiles, returnSharedFiles } from './server-sync.js';
import { generateClientUploadId } from './cancelled-uploads.js';
import { selectRestorableEntries } from './queue-restore.js';
import { createStreamBudget } from './stream-budget.js';
import { showYouTubeCookieDialog } from './youtube.js';
import { parseYouTubeFileName } from './youtube-url.js';

/** @type {Array<import('../push.js').QueueEntry>} */
let uploadQueue = [];
/** @type {string|null} */
let activeUploadId = null;
/** @type {boolean} */
let isUploading = false;
/** @type {number} */
let nextEntryId = 0;
/** @type {((errorType: 'invalid'|'no-access') => void)|null} Raises the key prompt; registered by push.js */
let onUploadKeyRejected = null;

/**
 * Most job progress streams (/api/jobs/{jobId}/progress) open at once. On a plain HTTP/1.1 origin Chromium opens at
 * most six connections per host; the feed events stream takes one, an upload one, and one stays free for API calls.
 */
const MAX_JOB_PROGRESS_STREAMS = 3;
/** The job progress streams this page holds open; monitorEntryNormalization polls instead when none is free */
const progressStreams = createStreamBudget(MAX_JOB_PROGRESS_STREAMS);
/** What pollEntryNormalization resolves when its upgradeWhen option ends the loop so a stream can take over */
const POLL_UPGRADE = 'upgrade';

/**
 * Register the handler that raises the key prompt (push.js showNoKeyUI). rejectQueueForKey calls it when an upload
 * answered 401 or 403 rejects the page's accepted key; that call must begin a new key attempt, since the CLI files
 * handed back to intake then wait for the next accepted one.
 * @param {(errorType: 'invalid'|'no-access') => void} callback
 */
export function registerUploadKeyRejectedCallback(callback) {
    onUploadKeyRejected = callback;
}

/**
 * Build the descriptor youtube.js needs to re-submit an import after a cookie upload.
 * The video id and format come from the server-shaped fileName (<videoId>.m4a|.mp4),
 * so entries discovered from other devices qualify too; null for any other name.
 * @param {Object} entry - Failed queue entry whose job reported authRequired
 * @returns {{videoId: string, format: 'audio'|'video', title: string|null, entryId: string}|null}
 */
function cookieRetryFor(entry) {
    const parsed = parseYouTubeFileName(entry.fileName);

    return parsed ? { ...parsed, title: entry.title ?? null, entryId: entry.id } : null;
}

/**
 * Build a minimal episode object from job status data for local history,
 * used as a fallback when the server cache hasn't refreshed yet.
 * @param {Object} jobStatus - Terminal job status passed to finalizeCompletedEntry
 * @param {Object} entry - Queue entry
 * @returns {Object|null}
 */
function buildFallbackEpisode(jobStatus, entry) {
    const id = jobStatus.episodeId || entry.episodeId;
    if (!id) {
        return null;
    }

    return {
        id,
        fileName: entry.fileName,
        title: jobStatus.title || entry.title || entry.fileName,
        uploadedAt: new Date().toISOString()
    };
}

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

        const valid = isValidMediaFile(file);
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
            localSourceIndex: file.localSourceIndex ?? null,
            shareCacheUrl: file.shareCacheUrl ?? null,
            validationError: !valid,
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
        refreshQueueActivity(hasActive);
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
 * Records the dismissal for the job attempt the entry follows (see dismissal-store.js), so a later attempt of the job (a
 * normalization retry made elsewhere) is never hidden by it, and cancels a job that is not completed on the server for
 * that attempt only.
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
    const attempt = attemptOf(entry);
    if (jobId) {
        recordDismissedJobs([{ jobId, attempt }]);
    }

    uploadQueue.splice(index, 1);
    removeQueueItemFromDOM(entryId);
    progressAnimator.removeSlot(entryId);

    saveQueueState();
    checkAllComplete();

    // Mark as cancelled server-side so mergeServerJobs won't re-add it (best-effort). The cancel names the attempt this
    // view showed, so a dismissal of an attempt another tab has since retried leaves the retry running.
    if (jobId && entry.status !== 'completed') {
        await requestJobCancel(jobId, attempt);
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
 * Either way both toggles end unpressed: a hidden bell must not keep its push subscription for work
 * discovered later, and a hidden wake-lock toggle must not re-acquire the lock on tab reactivation.
 * An emptied queue leaves the key prompt showing when it is up: the prompt owns the page whatever view is current.
 */
export function checkAllComplete() {
    if (uploadQueue.length === 0) {
        clearQueueState();
        resetNotificationToggle();
        resetWakeLockToggle();
        if (getCurrentState() === 'no-key') {
            return;
        }

        const queueDZHeight = document.getElementById('queue-drop-zone')?.getBoundingClientRect().height || 0;
        if (queueDZHeight > 0) {
            prepareReadyDropZoneMorph(queueDZHeight);
        }
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
        refreshQueueActivity(false);
        saveQueueState();
        const completed = uploadQueue.filter(e => e.status === 'completed').length;
        const failed = uploadQueue.filter(e => e.status === 'failed' && !e.validationError).length;
        notifyQueueComplete({ completed, failed });
        resetNotificationToggle();
        resetWakeLockToggle();
    }
}

/**
 * Fire-and-forget wrapper around monitorEntryNormalization for background monitoring.
 * When the promise resolves: updates DOM, saves state, calls checkAllComplete().
 * Pass `isResuming: true` for any caller that picks up an in-progress server job
 * mid-stream (page reload restore, failed-entry recovery, server-discovered jobs)
 * so the first live update snaps to the live value via the animator's restoring
 * branch instead of being treated as a fresh stage start.
 * @param {QueueEntry} entry
 * @param {{ isResuming?: boolean }} [options]
 */
export function monitorEntryNormalizationInBackground(entry, { isResuming = false } = {}) {
    if (isResuming) {
        progressAnimator.markRestoring(entry.id, entry.fileSize);
    }
    monitorEntryNormalization(entry).then(() => {
        if (uploadQueue.includes(entry)) {
            updateQueueItemInDOM(entry);
        }
        repaintTranscriptRetryOrigins(entry);
        saveQueueState();
        checkAllComplete();
    });
}

/**
 * Repaint the entries that offer "Retry transcript" for the episode of a transcript retry job whose monitoring ended,
 * since they hide the action while a retry of their episode is active or after one published the transcript (see hasSettlingTranscriptRetry).
 * @param {QueueEntry} entry
 */
function repaintTranscriptRetryOrigins(entry) {
    if (entry.kind !== TRANSCRIPT_RETRY_KIND || !entry.episodeId) {
        return;
    }
    for (const other of uploadQueue) {
        if (other !== entry && transcriptRetryEpisodeId(other) === entry.episodeId) {
            updateQueueItemInDOM(other);
        }
    }
}

/**
 * Process a single queue entry -- upload file, handle sync/async response.
 * Each upload sends a fresh clientUploadId, which the server stores on the job it creates, so a job whose response
 * never arrives (an upload cancelled during 'saving') can still be recognized; a retry sends a new one, so it is never
 * taken for the cancelled upload it repeats.
 * @param {QueueEntry} entry
 */
async function processEntry(entry) {
    entry.status = 'uploading';
    entry.progress = 0;
    entry.clientUploadId = generateClientUploadId();
    updateQueueItemInDOM(entry);

    saveQueueState();

    const progressBar = getEntryProgressBar(entry.id);
    progressAnimator.startWithAssumption('Uploading', progressBar, entry.id, entry.fileSize);

    const formData = new FormData();
    formData.append('file', entry.file);
    formData.append('clientUploadId', entry.clientUploadId);
    const sentKey = getApiKey();

    try {
        const response = await new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            entry.xhr = xhr;

            xhr.upload.addEventListener('progress', (e) => {
                if (e.lengthComputable) {
                    const percent = Math.round((e.loaded / e.total) * 100);
                    entry.progress = percent;
                    progressAnimator.setTarget(percent, 'Uploading', entry.id);
                    updateQueueItemProgress(entry);
                }
            });

            // Upload body fully sent -- server is now persisting the file and
            // creating the job entity. The 202 response arrives via xhr.onload
            // a few seconds later. Flip the label to "Saving" so the user
            // sees progress instead of a stuck "Uploading 100%".
            xhr.upload.addEventListener('load', () => {
                entry.progress = 100;
                entry.status = 'saving';
                updateQueueItemInDOM(entry);
            });

            xhr.onload = () => {
                entry.xhr = null;
                progressAnimator.reset(entry.id);
                progressAnimator.removeSlot(entry.id);
                resolve({ status: xhr.status, body: xhr.responseText });
            };

            xhr.onerror = () => {
                entry.xhr = null;
                progressAnimator.reset(entry.id);
                progressAnimator.removeSlot(entry.id);
                reject(new Error('Network error'));
            };

            xhr.onabort = () => {
                entry.xhr = null;
                progressAnimator.reset(entry.id);
                progressAnimator.removeSlot(entry.id);
                reject(new DOMException('Upload cancelled', 'AbortError'));
            };

            const uploadUrl = '/api/feeds/' + FEED_ID + '/episodes?normalize=true&source=Browser';
            xhr.open('POST', uploadUrl);
            xhr.setRequestHeader('X-API-Key', sentKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            entry.status = 'completed';
            entry.episode = episode;
            entry.progress = 100;
            if (entry.localSourceIndex != null) {
                notifyLocalSourceUploaded(entry.localSourceIndex);
            }
            saveToLocalHistory(episode);
            refreshHistoryList(episode?.id);
        } else if (response.status === 202) {
            const jobResponse = JSON.parse(response.body);
            entry.jobId = jobResponse.jobId;
            entry.attempt = attemptOf(jobResponse);
            entry.kind = jobResponse.kind ?? null;
            entry.jobIdReceivedAt = performance.now();
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
            rejectQueueForKey(entry, response.status === 401 ? 'invalid' : 'no-access', sentKey);

            return;
        } else {
            const error = tryParseJson(response.body);
            entry.status = 'failed';
            entry.error = error?.error || 'Upload failed';
        }
    } catch (err) {
        if (err.name === 'AbortError') {
            finalizeCancelledEntry(entry);
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
 * Handle an upload answered 401 (invalid key) or 403 (no access to this feed). Nothing more is uploaded with that key:
 * the rejected entry and every entry still queued behind it fail with the reason and a Retry button, and those whose
 * files came from the CLI local source or the share target are then handed back to intake (returnLocalFiles,
 * returnSharedFiles) and leave the queue once intake has taken them back, so they are offered again once a key is
 * accepted, in this tab or after a reload. Files that cannot go back (added by hand, a local source connection that
 * has closed, a share-target cache that is unavailable) stay in the queue as failed with Retry.
 * The key prompt is raised with the reason only when the rejected key is still the page's key under an accepted key
 * attempt: the server has then rejected the key the page is using. It is raised before the CLI files are handed back,
 * because it begins the new key attempt they wait for. Otherwise the page's own key handling owns the decision (a
 * newer key is already accepted, or init is still checking the key the upload carried and may fall back to the saved
 * key), so the answer neither clears a key nor begins an attempt.
 * @param {QueueEntry} rejected - The entry whose upload was rejected
 * @param {'invalid'|'no-access'} errorType
 * @param {string|null} sentKey - The key the rejected upload carried
 */
function rejectQueueForKey(rejected, errorType, sentKey) {
    const raisePrompt = sentKey === getApiKey() && keyAttempts.verdict === KEY_VERDICT.ACCEPTED;
    const error = errorType === 'invalid' ? STR_INVALID_KEY : STR_NO_FEED_ACCESS;
    const affected = [rejected, ...uploadQueue.filter(e => e.status === 'queued')];
    for (const e of affected) {
        e.status = 'failed';
        e.error = error;
        updateQueueItemInDOM(e);
    }
    activeUploadId = null;
    isUploading = false;
    if (raisePrompt) {
        onUploadKeyRejected?.(errorType);
    }

    const fromLocalSource = affected.filter(e => e.localSourceIndex != null);
    if (returnLocalFiles(fromLocalSource.map(e => ({ index: e.localSourceIndex, name: e.fileName, file: e.file })))) {
        removeHandedBackEntries(fromLocalSource);
    }

    // After the prompt, so an emptied queue leaves it showing (whoever raised it) instead of switching to the ready view;
    // once, when the shared files' hand-back has settled, so the queue-complete notice is not sent twice
    const settle = () => {
        saveQueueState();
        checkAllComplete();
    };
    const fromShare = affected.filter(e => e.shareCacheUrl);
    if (fromShare.length === 0) {
        settle();

        return;
    }

    returnSharedFiles(fromShare.map(e => ({ url: e.shareCacheUrl, file: e.file })))
        .then(returned => removeHandedBackEntries(fromShare.filter(e => returned.includes(e.shareCacheUrl))))
        .catch(e => console.error('Failed to hand shared files back:', e))
        .finally(settle);
}

/**
 * Remove queue entries whose files intake took back (see rejectQueueForKey), skipping any no longer in the queue or
 * already retried (no longer failed), since intake now holds their files.
 * @param {Array<QueueEntry>} entries
 */
function removeHandedBackEntries(entries) {
    for (const e of entries) {
        const index = uploadQueue.indexOf(e);
        if (index !== -1 && e.status === 'failed') {
            uploadQueue.splice(index, 1);
            removeQueueItemFromDOM(e.id);
        }
    }
}

/**
 * Record that a status answer for the entry's job (an SSE event, a poll answer or a terminal finalization) has just
 * been applied, so the non-terminal state of a recent-jobs snapshot requested before it cannot undo it (see
 * QueueEntry.lastLiveUpdateAt).
 * @param {QueueEntry} entry
 */
function markLiveUpdate(entry) {
    entry.lastLiveUpdateAt = performance.now();
}

/**
 * The time an attempt started on this device's clock: the job's queuedAt, or now when it carries none (or an unparsable
 * one).
 * @param {Object} job
 * @returns {number} Epoch milliseconds
 */
function attemptStartedAt(job) {
    const queuedAt = job?.queuedAt ? Date.parse(job.queuedAt) : NaN;

    return Number.isFinite(queuedAt) ? queuedAt : Date.now();
}

/**
 * Make an entry follow a later attempt of its job (a normalization retry, made here or elsewhere, reset the job): the
 * entry takes the job's attempt, and everything it kept about the earlier attempt is reset, so the new attempt is fresh
 * to every consumer. The terminal claim and QueueEntry.lastLiveUpdateAt are cleared (no answer about the new attempt has
 * been applied yet; a caller applying one stamps it afterwards), jobIdReceivedAt is stamped now, so a recent-jobs
 * snapshot requested before the reset cannot remove the entry as stale, and startedAt becomes the attempt's start, so
 * a retry running past the first hour of the original job is still restored after a reload and kept by
 * clearTerminalEntries. Stage, progress and both tracks' state come from the job. The caller sets the entry's status.
 * @param {QueueEntry} entry
 * @param {Object} job - Status of the job's later attempt
 * @param {{startedAt?: number}} [options] - startedAt: the attempt's start (default: the job's queuedAt)
 */
export function adoptJobAttempt(entry, job, { startedAt = attemptStartedAt(job) } = {}) {
    entry.attempt = attemptOf(job);
    entry.terminalClaim = null;
    entry.lastLiveUpdateAt = null;
    entry.jobIdReceivedAt = performance.now();
    entry.startedAt = startedAt;
    entry.normalizationFailed = false;
    entry.error = null;
    entry.stage = job.stage || 'Queued';
    entry.progress = job.progressPercent ?? 0;
    entry.normalizationComplete = job.normalizationComplete ?? false;
    entry.transcriptionStatus = job.transcriptionStatus ?? null;
    entry.transcriptionError = job.transcriptionError ?? null;
}

/**
 * Decide whether a live status answer for the entry's job applies to the attempt the entry follows: an answer for an
 * earlier attempt (read before a normalization retry reset the job) does not, and leaves the entry untouched; an answer
 * for a later attempt (another tab or device retried the job) applies after the entry adopts that attempt.
 * @param {QueueEntry} entry
 * @param {Object|null} job
 * @returns {boolean} Whether the answer applies
 */
function admitAttemptAnswer(entry, job) {
    if (isEarlierAttemptAnswer(entry, job)) {
        return false;
    }
    if (job && compareJobAttempt(entry, job) === ATTEMPT_ORDER.LATER) {
        adoptJobAttempt(entry, job);
    }

    return true;
}

/**
 * Update a queue entry from a live job status event (SSE progress or a poll answer). An answer for an earlier attempt
 * than the entry follows changes nothing, and one for a later attempt is adopted first (see admitAttemptAnswer). Marks
 * the live update, tracks transcription state, applies title changes to the model and its label, then updates the
 * progress bar (including the indeterminate "Transcribing" state when normalization is complete).
 * @param {QueueEntry} entry
 * @param {Object} job - JobStatusResponse from server
 * @returns {boolean} Whether the answer was applied
 */
export function updateEntryFromJobStatus(entry, job) {
    if (!admitAttemptAnswer(entry, job)) {
        return false;
    }
    markLiveUpdate(entry);
    if (job.title && job.title !== entry.title) {
        entry.title = job.title;
        updateQueueItemName(entry);
    }

    updateTranscriptionState(entry, job);
    updateNormalizationProgress(entry, job);

    return true;
}

/**
 * Update the displayed title of any queue entries that reference the given episode.
 * Matches by the resolved episode ID (terminal entries) or the job's episodeId
 * (set by server-sync during normalization). Called when an episode is renamed
 * in the history panel so the queue reflects the new name.
 * @param {string} episodeId
 * @param {string} newTitle
 */
export function updateQueueTitleForEpisode(episodeId, newTitle) {
    let changed = false;

    for (const entry of uploadQueue) {
        const isMatch = entry.episode?.id === episodeId || entry.episodeId === episodeId;
        if (!isMatch) {
            continue;
        }

        entry.title = newTitle;
        updateQueueItemName(entry);
        if (entry.episode) {
            entry.episode = { ...entry.episode, title: newTitle };
        }

        changed = true;
    }

    if (changed) {
        saveQueueState();
    }
}

/**
 * Update the normalization progress bar from a job status event.
 * Records job.stage on the entry first, so the status label and the bar read the same stage. While transcription is
 * active and normalization is complete or finishing, shows the indeterminate bar (the label reads "Transcribing");
 * otherwise drives the bar from job.stage, and no-ops if the event carries no stage.
 * @param {QueueEntry} entry
 * @param {Object} job
 */
function updateNormalizationProgress(entry, job) {
    const progressBar = getEntryProgressBar(entry.id);
    if (job.stage) {
        entry.stage = job.stage;
    }

    // After normalization completes, show indeterminate bar while transcription runs
    const transcriptionActive = TRANSCRIPTION_ACTIVE_STATUSES.has(entry.transcriptionStatus);
    if (transcriptionActive && (entry.normalizationComplete || entry.stage === 'Finishing')) {
        progressAnimator.reset(entry.id);
        if (progressBar) {
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
        }
        updateQueueItemProgress(entry);

        return;
    }

    if (!job.stage) {
        return;
    }

    const isProgressStage = STAGES_WITH_PROGRESS.includes(job.stage);

    if (isProgressStage) {
        if (progressBar) {
            progressBar.classList.remove('indeterminate');
        }

        if (progressAnimator.getCurrentStage(entry.id) !== job.stage) {
            progressAnimator.startWithAssumption(job.stage, progressBar, entry.id);
        }

        if (job.progressPercent != null) {
            entry.progress = job.progressPercent;
            progressAnimator.setTarget(job.progressPercent, job.stage, entry.id, job.tickMs);
        }

        progressAnimator.start(progressBar, entry.id);
    } else {
        progressAnimator.reset(entry.id);
        if (progressBar) {
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
        }
    }

    updateQueueItemProgress(entry);
}

/**
 * Track transcription state fields from a job status event.
 * Must run before updateNormalizationProgress so the indeterminate bar
 * logic can check normalizationComplete + transcriptionStatus.
 * @param {QueueEntry} entry
 * @param {Object} job
 */
function updateTranscriptionState(entry, job) {
    if (job.normalizationComplete != null && job.normalizationComplete !== entry.normalizationComplete) {
        entry.normalizationComplete = job.normalizationComplete;
    }
    if (job.transcriptionStatus && job.transcriptionStatus !== entry.transcriptionStatus) {
        entry.transcriptionStatus = job.transcriptionStatus;
        entry.transcriptionError = job.transcriptionError;
    }
}

/**
 * Apply a job's terminal status to its queue entry: the single terminal handling shared by the SSE progress (Cancelled)
 * and done handlers, the poll fallback and the recent-jobs merge (which finalizes a still-normalizing entry in place,
 * and recovers a locally failed entry whose job completed). Cancelled goes through finalizeCancelledEntry and Completed
 * through finalizeCompletedEntry; any other status marks the entry failed with the job's error, opens the YouTube
 * cookie dialog when the job reports authRequired, and removes the progress slot. An answer for another job than the
 * entry's current one is ignored, as a defensive check: the poll fallback already drops answers for a job its entry no
 * longer runs, and an upload retry only follows a closed stream. An answer for an earlier attempt of the job than the
 * entry follows (read before a normalization retry reset the job) is ignored too, whatever its status, so it never
 * finalizes, fails or removes the entry that follows the new attempt; an answer for a later attempt (the job was retried
 * elsewhere) is applied after the entry adopts that attempt (see admitAttemptAnswer). A terminal job status (Completed,
 * Failed or Cancelled) is applied once per entry, job and attempt: the first such call claims the attempt
 * synchronously, and while the entry stays finalized, every later call for that attempt returns without effect, so a
 * terminal answer that arrives after another path already finalized the entry neither notifies the CLI local source
 * again nor repeats the history save or the cookie dialog. The claim lapses as soon as the entry is active work again (a
 * recent-jobs merge that revives a failed entry for the same job, or a normalization retry of it), so the revived
 * monitor's terminal answer finalizes it again; adopting a later attempt clears it too. A missing or non-terminal status
 * (an SSE done event whose last progress event was not terminal) still marks the entry failed but claims nothing, since
 * the server has not decided the job, so a merge that later finds the job Completed can still recover the entry. An
 * upload retry gives the entry a new job, whose terminal status is handled afresh. A Cancelled answer for an attempt
 * already claimed as Failed (the server cancels a failed job when it is dismissed) is likewise ignored here; the merge's
 * failed-and-Cancelled branch removes such an entry. A Failed answer records whether the job failed in normalization
 * (QueueEntry.normalizationFailed), which decides whether Retry asks the server first. The claim is in memory only (not
 * saved with the queue), so a restored entry starts unclaimed. The entry's status is terminal when this returns its
 * promise; a Completed entry's episode lookup and history save finish when the promise resolves. Every applied call
 * stamps QueueEntry.lastLiveUpdateAt, since the poll fallback's terminal answers reach only this function; a merge that
 * finalizes the entry stamps it too, which is harmless because the stamp gates only a snapshot's non-terminal statuses
 * and an attempt that reached a terminal status never returns to one (a normalization retry starts a new attempt, whose
 * answers the snapshot rules compare by attempt first).
 * @param {QueueEntry} entry
 * @param {Object|null} job - Terminal job status (SSE lastStatus, poll response or recent-jobs snapshot job)
 * @returns {Promise<void>}
 */
export async function finalizeTerminalEntry(entry, job) {
    if (job?.jobId && job.jobId !== entry.jobId) {
        return;
    }
    if (!admitAttemptAnswer(entry, job)) {
        return;
    }
    markLiveUpdate(entry);
    if (TERMINAL_JOB_STATUSES.has(job?.status)) {
        const claim = entry.jobId + '#' + attemptOf(entry);
        if (entry.terminalClaim === claim && !isActiveWork(entry)) {
            return;
        }
        entry.terminalClaim = claim;
    }

    if (job?.status === 'Cancelled') {
        finalizeCancelledEntry(entry);
    } else if (job?.status === 'Completed') {
        await finalizeCompletedEntry(entry, job);
    } else {
        entry.status = 'failed';
        entry.error = job?.error || 'Normalization failed';
        entry.normalizationFailed = isNormalizationFailure(job);
        if (job?.authRequired) {
            showYouTubeCookieDialog(cookieRetryFor(entry));
        }
        progressAnimator.removeSlot(entry.id);
        updateQueueItemInDOM(entry);
    }
}

/**
 * Apply terminal "completed" state to an entry. It first applies the job's transcription state, since a job can
 * complete with a failed transcription (transcription finishing last) and the poll fallback may see only that Completed
 * answer, so every Completed answer (the poll's, the SSE done handler's last status and the merges') carries the job's
 * final transcription status. The status, the CLI local-source notification and the progress slot removal happen before the
 * first await, so a caller that does not await (the recent-jobs merge) already sees the entry terminal. Then it
 * refreshes the uploads cache, resolves the episode (by the job's bound episode id when known, else by file name), saves it to local history, and repaints the queue item and saves
 * the queue state if the entry is still in the upload queue. Called only through finalizeTerminalEntry.
 * @param {QueueEntry} entry
 * @param {Object} jobStatus - Job status (SSE lastStatus, poll response or recent-jobs snapshot job)
 * @returns {Promise<void>}
 */
async function finalizeCompletedEntry(entry, jobStatus) {
    updateTranscriptionState(entry, jobStatus);
    entry.status = 'completed';
    entry.progress = 100;
    if (entry.localSourceIndex != null) {
        notifyLocalSourceUploaded(entry.localSourceIndex);
    }
    progressAnimator.removeSlot(entry.id);

    invalidateBrowserUploadsCache();
    const uploads = await fetchBrowserUploads();
    // By the id the job is bound to when known: episodes of different sizes can share a file name
    const boundId = jobStatus?.episodeId || entry.episodeId;
    const episode = uploads?.find(ep => (boundId ? ep.id === boundId : ep.fileName === entry.fileName)) || null;
    entry.episode = episode;
    const historyEpisode = episode || buildFallbackEpisode(jobStatus, entry);
    if (historyEpisode) {
        saveToLocalHistory(historyEpisode);
    }
    if (uploadQueue.includes(entry)) {
        updateQueueItemInDOM(entry);
        saveQueueState();
    }
    refreshHistoryList(historyEpisode?.id);
}

/**
 * Apply terminal "cancelled" state to an entry: record its job attempt as dismissed, clear the progress animator slot, drop the
 * DOM item, and remove it from the upload queue if it is still there (a defensive check: callers reach it only for an
 * entry still in the queue, since the poll fallback stops once its entry has left the queue). Shared by the
 * aborted-upload path in processEntry, cancelEntry and finalizeTerminalEntry (the SSE progress and done handlers and
 * the poll fallback), so a cancel made on this page and one made elsewhere (another tab or device, a direct API call)
 * both keep a recent-jobs snapshot requested before the cancel from re-adding the job. cancelEntry records the
 * dismissal as pending, since its cancel request has not settled yet; the other callers apply a cancel the server
 * already reported and record an ordinary one. Callers are responsible for any monitor-specific teardown (e.g.,
 * finishMonitoring).
 * @param {QueueEntry} entry
 * @param {{pending?: boolean}} [options] - pending: record the dismissal as pending (see recordDismissedJobs)
 * @returns {number|undefined} The time the job's dismissal was recorded, or undefined for an entry without a job
 */
function finalizeCancelledEntry(entry, options) {
    entry.status = 'cancelled';
    const dismissedAt = entry.jobId ? recordDismissedJobs([{ jobId: entry.jobId, attempt: attemptOf(entry) }], options) : undefined;
    progressAnimator.removeSlot(entry.id);
    removeQueueItemFromDOM(entry.id);
    const index = uploadQueue.indexOf(entry);
    if (index !== -1) {
        uploadQueue.splice(index, 1);
    }
    saveQueueState();

    return dismissedAt;
}

/**
 * Monitor normalization for a queue entry via SSE with polling fallback.
 * Returns a Promise that resolves when normalization completes/fails/cancels.
 * At most MAX_JOB_PROGRESS_STREAMS job progress streams are open at once (see progressStreams). An entry refused a
 * stream polls instead, and switches to a stream once one closes, provided it is still this monitor's entry: still
 * 'normalizing', in the queue, on the same job and not taken over by a newer monitor. An entry that fell back to
 * polling because its stream failed keeps polling, as before, so a failing stream is not reopened every round.
 * The monitor acts on the entry only while it is the entry's current monitor (QueueEntry._resolveMonitor is its own
 * resolver): a monitor a normalization retry or a merge revival replaced stops at its next stream event or poll round
 * and changes nothing. Stream events for an earlier attempt than the entry follows are skipped (see
 * updateEntryFromJobStatus); when a stream ends with only such events, the monitor polls for the current attempt
 * instead of failing the entry.
 * @param {QueueEntry} entry
 * @returns {Promise<void>}
 */
function monitorEntryNormalization(entry) {
    return new Promise((resolve) => {
        entry._resolveMonitor = resolve;
        const isCurrentMonitor = () => entry._resolveMonitor === resolve;

        const progressBar = getEntryProgressBar(entry.id);
        if (progressBar) {
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
        }
        progressAnimator.reset(entry.id);
        progressAnimator.setFileSize(entry.id, entry.fileSize);

        const jobId = entry.jobId;
        const sseUrl = '/api/jobs/' + jobId + '/progress';
        const isStillMonitored = () => isCurrentMonitor() && entry.status === 'normalizing' && entry.jobId === jobId && uploadQueue.includes(entry);

        // Resolve this monitor once polling ends. The entry may already belong to a newer monitor by then (a retry, or
        // a merge that revived it), so only this monitor's own resolver is cleared.
        function finishPolling() {
            if (isCurrentMonitor()) {
                entry._resolveMonitor = null;
            }
            resolve();
        }

        function pollUntilDone() {
            pollEntryNormalization(entry, { isCurrentMonitor }).then(finishPolling);
        }

        // Poll while every stream slot is taken; once one frees, try for a stream again
        function pollUntilStreamSlot() {
            pollEntryNormalization(entry, { upgradeWhen: progressStreams.hasRoom, isCurrentMonitor }).then((outcome) => {
                if (outcome === POLL_UPGRADE && isStillMonitored()) {
                    openStreamOrPoll();

                    return;
                }
                finishPolling();
            });
        }

        function openStreamOrPoll() {
            if (typeof EventSource === 'undefined') {
                pollUntilDone();

                return;
            }

            /** @type {EventSource|null} */
            let eventSource = null;
            if (!progressStreams.tryAcquire(() => eventSource !== null && eventSource.readyState !== EventSource.CLOSED)) {
                pollUntilStreamSlot();

                return;
            }
            eventSource = new EventSource(sseUrl);
            listenToStream(eventSource);
        }

        /** @param {EventSource} eventSource */
        function listenToStream(eventSource) {
            entry.eventSource = eventSource;
            let lastStatus = null;
            let sawEarlierAttempt = false;
            let connectionEstablished = false;
            let jobFinished = false;

            function finishMonitoring() {
                if (isCurrentMonitor()) {
                    entry.eventSource = null;
                    entry._resolveMonitor = null;
                }
                resolve();
            }

            // A monitor that a normalization retry or a merge revival replaced closes its stream and leaves the entry alone
            function abandonIfReplaced() {
                if (isCurrentMonitor()) {
                    return false;
                }
                clearTimeout(connectionTimeout);
                jobFinished = true;
                eventSource.close();
                resolve();

                return true;
            }

            const connectionTimeout = setTimeout(() => {
                if (!connectionEstablished) {
                    eventSource.close();
                    entry.eventSource = null;
                    pollUntilDone();
                }
            }, 5000);

            eventSource.onopen = () => {
                connectionEstablished = true;
                clearTimeout(connectionTimeout);
            };

            eventSource.addEventListener('progress', (e) => {
                if (abandonIfReplaced()) {
                    return;
                }
                const parsed = tryParseJson(e.data);
                if (parsed) {
                    if (isEarlierAttemptAnswer(entry, parsed)) {
                        sawEarlierAttempt = true;

                        return;
                    }
                    lastStatus = parsed;
                    if (parsed.status === 'Cancelled') {
                        clearTimeout(connectionTimeout);
                        jobFinished = true;
                        eventSource.close();
                        void finalizeTerminalEntry(entry, parsed);
                        finishMonitoring();

                        return;
                    }
                    updateEntryFromJobStatus(entry, parsed);
                    updateQueueItemProgress(entry);
                }
            });

            eventSource.addEventListener('done', async () => {
                if (abandonIfReplaced()) {
                    return;
                }
                clearTimeout(connectionTimeout);
                jobFinished = true;
                eventSource.close();

                if (!lastStatus && sawEarlierAttempt) {
                    // The stream only reported an attempt from before the entry's, so it says nothing about the current one
                    entry.eventSource = null;
                    pollUntilDone();

                    return;
                }

                await finalizeTerminalEntry(entry, lastStatus);
                finishMonitoring();
            });

            // Named 'error' event from server (e.g., job not found)
            eventSource.addEventListener('error', (e) => {
                if (!e.data || abandonIfReplaced()) {
                    return;
                }
                clearTimeout(connectionTimeout);
                jobFinished = true;
                eventSource.close();
                const data = tryParseJson(e.data);
                markLiveUpdate(entry);
                entry.status = 'failed';
                entry.error = data?.error || 'An error occurred';
                progressAnimator.removeSlot(entry.id);

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
                pollUntilDone();
            };
        }

        openStreamOrPoll();
    });
}

/**
 * Poll normalization job status for a queue entry: the fallback monitorEntryNormalization uses when
 * EventSource is unavailable, when the SSE connection is not established in time, and when the
 * stream errors or closes before its done event, and the stand-in for a stream while every progress stream slot is
 * taken (options.upgradeWhen). Retries transient errors (network failures,
 * non-OK responses) with exponential backoff; after maxAttempts consecutive failures it stops polling
 * without marking the entry failed, leaving its status to the next recent-jobs merge. This handles mobile
 * browsers where the network may not be immediately available after resuming from background.
 * The loop polls the job the entry had when it started, and only while the entry is still its own: still
 * 'normalizing', still in the upload queue and still on that job. It stops as soon as another path has finished
 * the entry (a cancel, or a recent-jobs merge that cancelled, removed or finalized it) or a retry has given it
 * another job. It checks at the top of each round and again once each answer arrives, before applying it, so an
 * answer already in flight when that happens is dropped instead of re-creating a progress slot or overwriting the
 * retried entry. A loop that stops this way leaves the progress slot alone, since the path that finished or
 * restarted the entry owns it now; only a loop that gives up on failures removes it.
 * With options.isCurrentMonitor, the entry is the loop's own only while it answers true, so a loop whose monitor a
 * normalization retry or a merge revival replaced (the entry is 'normalizing' on the same job again) stops too.
 * An answer for an earlier attempt of the job than the entry follows is skipped, and the loop polls on; one for a later
 * attempt is adopted (see updateEntryFromJobStatus and finalizeTerminalEntry).
 * With options.upgradeWhen, each round first asks it whether the entry may switch to a stream; when it answers true,
 * the loop stops without polling that round and resolves POLL_UPGRADE, leaving the progress slot to the stream.
 * @param {QueueEntry} entry
 * @param {{upgradeWhen?: () => boolean, isCurrentMonitor?: () => boolean}} [options]
 * @returns {Promise<typeof POLL_UPGRADE|undefined>} POLL_UPGRADE when upgradeWhen ended the loop, otherwise undefined
 */
async function pollEntryNormalization(entry, options = {}) {
    const pollInterval = 2000;
    const maxAttempts = 5;
    const baseRetryDelay = 2000;
    const delay = ms => new Promise(r => setTimeout(r, ms));
    const jobId = entry.jobId;
    const isStillPolled = () => entry.status === 'normalizing' && entry.jobId === jobId && uploadQueue.includes(entry) && (options.isCurrentMonitor?.() ?? true);
    let consecutiveErrors = 0;

    while (isStillPolled()) {
        if (options.upgradeWhen?.()) {
            return POLL_UPGRADE;
        }

        let job = null;
        try {
            const response = await fetch('/api/jobs/' + jobId, {
                headers: { 'X-API-Key': getApiKey() }
            });
            if (response.ok) {
                job = await response.json();
            }
        } catch {
            // Network error: retried with backoff below (handles mobile resume, where the network is not
            // immediately available after returning from the background)
        }

        if (!isStillPolled()) {
            return;
        }

        if (!job) {
            consecutiveErrors++;
            if (consecutiveErrors < maxAttempts) {
                await delay(baseRetryDelay * Math.pow(2, consecutiveErrors - 1));
                continue;
            }
            // Stop polling but don't mark as failed - mergeServerJobs on
            // visibility change will resolve the actual status from the server
            progressAnimator.removeSlot(entry.id);

            return;
        }

        consecutiveErrors = 0;
        if (isEarlierAttemptAnswer(entry, job)) {
            await delay(pollInterval);
            continue;
        }
        if (TERMINAL_JOB_STATUSES.has(job.status)) {
            await finalizeTerminalEntry(entry, job);

            return;
        }

        updateEntryFromJobStatus(entry, job);
        updateQueueItemProgress(entry);

        await delay(pollInterval);
    }
}

/**
 * Cancel a single queue entry, removing it from the queue and DOM.
 * Queued: removes immediately. Uploading or saving: aborts XHR, after remembering the upload's clientUploadId as
 * cancelled (recordCancelledUpload), since the server may still create the job of an upload whose body it has received
 * and the page never learns that job's id: recent-jobs merges then withhold that job and cancel it on the server, and
 * show it as completed if it already finished. Normalizing: closes SSE, finalizes the entry (which records the
 * job's dismissal as pending, so a recent-jobs snapshot fetched before the cancel cannot re-add it in this tab, while a
 * later load skips it), POSTs cancel for the attempt the entry follows (requestJobCancel), so a cancel that reaches the
 * server after the job moved to a later attempt leaves that attempt running. When the request succeeds or returns 404 (the job is gone), the
 * dismissal is confirmed, so later loads honor it too. When it throws, times out after CANCEL_REQUEST_TIMEOUT_MS or
 * returns another non-OK status, the job may still be running, may have completed first or may already be cancelled, so the dismissal
 * this cancel recorded is dropped again wherever no other path or tab has replaced it, and the next snapshot shows the
 * job unless the server lists it as Cancelled. A snapshot requested before the cancel and merged after that drop can
 * still briefly re-add a job the server did cancel. When the page closes or reloads before the request settles, the
 * pending dismissal stays in storage unconfirmed, and the next load shows the job unless the server lists it as Cancelled.
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

    if (isInUploadPhase(entry)) {
        if (entry.xhr) {
            recordCancelledUpload(entry.clientUploadId);
            entry.xhr.abort();
        }

        return;
    }

    if (entry.status === 'normalizing') {
        const jobId = entry.jobId;
        const attempt = attemptOf(entry);

        if (entry.eventSource) {
            entry.eventSource.close();
            entry.eventSource = null;
        }

        progressAnimator.reset(entry.id);
        const dismissedAt = finalizeCancelledEntry(entry, { pending: true });

        // Force-resolve the monitoring promise so the promise chain completes
        if (entry._resolveMonitor) {
            entry._resolveMonitor();
            entry._resolveMonitor = null;
        }

        checkAllComplete();

        // A cancel that may not have taken effect drops this cancel's dismissal, so the next snapshot can show the job again;
        // if the server did cancel it after all, later snapshots list it as Cancelled and skip it
        if (jobId) {
            if (await requestJobCancel(jobId, attempt)) {
                confirmDismissedJob(jobId, dismissedAt);
            } else {
                dropDismissedJob(jobId, dismissedAt);
            }
        }
    }
}

/**
 * Retry a failed or cancelled entry. An entry whose job failed in normalization (isServerRetryable) asks the server to
 * retry the job first, whether or not it still holds its file (retryNormalizationOnServer); any other entry that holds
 * its file uploads it again (requeueForUpload). Does nothing for an entry that offers neither (see offersRetry).
 * @param {string} entryId
 * @returns {Promise<void>}
 */
export async function retryEntry(entryId) {
    const entry = uploadQueue.find(e => e.id === entryId);
    if (!entry) {
        return;
    }
    if (isServerRetryable(entry)) {
        await retryNormalizationOnServer(entry);
    } else if (isUploadRetryable(entry)) {
        requeueForUpload(entry);
    }
}

/**
 * Ask the server to retry the normalization of a failed entry's job (POST /api/jobs/{jobId}/retry?attempt=N), which resets
 * the job in place under its next attempt. The request names the attempt the entry shows, so the server refuses it (409)
 * when the job has moved past that view. While the request is in flight the entry is marked QueueEntry.retryPending, so its
 * Retry button is disabled. When the answer arrives after another path moved the entry on (a merge or another tab's
 * retry made it follow the new attempt, a dismissal removed it), the answer is dropped. On 202 the same entry follows
 * the new attempt (followRetriedAttempt). A refusal with reason source-gone (the pending upload is gone) uploads the
 * file again when the entry still holds it (requeueForUpload); every other refusal, and one for an entry without its
 * file, shows the server's error on the entry, which stays failed and retryable on the attempt the server is now on
 * (followJobAfterRefusedRetry).
 * @param {QueueEntry} entry
 * @returns {Promise<void>}
 */
async function retryNormalizationOnServer(entry) {
    if (entry.retryPending) {
        return;
    }
    const jobId = entry.jobId;
    const attempt = attemptOf(entry);
    entry.retryPending = true;
    updateQueueItemInDOM(entry);

    let status = 0;
    let body = null;
    try {
        const response = await fetch('/api/jobs/' + encodeURIComponent(jobId) + '/retry?attempt=' + attempt, {
            method: 'POST',
            headers: { 'X-API-Key': getApiKey() }
        });
        status = response.status;
        body = tryParseJson(await response.text());
    } catch {
        // Network error: shown on the entry below, which stays retryable
    }
    entry.retryPending = false;

    if (!uploadQueue.includes(entry) || entry.jobId !== jobId || entry.status !== 'failed' || attemptOf(entry) !== attempt) {
        if (uploadQueue.includes(entry)) {
            updateQueueItemInDOM(entry);
        }

        return;
    }

    if (status === 202 && body?.jobId === jobId) {
        followRetriedAttempt(entry, body);

        return;
    }
    if (body?.reason === SOURCE_GONE_REASON && isUploadRetryable(entry)) {
        requeueForUpload(entry);

        return;
    }

    const error = body?.error || (status === 0 ? 'Retry failed: the server could not be reached' : 'Retry failed');
    entry.error = error;
    updateQueueItemInDOM(entry);
    saveQueueState();
    await followJobAfterRefusedRetry(entry, jobId, attempt, error);
}

/**
 * After a retry the server did not accept, read the job again and make the entry follow the attempt the server is on: a
 * refused retry can still have moved the job (a failed send leaves it failed on its next attempt, another client's retry
 * started one), and a later retry from this entry must name that attempt or the server refuses it as stale. An active
 * later attempt is followed like an accepted retry (followRetriedAttempt); a failed one is adopted with the refusal's
 * error kept, and stays retryable. Any other answer (the same attempt, unreachable, the entry moved on meanwhile) leaves
 * the entry to the next recent-jobs merge.
 * @param {QueueEntry} entry
 * @param {string} jobId
 * @param {number} attempt - The attempt the refused retry named
 * @param {string} error - The refusal's error, kept on the entry
 * @returns {Promise<void>}
 */
async function followJobAfterRefusedRetry(entry, jobId, attempt, error) {
    let job = null;
    try {
        const response = await fetch('/api/jobs/' + encodeURIComponent(jobId), { headers: { 'X-API-Key': getApiKey() } });
        job = response.ok ? tryParseJson(await response.text()) : null;
    } catch {
        // Unreachable: the next recent-jobs merge reconciles the entry
    }
    const unchanged = uploadQueue.includes(entry) && entry.jobId === jobId && entry.status === 'failed' && attemptOf(entry) === attempt;
    if (!job || job.jobId !== jobId || !unchanged || compareJobAttempt(entry, job) !== ATTEMPT_ORDER.LATER) {
        return;
    }
    if (job.status === 'Failed') {
        adoptJobAttempt(entry, job);
        entry.normalizationFailed = isNormalizationFailure(job);
        entry.error = error;
        updateQueueItemInDOM(entry);
        saveQueueState();
    } else if (!TERMINAL_JOB_STATUSES.has(job.status)) {
        followRetriedAttempt(entry, job);
    }
}

/**
 * Make a failed entry follow the attempt a normalization retry it requested started: the entry adopts the attempt from
 * the server's answer (adoptJobAttempt, with this device's clock as the attempt's start), stamps it as a live update,
 * becomes 'normalizing' and is monitored again under the stream budget (monitorEntryNormalizationInBackground, whose
 * updates create the progress slot), and the queue's title and toggles are refreshed for the active work.
 * @param {QueueEntry} entry
 * @param {Object} job - The retried job's status from the 202 answer
 */
function followRetriedAttempt(entry, job) {
    adoptJobAttempt(entry, job, { startedAt: Date.now() });
    markLiveUpdate(entry);
    entry.status = 'normalizing';
    entry.title = job.title || entry.title;
    entry.episode = null;
    updateQueueItemInDOM(entry);
    refreshQueueActivity(uploadQueue.some(e => isActiveWork(e)));
    saveQueueState();
    syncPushSession([entry.jobId], uploadQueue);
    monitorEntryNormalizationInBackground(entry);
}

/**
 * Reset a failed or cancelled entry that holds its file to queued, so it is uploaded again as a new job, and move it to
 * the end of the queue.
 * @param {QueueEntry} entry
 */
function requeueForUpload(entry) {
    entry.status = 'queued';
    entry.progress = 0;
    entry.stage = null;
    entry.jobId = null;
    entry.attempt = null;
    entry.kind = null;
    entry.normalizationFailed = false;
    entry.terminalClaim = null;
    entry.jobIdReceivedAt = null;
    entry.lastLiveUpdateAt = null;
    entry.clientUploadId = null;
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
    refreshQueueActivity(uploadQueue.some(e => isActiveWork(e)));

    saveQueueState();

    if (!isUploading) {
        processQueue();
    }
}

/**
 * Build a queue entry for a server job the page did not start here: one a recent-jobs merge discovered (CLI uploads,
 * other tabs and devices) or a transcript retry job this page requested. The entry follows the job's attempt and kind,
 * records whether a failed job failed in normalization, holds no file, and is stamped with jobIdReceivedAt, so an older
 * snapshot merged later cannot remove it as stale. Its startedAt is the attempt's queuedAt. Not yet pushed onto the queue.
 * @param {Object} serverJob - Job status (recent-jobs snapshot job or a 202 answer)
 * @returns {QueueEntry}
 */
export function createServerJobEntry(serverJob) {
    const serverStatus = serverJob.status;
    const isServerTerminal = TERMINAL_JOB_STATUSES.has(serverStatus);

    return {
        id: generateEntryId(),
        file: null,
        status: isServerTerminal ? (serverStatus === 'Completed' ? 'completed' : 'failed') : 'normalizing',
        progress: serverJob.progressPercent ?? (isServerTerminal ? 100 : 0),
        stage: serverJob.stage || 'Queued',
        jobId: serverJob.jobId,
        attempt: attemptOf(serverJob),
        kind: serverJob.kind ?? null,
        normalizationFailed: isNormalizationFailure(serverJob),
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
        startedAt: attemptStartedAt(serverJob),
        normalizationComplete: serverJob.normalizationComplete || false,
        transcriptionStatus: serverJob.transcriptionStatus || null,
        transcriptionError: serverJob.transcriptionError || null,
        _resolveMonitor: null
    };
}

/**
 * Whether the queue's transcript retry entries settle an episode's "Retry transcript" (one still active, or one that
 * published the transcript; see isTranscriptRetrySettled in job-attempts.js), so entries offering it hide the action.
 * @param {string} episodeId
 * @returns {boolean}
 */
export function hasSettlingTranscriptRetry(episodeId) {
    return isTranscriptRetrySettled(uploadQueue, episodeId, isActiveWork);
}

/**
 * Retry the transcription of an episode whose transcript failed (POST /api/feeds/{feedId}/episodes/{id}/transcript/retry).
 * On 202 the server's new transcript retry job gets a new queue entry (unless a recent-jobs merge already added it),
 * monitored like any job and shown as an indeterminate "Transcribing" (its normalization track is already complete).
 * The queue view is shown when it is not current, and the queue's title and toggles are refreshed for the active work.
 * A refusal (another retry of the episode running, missing audio, Speech not configured, and so on) or a network error
 * is shown as a toast with the server's error. Used by the history context menu and by a queue entry completed with a
 * failed transcript (retryEntryTranscript).
 * @param {string} episodeId
 * @returns {Promise<boolean>} Whether the server started a retry
 */
export async function retryTranscript(episodeId) {
    let status = 0;
    let body = null;
    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/' + encodeURIComponent(episodeId) + '/transcript/retry', {
            method: 'POST',
            headers: { 'X-API-Key': getApiKey() }
        });
        status = response.status;
        body = tryParseJson(await response.text());
    } catch {
        // Network error: reported below
    }

    if (status !== 202 || !body?.jobId) {
        const reason = body?.error || (status === 0 ? 'the server could not be reached' : 'the server refused it');
        showToast(body?.error ? 'Transcript retry refused: ' + reason : 'Transcript retry failed: ' + reason, 6000, 'transcript-retry-error');

        return false;
    }

    if (!uploadQueue.some(e => e.jobId === body.jobId)) {
        addServerStartedEntry(createServerJobEntry(body));
    }

    return true;
}

/**
 * Add an entry for a job the server started at this page's request (a transcript retry) to the queue: shows the queue
 * view (rendering it whole) or adds the item to the current one, refreshes the queue's title and toggles for its active
 * work, saves the queue, syncs the push session and starts monitoring it.
 * @param {QueueEntry} entry
 */
function addServerStartedEntry(entry) {
    uploadQueue.push(entry);
    const hasActive = uploadQueue.some(e => isActiveWork(e));
    if (getCurrentState() === 'queue') {
        renderQueueList(true);
        refreshQueueActivity(hasActive);
    } else {
        showState('queue', hasActive, collapseHistoryImmediate);
        renderQueueList(false);
        void initHistorySection();
    }
    saveQueueState();
    if (isActiveWork(entry)) {
        syncPushSession([entry.jobId], uploadQueue);
        monitorEntryNormalizationInBackground(entry);
    }
}

/**
 * "Retry transcript" on a queue entry completed with a failed transcript: retries its episode's transcription
 * (retryTranscript), which adds a new entry for the retry job, while the entry itself stays as it is. While the request
 * is in flight the entry is marked QueueEntry.retryPending, so its button is disabled.
 * @param {string} entryId
 * @returns {Promise<void>}
 */
export async function retryEntryTranscript(entryId) {
    const entry = uploadQueue.find(e => e.id === entryId);
    const episodeId = entry ? transcriptRetryEpisodeId(entry) : null;
    if (!episodeId || entry.retryPending) {
        return;
    }

    entry.retryPending = true;
    updateQueueItemInDOM(entry);
    try {
        await retryTranscript(episodeId);
    } finally {
        entry.retryPending = false;
        updateQueueItemInDOM(entry);
    }
}

/**
 * Remove completed and cancelled entries older than 1 hour from the upload queue.
 * Called when the tab regains focus or the PWA resumes, so stale terminal entries
 * don't linger until a full page refresh. Recent entries are kept so the user can
 * see completions that happened while the tab was inactive.
 * Records the cleared entries' job attempts as dismissed so mergeServerJobs does not re-add them. An entry's startedAt is
 * the start of the attempt it follows, so a job retried after its first hour is not cleared as old.
 */
export function clearTerminalEntries() {
    const oneHourAgo = Date.now() - JOB_TTL_MS;
    const terminalEntries = uploadQueue.filter(e =>
        (e.status === 'completed' || e.status === 'cancelled') && e.startedAt < oneHourAgo
    );
    if (terminalEntries.length === 0) {
        return;
    }
    for (const entry of terminalEntries) {
        if (entry.eventSource) {
            entry.eventSource.close();
            entry.eventSource = null;
        }
        if (entry._resolveMonitor) {
            entry._resolveMonitor();
            entry._resolveMonitor = null;
        }
        removeQueueItemFromDOM(entry.id);
        progressAnimator.removeSlot(entry.id);

        const idx = uploadQueue.indexOf(entry);
        if (idx !== -1) {
            uploadQueue.splice(idx, 1);
        }
    }
    recordDismissedJobs(terminalEntries.filter(e => e.jobId).map(e => ({ jobId: e.jobId, attempt: attemptOf(e) })));
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
            attempt: e.attempt ?? null,
            kind: e.kind ?? null,
            normalizationFailed: e.normalizationFailed || false,
            clientUploadId: e.clientUploadId,
            episodeId: e.episodeId,
            episode: e.episode,
            error: e.error,
            localSourceIndex: e.localSourceIndex,
            validationError: e.validationError,
            startedAt: e.startedAt,
            normalizationComplete: e.normalizationComplete || false,
            transcriptionStatus: e.transcriptionStatus || null,
            transcriptionError: e.transcriptionError || null
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
 * Restores only the entries selectRestorableEntries (queue-restore.js) keeps: started within JOB_TTL_MS (entries without
 * startedAt, from before the field existed, count as expired; a retried entry's startedAt is its attempt's start) and not
 * following a job attempt this browser has dismissed (a dismissal of an earlier attempt leaves a retried entry). The
 * dismissals must be loaded first (startSession does), since the saved queue may come from another tab that still showed
 * an entry this tab dismissed.
 * An entry still uploading or saving is restored as failed with UPLOAD_INTERRUPTED_ERROR (the upload cannot resume);
 * when the server created a job for it anyway, the recent-jobs merge replaces it with that job (mergeServerJobs).
 * Starts normalization monitors (a progress stream, or polling while every stream slot is taken; see
 * monitorEntryNormalization) for normalizing entries so progress updates arrive during the sync wait.
 * Caller is responsible for showing the appropriate state after server sync completes.
 * No-op while the queue already holds entries: startup re-runs on hashchange and key entry, and the live
 * in-memory queue is authoritative. The saved copy can lag it (upload and live progress updates do not trigger
 * a save) or come from another tab (the key is per feed and shared across tabs), and the in-flight uploads,
 * normalization monitors and animator slots reference the live entries, so restoring over them would orphan those.
 * @returns {Promise<boolean>} True if entries were restored into uploadQueue
 */
export async function restoreQueueState() {
    if (uploadQueue.length > 0) {
        return false;
    }

    const saved = localStorage.getItem(QUEUE_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const entries = tryParseJson(saved);
    if (!entries || !Array.isArray(entries) || entries.length === 0) {
        clearQueueState();

        return false;
    }

    // Only entries started within the TTL whose job this browser has not dismissed (another tab may have saved the queue last)
    const recentEntries = selectRestorableEntries(entries, { now: Date.now(), ttlMs: JOB_TTL_MS, isDismissedJob: isJobDismissed });
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
        if (isInUploadPhase(entry)) {
            entry.status = 'failed';
            entry.error = UPLOAD_INTERRUPTED_ERROR;
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
    // Start normalization monitors now so progress updates arrive during the sync wait.
    // isResuming=true so the animator snaps to the live server value instead of
    // computing an inflated velocity from a fake stage start at 0.
    for (const entry of uploadQueue.filter(e => e.status === 'normalizing' && e.jobId)) {
        monitorEntryNormalizationInBackground(entry, { isResuming: true });
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
        retryEntryTranscript,
        dismissEntry,
        getQueue,
        hasSettlingTranscriptRetry,
    });
}
