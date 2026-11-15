/**
 * The page's record of dismissed jobs: jobs whose entries were dismissed, cleared or cancelled, which recent-jobs merges
 * must not add back (pure, no DOM).
 *
 * The store keeps an in-memory map for this tab and a saved list in storage that every tab of the browser on the same
 * feed shares, so a dismissal survives a reload for the TTL. The saved list is changed job by job rather than replaced
 * with this tab's map, so dismissals other tabs saved after this tab loaded survive this tab's writes.
 * Kept separate so it can be unit-tested under Node (tests/push/dismissal-store.test.js, run via `npm test`).
 */

/**
 * @typedef {Object} DismissalStore
 * @property {(jobId: string) => boolean} has - Whether this tab treats the job as dismissed
 * @property {() => void} load - Load the saved dismissals into this tab's map, dropping expired ones from storage
 * @property {(jobIds: string[]) => number} record - Record jobs as dismissed in this tab's map and in storage; returns
 *     the time recorded as their dismissal, which drop takes to drop one again
 * @property {(jobId: string, dismissedAt: number) => void} drop - Drop a job's dismissal from this tab's map and from
 *     storage, from each only while it still holds dismissedAt, so a dismissal another path or tab recorded since is kept
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

    /**
     * Read the saved dismissals, without the ones older than the TTL or malformed.
     * @returns {Map<string, number>} dismissedAt by jobId
     */
    function readSaved() {
        const saved = new Map();
        const entries = parseJson(getStorage().getItem(key));
        if (!Array.isArray(entries)) {
            return saved;
        }
        const oldestKept = now() - ttlMs;
        for (const entry of entries) {
            if (entry?.jobId && entry.dismissedAt && entry.dismissedAt >= oldestKept) {
                saved.set(entry.jobId, entry.dismissedAt);
            }
        }

        return saved;
    }

    /**
     * Apply a change to the saved dismissals, dropping expired ones on the way.
     * @param {(saved: Map<string, number>) => void} change - Mutates the saved dismissals (dismissedAt by jobId)
     */
    function updateSaved(change) {
        try {
            const saved = readSaved();
            change(saved);
            getStorage().setItem(key, JSON.stringify(Array.from(saved, ([jobId, dismissedAt]) => ({ jobId, dismissedAt }))));
        } catch {
            // Storage unavailable or full: the saved list keeps its previous contents
        }
    }

    return {
        has: jobId => dismissed.has(jobId),

        load() {
            updateSaved(saved => {
                for (const [jobId, dismissedAt] of saved) {
                    dismissed.set(jobId, dismissedAt);
                }
            });
        },

        record(jobIds) {
            const dismissedAt = now();
            if (jobIds.length > 0) {
                for (const jobId of jobIds) {
                    dismissed.set(jobId, dismissedAt);
                }
                updateSaved(saved => {
                    for (const jobId of jobIds) {
                        saved.set(jobId, dismissedAt);
                    }
                });
            }

            return dismissedAt;
        },

        drop(jobId, dismissedAt) {
            if (dismissed.get(jobId) === dismissedAt) {
                dismissed.delete(jobId);
            }
            updateSaved(saved => {
                if (saved.get(jobId) === dismissedAt) {
                    saved.delete(jobId);
                }
            });
        }
    };
}
