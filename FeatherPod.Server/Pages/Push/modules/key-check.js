/**
 * API key check rules -- pure functions with no DOM or window dependencies.
 *
 * Kept separate from auth.js so the rules can be unit-tested under Node
 * (tests/push/key-check.test.js, run via `npm test`).
 */

/** Outcomes of a /api/users/me response status */
export const KEY_CHECK_STATUS = Object.freeze({
    OK: 'ok',
    REJECTED: 'rejected',
    SERVER_ERROR: 'server-error'
});

/**
 * Classify a /api/users/me response status. Only 401 is a verdict on the key; any other failure (a 502 or 503 while
 * the App Service restarts, for example) says nothing about it.
 * @param {number} status - HTTP status code
 * @returns {string} One of KEY_CHECK_STATUS
 */
export function classifyKeyCheckStatus(status) {
    if (status >= 200 && status < 300) {
        return KEY_CHECK_STATUS.OK;
    }

    return status === 401 ? KEY_CHECK_STATUS.REJECTED : KEY_CHECK_STATUS.SERVER_ERROR;
}

/**
 * Whether a key check ended without a verdict on the key: the server was unreachable or answered with an error.
 * @param {{networkError: boolean, serverError: boolean}} result
 * @returns {boolean}
 */
export function isKeyCheckInconclusive(result) {
    return result.networkError || result.serverError;
}

/**
 * Run a key check, repeating it after `delayMs` while it is inconclusive, at most `retries` more times.
 * @template {{networkError: boolean, serverError: boolean}} T
 * @param {() => Promise<T>} check - Performs one key check
 * @param {Object} [options]
 * @param {number} [options.retries=2] - Additional attempts after the first
 * @param {number} [options.delayMs=1000] - Pause before each additional attempt
 * @param {(ms: number) => Promise<void>} [options.wait] - Pause implementation (injectable for tests)
 * @returns {Promise<T>} The first conclusive result, or the last inconclusive one
 */
export async function retryKeyCheck(check, { retries = 2, delayMs = 1000, wait = ms => new Promise(r => setTimeout(r, ms)) } = {}) {
    let result = await check();
    for (let attempt = 0; attempt < retries && isKeyCheckInconclusive(result); attempt++) {
        await wait(delayMs);
        result = await check();
    }

    return result;
}

/**
 * Banner text for a key kept without a verdict because its check was inconclusive.
 * @param {{serverError: boolean}} result - The inconclusive check result
 * @param {string} keyDescription - Which key is kept, e.g. 'URL key' or 'saved key'
 * @returns {string}
 */
export function unverifiedKeyMessage(result, keyDescription) {
    return (result.serverError ? 'Server error' : 'Server unreachable') + ', using ' + keyDescription;
}
