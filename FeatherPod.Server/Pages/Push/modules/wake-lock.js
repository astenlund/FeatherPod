import { WAKE_LOCK_KEY } from './config.js';

const wakeLockAvailable = 'wakeLock' in navigator;

let wakeLockSentinel = null;
let wakeLockGeneration = 0;

/**
 * Set both representations of the toggle's enabled state (aria-pressed and localStorage) so they cannot drift.
 * @param {boolean} enabled
 */
function setToggleState(enabled) {
    document.getElementById('wake-lock-toggle').setAttribute('aria-pressed', enabled ? 'true' : 'false');
    if (enabled) {
        localStorage.setItem(WAKE_LOCK_KEY, 'true');
    } else {
        localStorage.removeItem(WAKE_LOCK_KEY);
    }
}

export function isWakeLockTogglePressed() {
    const toggle = document.getElementById('wake-lock-toggle');

    return toggle?.getAttribute('aria-pressed') === 'true';
}

export function initWakeLockToggle() {
    if (!wakeLockAvailable) {
        return;
    }
    if (localStorage.getItem(WAKE_LOCK_KEY) === 'true') {
        setToggleState(true);
    }
    document.getElementById('wake-lock-toggle').addEventListener('click', handleWakeLockToggle);
}

/**
 * Request the screen wake lock.
 * @returns {Promise<boolean>} false only when this request was the latest one and it failed;
 *   a request superseded by a newer acquire or release leaves the outcome to that newer call
 */
export async function acquireWakeLock() {
    const gen = ++wakeLockGeneration;
    try {
        const sentinel = await navigator.wakeLock.request('screen');
        if (gen !== wakeLockGeneration) {
            await sentinel.release();

            return true;
        }
        wakeLockSentinel = sentinel;
        sentinel.addEventListener('release', () => {
            if (wakeLockSentinel === sentinel) {
                wakeLockSentinel = null;
            }
        });

        return true;
    } catch {
        // Denied (for example while the document is hidden); the caller decides whether to roll back
        return gen !== wakeLockGeneration;
    }
}

async function releaseWakeLock() {
    wakeLockGeneration++;
    const sentinel = wakeLockSentinel;
    wakeLockSentinel = null;
    if (sentinel) {
        try {
            await sentinel.release();
        } catch {
            // Already released
        }
    }
}

async function handleWakeLockToggle() {
    if (isWakeLockTogglePressed()) {
        setToggleState(false);
        await releaseWakeLock();
    } else {
        setToggleState(true);
        if (!(await acquireWakeLock())) {
            setToggleState(false);
        }
    }
}

export function setWakeLockToggleVisible(visible) {
    if (!wakeLockAvailable) {
        return;
    }
    document.getElementById('wake-lock-toggle').hidden = !visible;

    if (visible && localStorage.getItem(WAKE_LOCK_KEY) === 'true') {
        setToggleState(true);
        acquireWakeLock();
    } else if (!visible) {
        releaseWakeLock();
    }
}

/**
 * Clear the pressed state and release the lock when the queue finishes, so a later tab
 * reactivation does not re-acquire it while idle. Deliberately leaves WAKE_LOCK_KEY alone:
 * the preference persists across queue completions and re-enables the toggle on the next
 * active work, which is why this does not go through setToggleState.
 */
export function resetWakeLockToggle() {
    if (!wakeLockAvailable) {
        return;
    }
    document.getElementById('wake-lock-toggle').setAttribute('aria-pressed', 'false');
    releaseWakeLock();
}
