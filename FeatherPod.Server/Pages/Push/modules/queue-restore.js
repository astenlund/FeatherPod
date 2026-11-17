/**
 * Which entries of the saved queue a page load restores (pure, no DOM).
 *
 * Every tab of the browser open on a feed saves its whole queue under the feed's one storage key, so the last tab to save
 * wins, and the saved queue can hold an entry this browser has since dismissed in another tab. restoreQueueState in
 * queue.js therefore restores only entries started within the TTL whose job is not dismissed; the dismissals are loaded
 * before it runs (startSession in push.js). A dismissal hides only the job attempt it was decided on and earlier ones
 * (see dismissal-store.js), so an entry that follows a later attempt of a dismissed job (the job was retried) is
 * restored. Everything else about the saved queue still follows the last save.
 * Kept separate so it can be unit-tested under Node (tests/push/queue-restore.test.js, run via `npm test`).
 */

import { attemptOf } from './job-attempts.js';

/**
 * Select the saved entries to restore: started no earlier than ttlMs before now (an entry without startedAt, saved
 * before the field existed, counts as expired; a retried entry's startedAt is its current attempt's start), and not
 * following a dismissed job attempt (the entry's attempt, 1 for an entry saved without one).
 * @param {Array<Object>} entries - Parsed saved queue entries
 * @param {Object} options
 * @param {number} options.now - Current time in epoch milliseconds
 * @param {number} options.ttlMs - How long a saved entry stays restorable
 * @param {(jobId: string, attempt: number) => boolean} options.isDismissedJob - Whether this browser has dismissed the
 *     job's attempt
 * @returns {Array<Object>} The entries to restore, in saved order
 */
export function selectRestorableEntries(entries, { now, ttlMs, isDismissedJob }) {
    const oldestKept = now - ttlMs;

    return entries.filter(e => e?.startedAt && e.startedAt >= oldestKept && !(e.jobId && isDismissedJob(e.jobId, attemptOf(e))));
}
