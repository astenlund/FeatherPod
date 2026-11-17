/**
 * Job attempt identity and the retry decisions that rest on it (pure, no DOM).
 *
 * A normalization retry resets a failed job in place: the job keeps its id and moves to its next attempt (a new
 * queuedAt, no completedAt). Every job status the server sends (an upload's 202 body, GET /api/jobs/{id}, progress
 * stream events, recent-jobs listings) carries that attempt, so the page can tell a status answer about the attempt an
 * entry follows from one about an earlier attempt that was read before the reset. An answer for an earlier attempt never
 * changes the entry; one for a later attempt (another tab retried the job) is adopted. A job status or saved entry
 * without an attempt (from before attempts existed) means the first attempt.
 * The module also decides which retry a failed or completed entry offers: a normalization retry on the server, a re-upload
 * of the file the entry still holds, or a transcript retry of the episode.
 * Kept separate so it can be unit-tested under Node (tests/push/job-attempts.test.js, run via `npm test`).
 */

/** The job kind of a transcript retry job (JobKinds.TranscriptRetry on the server); an upload job has no kind */
export const TRANSCRIPT_RETRY_KIND = 'TranscriptRetry';

/** The reason a normalization retry refusal carries when the pending upload is gone and only a re-upload can help */
export const SOURCE_GONE_REASON = 'source-gone';

/**
 * How an attempt stands against another.
 * @readonly
 * @enum {string}
 */
export const ATTEMPT_ORDER = Object.freeze({
    EARLIER: 'earlier',
    SAME: 'same',
    LATER: 'later'
});

/**
 * The attempt a job status or a queue entry carries: a positive integer, or 1 when it carries none (or an unusable value).
 * @param {{attempt?: *}|null|undefined} holder - A job status or a queue entry
 * @returns {number}
 */
export function attemptOf(holder) {
    const attempt = holder?.attempt;

    return Number.isInteger(attempt) && attempt >= 1 ? attempt : 1;
}

/**
 * How a job status answer's attempt stands against the attempt an entry follows.
 * @param {{attempt?: *}} entry - Queue entry
 * @param {{attempt?: *}} job - Job status answer for the entry's job
 * @returns {ATTEMPT_ORDER[keyof ATTEMPT_ORDER]}
 */
export function compareJobAttempt(entry, job) {
    const entryAttempt = attemptOf(entry);
    const jobAttempt = attemptOf(job);
    if (jobAttempt < entryAttempt) {
        return ATTEMPT_ORDER.EARLIER;
    }

    return jobAttempt > entryAttempt ? ATTEMPT_ORDER.LATER : ATTEMPT_ORDER.SAME;
}

/**
 * Whether a job status answer was read before the attempt the entry follows began, so it must not change the entry.
 * An answer without a job (null) is not tied to any attempt and is never stale here.
 * @param {{attempt?: *}} entry
 * @param {{attempt?: *}|null|undefined} job
 * @returns {boolean}
 */
export function isEarlierAttemptAnswer(entry, job) {
    return job != null && compareJobAttempt(entry, job) === ATTEMPT_ORDER.EARLIER;
}

/**
 * Whether a terminal job status means the job failed in normalization, the only failure a normalization retry can fix
 * (the server requires a Failed job with a normalization error that is not a transcript retry job).
 * @param {{status?: string, normalizationError?: string|null, kind?: string|null}|null|undefined} job
 * @returns {boolean}
 */
export function isNormalizationFailure(job) {
    return job?.status === 'Failed' && typeof job.normalizationError === 'string' && job.normalizationError !== '' && job.kind !== TRANSCRIPT_RETRY_KIND;
}

/**
 * Whether Retry on a failed entry asks the server to retry the job's normalization first: the entry has a job, and that
 * job failed in normalization (QueueEntry.normalizationFailed). The entry need not hold its file.
 * @param {{status: string, jobId?: string|null, kind?: string|null, normalizationFailed?: boolean}} entry
 * @returns {boolean}
 */
export function isServerRetryable(entry) {
    return entry.status === 'failed' && !!entry.jobId && entry.kind !== TRANSCRIPT_RETRY_KIND && entry.normalizationFailed === true;
}

/**
 * Whether Retry on a failed or cancelled entry can upload its file again: the entry still holds the file, and it did not
 * fail validation.
 * @param {{status: string, file?: *, validationError?: boolean}} entry
 * @returns {boolean}
 */
export function isUploadRetryable(entry) {
    return (entry.status === 'failed' || entry.status === 'cancelled') && !!entry.file && !entry.validationError;
}

/**
 * Whether a failed entry offers Retry at all (on the server, or by uploading its file again).
 * @param {Object} entry
 * @returns {boolean}
 */
export function offersRetry(entry) {
    return entry.status === 'failed' && (isServerRetryable(entry) || isUploadRetryable(entry));
}

/**
 * Whether the queue's transcript retry entries settle an episode's "Retry transcript", so entries offering it hide the
 * action: a retry of the episode is still active (the server runs at most one per episode), or one completed with its
 * transcript published (the episode's transcript is no longer failed). A retry that failed again settles nothing.
 * @param {Array<{kind?: string|null, episodeId?: string|null, status?: string, transcriptionStatus?: string|null}>} entries
 * @param {string} episodeId
 * @param {(entry: object) => boolean} isActive - Whether an entry is still active work
 * @returns {boolean}
 */
export function isTranscriptRetrySettled(entries, episodeId, isActive) {
    return entries.some(e => e.kind === TRANSCRIPT_RETRY_KIND && e.episodeId === episodeId
        && (isActive(e) || (e.status === 'completed' && e.transcriptionStatus === 'Completed')));
}

/**
 * The episode a completed entry with a failed transcript offers "Retry transcript" for, or null when it offers none.
 * The episode the entry's job is bound to (`episodeId`) wins over the resolved `episode`, which an entry restored from
 * an older save may hold for another episode sharing the file name.
 * @param {{status: string, transcriptionStatus?: string|null, episodeId?: string|null, episode?: {id?: string}|null}} entry
 * @returns {string|null}
 */
export function transcriptRetryEpisodeId(entry) {
    if (entry.status !== 'completed' || entry.transcriptionStatus !== 'Failed') {
        return null;
    }

    return entry.episodeId || entry.episode?.id || null;
}

/**
 * Whether the history context menu offers "Retry transcript" for an episode: its transcript failed.
 * @param {{transcriptStatus?: string|null}|null|undefined} episode
 * @returns {boolean}
 */
export function offersTranscriptRetry(episode) {
    return episode?.transcriptStatus === 'Failed';
}
