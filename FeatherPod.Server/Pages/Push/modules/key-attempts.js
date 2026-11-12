/**
 * Key attempt tracking -- pure state with no DOM or window dependencies.
 *
 * A key attempt is one key the page is trying: the key init installs on each run (from the URL fragment or storage), or
 * a key prompt waiting for a typed or pasted key. Each new attempt supersedes the one before it, so a continuation that
 * captured an older attempt (a late key check, a key entry, a startup run) leaves the page to the newer one. Each
 * attempt ends with at most one verdict: accepted (the key is in use: it validated, or its check was inconclusive and
 * the page kept it) or rejected. File intake waits for an accepted key, so offered files are not uploaded with a key the
 * page's key check rejected. The verdict is the page's check, not the server's answer to an upload: a key kept after an
 * inconclusive check is accepted unjudged, and a key revoked mid-session keeps its accepted verdict.
 *
 * Kept separate so it can be unit-tested under Node (tests/push/key-attempts.test.js, run via `npm test`).
 */

/** Verdicts on a key attempt */
export const KEY_VERDICT = Object.freeze({
    PENDING: 'pending',
    ACCEPTED: 'accepted',
    REJECTED: 'rejected'
});

/**
 * Create a key attempt tracker. Attempt 0 is current and pending until the first begin().
 * @returns {KeyAttempts}
 */
export function createKeyAttempts() {
    let current = 0;
    let verdict = KEY_VERDICT.PENDING;
    /** @type {Array<() => boolean>} Waiter checks, each returning true once it has resolved its promise */
    let waiters = [];

    function notify() {
        waiters = waiters.filter(resolveIfDone => !resolveIfDone());
    }

    function wait(resolveIfDone) {
        if (!resolveIfDone()) {
            waiters.push(resolveIfDone);
        }
    }

    return {
        get current() {
            return current;
        },

        get verdict() {
            return verdict;
        },

        begin() {
            current++;
            verdict = KEY_VERDICT.PENDING;
            notify();

            return current;
        },

        isSuperseded(attempt) {
            return attempt !== current;
        },

        isOpen(attempt) {
            return attempt === current && verdict === KEY_VERDICT.PENDING;
        },

        settle(attempt, newVerdict) {
            if (attempt !== current || verdict !== KEY_VERDICT.PENDING || newVerdict === KEY_VERDICT.PENDING) {
                return false;
            }

            verdict = newVerdict;
            notify();

            return true;
        },

        waitForVerdict(attempt) {
            return new Promise(resolve => wait(() => {
                if (attempt !== current || verdict === KEY_VERDICT.REJECTED) {
                    resolve(false);

                    return true;
                }

                if (verdict === KEY_VERDICT.ACCEPTED) {
                    resolve(true);

                    return true;
                }

                return false;
            }));
        },

        whenAccepted() {
            return new Promise(resolve => wait(() => {
                if (verdict === KEY_VERDICT.ACCEPTED) {
                    resolve();

                    return true;
                }

                return false;
            }));
        }
    };
}

/**
 * @typedef {Object} KeyAttempts
 * @property {number} current - The current attempt
 * @property {string} verdict - The current attempt's verdict, one of KEY_VERDICT
 * @property {() => number} begin - Start a new attempt, superseding the current one; returns it
 * @property {(attempt: number) => boolean} isSuperseded - Whether a newer attempt has begun since `attempt`
 * @property {(attempt: number) => boolean} isOpen - Whether `attempt` is current and still has no verdict, so a check
 *     of its key may still decide it (a key entered on a prompt still showing may have decided it first)
 * @property {(attempt: number, verdict: string) => boolean} settle - Record the verdict on `attempt`; ignored (returns
 *     false) when the attempt is superseded or already settled
 * @property {(attempt: number) => Promise<boolean>} waitForVerdict - Resolves true once `attempt` is accepted, false
 *     once it is rejected or superseded
 * @property {() => Promise<void>} whenAccepted - Resolves once the current attempt, whichever it is by then, is accepted
 */

/** The page's key attempts, shared by push.js (key handling) and server-sync.js (file intake) */
export const keyAttempts = createKeyAttempts();
