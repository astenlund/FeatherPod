/**
 * The page's record of dismissed jobs: jobs whose entries were dismissed, cleared or cancelled, which recent-jobs merges
 * must not add back (pure, no DOM).
 *
 * The store keeps an in-memory map for this tab and a saved list in storage that every tab of the browser on the same
 * feed shares, so a dismissal survives a reload for the TTL. The saved list is changed job by job rather than replaced
 * with this tab's map, so dismissals other tabs saved after this tab loaded survive this tab's writes.
 *
 * A dismissal recorded for a cancel whose request has not settled yet is saved as pending: the tab that recorded it
 * honors it, but a later load skips it while keeping it in storage, so the page closing or reloading before the request
 * settles cannot hide a job that may still be running. A cancel that did take effect shows the job as Cancelled in the
 * next recent-jobs snapshot, which the merge skips anyway. Confirming the dismissal once the request succeeds saves it
 * as an ordinary one. Saved entries without the pending field (all of them before pending dismissals existed) are
 * ordinary dismissals.
 * The saved-list mechanics (TTL, entry-by-entry writes, storage failures) live in createSavedEntryList, which the
 * cancelled-upload store (cancelled-uploads.js) shares.
 * Kept separate so it can be unit-tested under Node (tests/push/dismissal-store.test.js, run via `npm test`).
 */

/**
 * @typedef {Object} DismissalStore
 * @property {(jobId: string) => boolean} has - Whether this tab treats the job as dismissed
 * @property {() => void} load - Load the saved ordinary dismissals into this tab's map, skipping pending ones (which
 *     stay in storage) and dropping expired ones from storage
 * @property {(jobIds: string[], options?: {pending?: boolean}) => number} record - Record jobs as dismissed in this
 *     tab's map and in storage, as pending dismissals when options.pending is true; returns the time recorded as their
 *     dismissal, which confirm and drop take to identify it
 * @property {(jobId: string, dismissedAt: number) => void} confirm - Save a pending dismissal as an ordinary one, only
 *     while the saved entry still holds dismissedAt, so a dismissal another path or tab recorded since is left as it is
 * @property {(jobId: string, dismissedAt: number) => void} drop - Drop a job's dismissal from this tab's map and from
 *     storage, from each only while it still holds dismissedAt, so a dismissal another path or tab recorded since is kept
 */

/**
 * @typedef {Object} SavedDismissal
 * @property {string} jobId
 * @property {number} dismissedAt - Epoch milliseconds
 * @property {true} [pending] - Present only for a dismissal recorded for a cancel whose request had not settled when it
 *     was saved
 */

/**
 * Parse JSON, returning null when the text is not valid JSON.
 * @param {string|null} text
 * @returns {*}
 */
function parseJson(text) {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

/**
 * @typedef {Object} SavedEntryList
 * @property {() => Map<string, *>} read - The saved entries by id, without expired or malformed ones; throws when the
 *     storage does
 * @property {(change: (saved: Map<string, *>) => void) => void} update - Apply a change to the saved entries by id and
 *     write them back, dropping expired ones on the way; a storage that throws or is full keeps its previous contents
 */

/**
 * A list saved in storage of entries keyed by a string id, each stamped with an epoch-ms time: entries older than the
 * TTL are ignored and dropped on the next write, and the list is changed entry by entry rather than replaced, so entries
 * other tabs saved survive this tab's writes. A saved entry counts only when its id is a non-empty string and its time
 * a number. Shared by the dismissal store and the cancelled-upload store (cancelled-uploads.js).
 * @param {Object} options
 * @param {() => Storage} options.getStorage - Returns the storage holding the list (getItem/setItem); called on every
 *     access, so a storage that throws when reached degrades like one that throws on use
 * @param {string} options.key - Storage key of the list
 * @param {number} options.ttlMs - How long an entry is kept
 * @param {() => number} options.now - Clock in epoch milliseconds
 * @param {string} options.idField - Field of a saved entry holding its id
 * @param {string} options.timeField - Field of a saved entry holding its time
 * @param {(entry: Object) => *} options.toEntry - Normalizes a valid saved entry into the value the map holds, which is
 *     also what update writes back
 * @returns {SavedEntryList}
 */
export function createSavedEntryList({ getStorage, key, ttlMs, now, idField, timeField, toEntry }) {
    function read() {
        const saved = new Map();
        const entries = parseJson(getStorage().getItem(key));
        if (!Array.isArray(entries)) {
            return saved;
        }
        const oldestKept = now() - ttlMs;
        for (const entry of entries) {
            const id = entry?.[idField];
            const time = entry?.[timeField];
            if (typeof id === 'string' && id !== '' && typeof time === 'number' && time >= oldestKept) {
                saved.set(id, toEntry(entry));
            }
        }

        return saved;
    }

    function update(change) {
        try {
            const saved = read();
            change(saved);
            getStorage().setItem(key, JSON.stringify(Array.from(saved.values())));
        } catch {
            // Storage unavailable or full: the saved list keeps its previous contents
        }
    }

    return { read, update };
}

/**
 * Build a saved dismissal entry, carrying the pending field only for a pending dismissal.
 * @param {string} jobId
 * @param {number} dismissedAt
 * @param {boolean} pending
 */
function savedDismissal(jobId, dismissedAt, pending) {
    return pending ? { jobId, dismissedAt, pending: true } : { jobId, dismissedAt };
}

/**
 * Create a dismissal store over a storage-like object.
 * @param {Object} options
 * @param {() => Storage} options.getStorage - Returns the storage holding the saved list (getItem/setItem); called on
 *     every access, so a storage that throws when reached degrades like one that throws on use
 * @param {string} options.key - Storage key of the saved list
 * @param {number} options.ttlMs - How long a dismissal lasts; older saved dismissals are ignored and dropped on the next write
 * @param {() => number} [options.now] - Clock in epoch milliseconds
 * @returns {DismissalStore}
 */
export function createDismissalStore({ getStorage, key, ttlMs, now = Date.now }) {
    /** @type {Map<string, number>} dismissedAt by jobId */
    const dismissed = new Map();
    const savedList = createSavedEntryList({
        getStorage,
        key,
        ttlMs,
        now,
        idField: 'jobId',
        timeField: 'dismissedAt',
        toEntry: entry => savedDismissal(entry.jobId, entry.dismissedAt, entry.pending === true)
    });

    return {
        has: jobId => dismissed.has(jobId),

        load() {
            savedList.update(saved => {
                for (const [jobId, { dismissedAt, pending }] of saved) {
                    if (!pending) {
                        dismissed.set(jobId, dismissedAt);
                    }
                }
            });
        },

        record(jobIds, { pending = false } = {}) {
            const dismissedAt = now();
            if (jobIds.length > 0) {
                for (const jobId of jobIds) {
                    dismissed.set(jobId, dismissedAt);
                }
                savedList.update(saved => {
                    for (const jobId of jobIds) {
                        saved.set(jobId, savedDismissal(jobId, dismissedAt, pending));
                    }
                });
            }

            return dismissedAt;
        },

        confirm(jobId, dismissedAt) {
            savedList.update(saved => {
                if (saved.get(jobId)?.dismissedAt === dismissedAt) {
                    saved.set(jobId, savedDismissal(jobId, dismissedAt, false));
                }
            });
        },

        drop(jobId, dismissedAt) {
            if (dismissed.get(jobId) === dismissedAt) {
                dismissed.delete(jobId);
            }
            savedList.update(saved => {
                if (saved.get(jobId)?.dismissedAt === dismissedAt) {
                    saved.delete(jobId);
                }
            });
        }
    };
}
