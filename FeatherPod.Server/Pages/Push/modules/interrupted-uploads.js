/**
 * Decides which server jobs a recent-jobs merge adds as new entries, and which queue entries whose upload a page
 * reload interrupted those jobs replace (pure, no DOM).
 *
 * Both decisions recognize an upload's job by the clientUploadId the page sent with the upload, which the server stores
 * on the job and returns in recent-jobs listings; file names and timestamps never decide, so a job from another source
 * for a file of the same name (the CLI sends no clientUploadId) and a device clock out of step with the server's change
 * nothing.
 * Reloading during 'uploading' or 'saving' restores the entry as failed with the interrupted-upload error and no jobId,
 * since the job id arrives only with the upload's 202 response. The server may still have created the job, and a
 * recent-jobs merge then discovers it as a new entry; pairInterruptedUploads decides which interrupted entry each such
 * job replaces, so the queue does not show the same upload twice. Only jobs isAddableServerJob admits reach it, so a
 * cancelled, dismissed, already-queued or still-uploading job replaces nothing.
 * Kept separate so it can be unit-tested under Node (tests/push/interrupted-uploads.test.js, run via `npm test`).
 */

/**
 * Whether the merge may add a server job as a new entry: it is not cancelled, not among the excluded job ids (jobs
 * already in the queue, and jobs dismissed, cleared or cancelled on this page or another tab), and not the job of an
 * upload this page still has in flight (the job can appear in a snapshot before the upload's 202 response gives the
 * entry its jobId).
 * @param {Object} job - Server job from a recent-jobs snapshot
 * @param {{has: (jobId: string) => boolean}} excludedJobIds - Set or Map keyed by jobId
 * @param {{has: (clientUploadId: string) => boolean}} [inFlightUploadIds] - clientUploadIds of the entries still uploading or saving
 * @returns {boolean}
 */
export function isAddableServerJob(job, excludedJobIds, inFlightUploadIds = new Set()) {
    return job.status !== 'Cancelled' && !excludedJobIds.has(job.jobId) && !(job.clientUploadId && inFlightUploadIds.has(job.clientUploadId));
}

/**
 * The server jobs from a snapshot that the merge may add as new entries (see isAddableServerJob), taking the in-flight
 * uploads from the queue: the entries in their upload phase, by the clientUploadId each upload sent. An in-flight entry's
 * file name withholds nothing, so a job for a file of the same name from another source is kept.
 * @param {Array<Object>} serverJobs - Server jobs from a recent-jobs snapshot
 * @param {Array<Object>} queueEntries - Current queue entries
 * @param {{has: (jobId: string) => boolean}} excludedJobIds - Set or Map keyed by jobId
 * @param {(entry: Object) => boolean} isInUploadPhase - Whether an entry is still uploading or saving
 * @returns {Array<Object>}
 */
export function selectAddableServerJobs(serverJobs, queueEntries, excludedJobIds, isInUploadPhase) {
    const inFlightUploadIds = new Set(queueEntries.filter(e => isInUploadPhase(e) && e.clientUploadId).map(e => e.clientUploadId));

    return serverJobs.filter(job => isAddableServerJob(job, excludedJobIds, inFlightUploadIds));
}

/**
 * Whether an entry is a reload-interrupted upload that a server job may replace.
 * @param {Object} entry - Queue entry
 * @param {string} interruptedError - The error restoreQueueState gives an interrupted upload
 * @returns {boolean}
 */
function isInterruptedUpload(entry, interruptedError) {
    return entry.status === 'failed' && !entry.jobId && entry.error === interruptedError;
}

/**
 * Pair each server job with the interrupted entry whose upload created it: the entry carrying the job's clientUploadId.
 * A job without a clientUploadId, or with one no interrupted entry carries, replaces nothing. Each upload sends a fresh
 * clientUploadId, so at most one entry and one job share it.
 * @param {Array<Object>} entries - Queue entries (any status; only interrupted uploads are considered)
 * @param {Array<Object>} jobs - Server jobs the merge is about to add as new entries (see isAddableServerJob)
 * @param {string} interruptedError - The error restoreQueueState gives an interrupted upload
 * @returns {Map<string, Object>} Replaced entry by jobId
 */
export function pairInterruptedUploads(entries, jobs, interruptedError) {
    const byUploadId = new Map();
    for (const entry of entries) {
        if (entry.clientUploadId && isInterruptedUpload(entry, interruptedError)) {
            byUploadId.set(entry.clientUploadId, entry);
        }
    }
    const pairs = new Map();
    for (const job of jobs) {
        const entry = job.clientUploadId ? byUploadId.get(job.clientUploadId) : undefined;
        if (entry) {
            byUploadId.delete(job.clientUploadId);
            pairs.set(job.jobId, entry);
        }
    }

    return pairs;
}
