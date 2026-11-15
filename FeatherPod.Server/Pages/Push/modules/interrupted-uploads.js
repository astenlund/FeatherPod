/**
 * Decides which server jobs a recent-jobs merge adds as new entries, and which queue entries whose upload a page
 * reload interrupted those jobs replace (pure, no DOM).
 *
 * Reloading during 'uploading' or 'saving' restores the entry as failed with the interrupted-upload error and no jobId,
 * since the job id arrives only with the upload's 202 response. The server may still have created the job, and a
 * recent-jobs merge then discovers it as a new entry; pairInterruptedUploads decides which interrupted entry each such
 * job replaces, so the queue does not show the same upload twice. Only jobs isAddableServerJob admits reach it, so a
 * cancelled, dismissed or already-queued job replaces nothing.
 * Kept separate so it can be unit-tested under Node (tests/push/interrupted-uploads.test.js, run via `npm test`).
 */

/**
 * Whether the merge may add a server job as a new entry: it is not cancelled, and not among the excluded job ids
 * (jobs already in the queue, and jobs dismissed, cleared or cancelled on this page or another tab).
 * @param {Object} job - Server job from a recent-jobs snapshot
 * @param {{has: (jobId: string) => boolean}} excludedJobIds - Set or Map keyed by jobId
 * @returns {boolean}
 */
export function isAddableServerJob(job, excludedJobIds) {
    return job.status !== 'Cancelled' && !excludedJobIds.has(job.jobId);
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
 * Parse a job's queuedAt into epoch milliseconds, or null when it is absent or unparseable.
 * @param {Object} job
 * @returns {number|null}
 */
function queuedAtMs(job) {
    if (!job.queuedAt) {
        return null;
    }
    const ms = new Date(job.queuedAt).getTime();

    return Number.isNaN(ms) ? null : ms;
}

/**
 * Pair each server job with at most one interrupted entry it replaces, and each entry with at most one job.
 * An entry matches a job with the same fileName that was queued no earlier than the entry was started, so an older job
 * for the same file cannot claim it; a job without a usable queuedAt matches nothing. Jobs are taken oldest first, and
 * each takes the earliest started unpaired entry it can match, as the queue uploads its entries in order.
 * @param {Array<Object>} entries - Queue entries (any status; only interrupted uploads are considered)
 * @param {Array<Object>} jobs - Server jobs the merge is about to add as new entries (see isAddableServerJob)
 * @param {string} interruptedError - The error restoreQueueState gives an interrupted upload
 * @returns {Map<string, Object>} Replaced entry by jobId
 */
export function pairInterruptedUploads(entries, jobs, interruptedError) {
    const candidates = entries.filter(e => isInterruptedUpload(e, interruptedError));
    const pairs = new Map();
    if (candidates.length === 0) {
        return pairs;
    }

    const datedJobs = jobs
        .map(job => ({ job, queuedAt: queuedAtMs(job) }))
        .filter(j => j.queuedAt !== null)
        .sort((a, b) => a.queuedAt - b.queuedAt);
    const paired = new Set();
    for (const { job, queuedAt } of datedJobs) {
        let best = null;
        for (const entry of candidates) {
            const eligible = !paired.has(entry) && entry.fileName === job.fileName && typeof entry.startedAt === 'number' && entry.startedAt <= queuedAt;
            if (eligible && (best === null || entry.startedAt < best.startedAt)) {
                best = entry;
            }
        }
        if (best !== null) {
            paired.add(best);
            pairs.set(job.jobId, best);
        }
    }

    return pairs;
}
