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

/**
 * Show the toggle unpressed without touching WAKE_LOCK_KEY, so the saved preference still presses it again on the next
 * active work.
 */
function unpressToggleKeepingPreference() {
    document.getElementById('wake-lock-toggle').setAttribute('aria-pressed', 'false');
}

export function isWakeLockTogglePressed() {
    const toggle = document.getElementById('wake-lock-toggle');

    return toggle?.getAttribute('aria-pressed') === 'true';
}

/**
 * Wire the toggle's click handler and restore a persisted preference while the toggle is visible.
 * Visibility owns the pressed state: a hidden toggle stays unpressed so the visibilitychange
 * re-acquire in push.js stays idle after a reload with no active work.
 */
export function initWakeLockToggle() {
    if (!wakeLockAvailable) {
        return;
    }
    const toggle = document.getElementById('wake-lock-toggle');
    if (!toggle.hidden && localStorage.getItem(WAKE_LOCK_KEY) === 'true') {
        setToggleState(true);
    }
    toggle.addEventListener('click', handleWakeLockToggle);
}

/**
 * Request the screen wake lock unless one is already held.
 * @returns {Promise<boolean>} false only when this request was the latest one and it failed;
 *   a request superseded by a newer acquire or release leaves the outcome to that newer call
 */
async function acquireWakeLock() {
    if (wakeLockSentinel) {
        return true;
    }
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

/**
 * Acquire the lock for the pressed toggle without a click: when the saved preference shows the toggle (see
 * setWakeLockToggleVisible) and when the tab becomes visible again (the visibilitychange handler in push.js).
 * A rejection while the document is hidden (the browser refuses wake locks for hidden pages) keeps the toggle pressed,
 * so the re-acquire on tab reactivation still runs. A rejection while it is visible is a real denial: the toggle is
 * unpressed, since no lock is held, but WAKE_LOCK_KEY stays set, so the next active work tries again. A request
 * superseded by a newer acquire or release leaves the toggle to that newer call.
 */
export async function acquireWakeLockForPressedToggle() {
    if (await acquireWakeLock()) {
        return;
    }
    if (document.visibilityState !== 'hidden') {
        unpressToggleKeepingPreference();
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

/**
 * Show or hide the toggle. Showing it with the saved preference set presses it and acquires the lock (see
 * acquireWakeLockForPressedToggle for a rejected request); hiding it releases the lock.
 * @param {boolean} visible
 */
export function setWakeLockToggleVisible(visible) {
    if (!wakeLockAvailable) {
        return;
    }
    document.getElementById('wake-lock-toggle').hidden = !visible;

    if (visible && localStorage.getItem(WAKE_LOCK_KEY) === 'true') {
        setToggleState(true);
        acquireWakeLockForPressedToggle();
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
    unpressToggleKeepingPreference();
    releaseWakeLock();
}
