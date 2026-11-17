/**
 * Uploads cancelled before their response arrived, and what a recent-jobs merge does with the jobs the server created
 * for them anyway (pure, no DOM).
 *
 * Every upload sends a fresh client upload id (generateClientUploadId), which the server stores on the job it creates
 * and returns in job listings. Cancelling an upload during 'uploading' or 'saving' aborts its request, so the page never
 * receives the job id; the server may still create the job, and without a dismissal a later recent-jobs merge would
 * add it to the queue, where it runs to a published episode. The store remembers the ids of such uploads for the TTL,
 * across reloads and the browser's other tabs on the feed (each merge refreshes it from storage), and
 * classifyCancelledUploadJobs tells the merge which jobs carrying one of them to withhold and cancel on the server, and
 * which ids are settled.
 * Kept separate so it can be unit-tested under Node (tests/push/cancelled-uploads.test.js, run via `npm test`).
 */

import { createSavedEntryList } from './dismissal-store.js';
import { attemptOf } from './job-attempts.js';

/**
 * @typedef {Object} CancelledUploadStore
 * @property {() => void} load - Load the saved ids into this tab, dropping expired ones from storage
 * @property {() => void} refresh - Add to this tab the ids saved since it loaded, by other tabs too, without writing to
 *     storage; a merge calls it first, so an upload another open tab cancelled is withheld here as well
 * @property {(clientUploadId: string) => void} record - Remember an upload as cancelled, in this tab and in storage
 * @property {(clientUploadId: string) => boolean} has - Whether this tab treats the upload as cancelled (as of its last
 *     load, refresh or record)
 * @property {(clientUploadId: string) => void} forget - Stop treating the upload as cancelled, in this tab and in storage
 */

/**
 * Create a store of cancelled upload ids over a storage-like object, saved through createSavedEntryList, so ids another
 * tab saved survive this tab's writes. An id another tab forgot can stay in this tab until it reloads; its job then shows
 * as Cancelled (settled) or is cancelled and dismissed again, which is harmless.
 * @param {Object} options
 * @param {() => Storage} options.getStorage - Returns the storage holding the saved list (getItem/setItem); called on
 *     every access, so a storage that throws when reached degrades like one that throws on use
 * @param {string} options.key - Storage key of the saved list
 * @param {number} options.ttlMs - How long an id is remembered; older saved ids are ignored and dropped on the next write
 * @param {() => number} [options.now] - Clock in epoch milliseconds
 * @returns {CancelledUploadStore}
 */
export function createCancelledUploadStore({ getStorage, key, ttlMs, now = Date.now }) {
    /** @type {Set<string>} */
    const cancelled = new Set();
    const savedList = createSavedEntryList({
        getStorage,
        key,
        ttlMs,
        now,
        idField: 'clientUploadId',
        timeField: 'cancelledAt',
        toEntry: entry => ({ clientUploadId: entry.clientUploadId, cancelledAt: entry.cancelledAt })
    });

    return {
        load() {
            savedList.update(saved => {
                for (const clientUploadId of saved.keys()) {
                    cancelled.add(clientUploadId);
                }
            });
        },

        refresh() {
            try {
                for (const clientUploadId of savedList.read().keys()) {
                    cancelled.add(clientUploadId);
                }
            } catch {
                // Storage unavailable: this tab keeps the ids it already holds
            }
        },

        record(clientUploadId) {
            cancelled.add(clientUploadId);
            savedList.update(saved => saved.set(clientUploadId, { clientUploadId, cancelledAt: now() }));
        },

        has: clientUploadId => cancelled.has(clientUploadId),

        forget(clientUploadId) {
            cancelled.delete(clientUploadId);
            savedList.update(saved => saved.delete(clientUploadId));
        }
    };
}

/**
 * Sort the jobs of a recent-jobs snapshot that carry the client upload id of a cancelled upload:
 * - withheld: jobs still running, queued or failed. The merge does not add them, cancels them on the server and, once
 *   the cancel settles, records them as dismissed and forgets the id.
 * - settled: ids whose job the server has already cancelled, which the merge skips anyway, or completed, which the merge
 *   adds as a completed entry, since the episode is already published and the page never deletes it, or moved past its
 *   upload's attempt by a normalization retry the user started since, which now owns the job and which a cancellation
 *   decided on the upload must not reach. The merge forgets these ids.
 * Every withheld job is therefore on its upload's own attempt, the first, which its server cancel names.
 * Jobs without a client upload id, or with one that was not cancelled, are in neither list.
 * @param {Array<Object>} jobs - Server jobs from a recent-jobs snapshot
 * @param {(clientUploadId: string) => boolean} isCancelledUpload
 * @returns {{withheld: Array<Object>, settled: Array<string>}}
 */
export function classifyCancelledUploadJobs(jobs, isCancelledUpload) {
    const withheld = [];
    const settled = [];
    for (const job of jobs) {
        if (!job.clientUploadId || !isCancelledUpload(job.clientUploadId)) {
            continue;
        }
        if (job.status === 'Cancelled' || job.status === 'Completed' || attemptOf(job) > 1) {
            settled.push(job.clientUploadId);
        } else {
            withheld.push(job);
        }
    }

    return { withheld, settled };
}

/**
 * Generate a client upload id: 32 random hex characters. Uses getRandomValues rather than randomUUID, which is missing
 * outside secure contexts (the push page also runs over plain HTTP on a local network).
 * @param {Crypto} [cryptoSource]
 * @returns {string}
 */
export function generateClientUploadId(cryptoSource = globalThis.crypto) {
    const bytes = cryptoSource.getRandomValues(new Uint8Array(16));

    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}
