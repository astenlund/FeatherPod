import { WAKE_LOCK_KEY } from './config.js';

let wakeLockSentinel = null;
let wakeLockGeneration = 0;

function isWakeLockAvailable() {
    if (isWakeLockAvailable._cached !== undefined) {
        return isWakeLockAvailable._cached;
    }
    isWakeLockAvailable._cached = !!navigator.wakeLock;

    return isWakeLockAvailable._cached;
}

export function isWakeLockTogglePressed() {
    const toggle = document.getElementById('wake-lock-toggle');

    return toggle?.getAttribute('aria-pressed') === 'true';
}

export function initWakeLockToggle() {
    if (!isWakeLockAvailable()) {
        return;
    }
    const toggle = document.getElementById('wake-lock-toggle');
    if (localStorage.getItem(WAKE_LOCK_KEY) === 'true') {
        toggle.setAttribute('aria-pressed', 'true');
    }
    toggle.addEventListener('click', handleWakeLockToggle);
}

export async function acquireWakeLock() {
    const gen = ++wakeLockGeneration;
    try {
        const sentinel = await navigator.wakeLock.request('screen');
        if (gen !== wakeLockGeneration) {
            await sentinel.release();

            return;
        }
        wakeLockSentinel = sentinel;
        wakeLockSentinel.addEventListener('release', () => {
            if (wakeLockSentinel === sentinel) {
                wakeLockSentinel = null;
            }
        });
    } catch {
        // Silently fail
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
    const toggle = document.getElementById('wake-lock-toggle');
    const isEnabled = toggle.getAttribute('aria-pressed') === 'true';

    if (isEnabled) {
        toggle.setAttribute('aria-pressed', 'false');
        localStorage.removeItem(WAKE_LOCK_KEY);
        await releaseWakeLock();
    } else {
        toggle.setAttribute('aria-pressed', 'true');
        localStorage.setItem(WAKE_LOCK_KEY, 'true');
        await acquireWakeLock();
        if (!wakeLockSentinel) {
            toggle.setAttribute('aria-pressed', 'false');
        }
    }
}

export function setWakeLockToggleVisible(visible) {
    if (!isWakeLockAvailable()) {
        return;
    }
    const toggle = document.getElementById('wake-lock-toggle');
    toggle.hidden = !visible;

    if (visible && localStorage.getItem(WAKE_LOCK_KEY) === 'true') {
        toggle.setAttribute('aria-pressed', 'true');
        acquireWakeLock();
    } else if (!visible) {
        releaseWakeLock();
    }
}

export function resetWakeLockToggle() {
    if (!isWakeLockAvailable()) {
        return;
    }
    document.getElementById('wake-lock-toggle').setAttribute('aria-pressed', 'false');
    releaseWakeLock();
}
