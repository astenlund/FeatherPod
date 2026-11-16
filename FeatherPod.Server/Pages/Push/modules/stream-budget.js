/**
 * A cap on how many long-lived streams the page keeps open at once.
 *
 * On a plain HTTP/1.1 origin Chromium opens at most six connections per host, and every open EventSource holds one
 * for as long as it lives. Without a cap, enough per-job progress streams fill the pool and later requests to the
 * same host (an upload, an API call) never leave the browser.
 *
 * The budget does not count acquisitions and releases. Each holder registers a predicate that reports whether its
 * stream is still open, and the budget drops holders whose predicate reports false whenever it is asked for room.
 * A stream closed by any path (for example its own handlers, a cancel, a recent-jobs merge or the removal of a finished
 * entry) therefore frees its slot without that path knowing about the budget, and a stream nobody closed keeps its
 * slot, since it still holds a connection.
 *
 * Pure module (no DOM, no globals), unit-tested under tests/push/.
 */

/**
 * @typedef {Object} StreamBudget
 * @property {(isOpen: () => boolean) => boolean} tryAcquire - Takes a slot when one is free and returns true; the
 *   slot stays taken while isOpen() returns true. Returns false, registering nothing, when every slot is taken. The
 *   caller must open its stream synchronously after a true result, before isOpen() can be consulted.
 * @property {() => boolean} hasRoom - Whether tryAcquire would take a slot now.
 * @property {() => number} openCount - The number of slots taken by streams still open.
 */

/**
 * Create a stream budget.
 * @param {number} limit - Maximum number of streams open at once; a positive integer
 * @returns {StreamBudget}
 */
export function createStreamBudget(limit) {
    if (!Number.isInteger(limit) || limit < 1) {
        throw new RangeError('Stream budget limit must be a positive integer');
    }

    /** @type {Set<{isOpen: () => boolean}>} One holder per acquisition, even when two share a predicate */
    const holders = new Set();

    function prune() {
        for (const holder of holders) {
            let open;
            try {
                open = holder.isOpen();
            } catch {
                // A predicate that cannot answer keeps its slot: freeing it could open one stream too many
                open = true;
            }
            if (!open) {
                holders.delete(holder);
            }
        }
    }

    return {
        tryAcquire(isOpen) {
            prune();
            if (holders.size >= limit) {
                return false;
            }
            holders.add({ isOpen });

            return true;
        },
        hasRoom() {
            prune();

            return holders.size < limit;
        },
        openCount() {
            prune();

            return holders.size;
        }
    };
}
