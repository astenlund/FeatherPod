import { FEED_ID, VAPID_PUBLIC_KEY, NOTIF_ENABLED_KEY, NOTIF_HINT_SHOWN_KEY, FAKE_PWA } from './config.js';
import { isActiveWork, isInUploadPhase, showToast } from './utils.js';
import { getApiKey } from './auth.js';
import { getQueue } from './queue.js';

function isInstalledPwa() {
    return FAKE_PWA || window.matchMedia('(display-mode: standalone)').matches;
}

function isPushSupported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

let toggleAvailable;

export function isNotificationToggleAvailable() {
    if (toggleAvailable === undefined) {
        const toggle = document.getElementById('notif-toggle');
        toggleAvailable = !!(toggle && VAPID_PUBLIC_KEY && isPushSupported() && isInstalledPwa());
    }

    return toggleAvailable;
}

/**
 * Set both representations of the toggle's enabled state (aria-pressed and localStorage) so they cannot drift.
 * @param {boolean} enabled
 */
function setToggleState(enabled) {
    document.getElementById('notif-toggle').setAttribute('aria-pressed', enabled ? 'true' : 'false');
    if (enabled) {
        localStorage.setItem(NOTIF_ENABLED_KEY, 'true');
    } else {
        localStorage.removeItem(NOTIF_ENABLED_KEY);
    }
}

/**
 * Initialize the push notification toggle button. Restores a previously enabled
 * toggle by re-subscribing, rolling the state back if the subscription fails.
 */
export function initNotificationToggle() {
    if (!isNotificationToggleAvailable()) {
        return;
    }
    const toggle = document.getElementById('notif-toggle');

    const queue = getQueue();
    const hasActiveQueue = queue.some(e => isActiveWork(e));
    const wasEnabled = localStorage.getItem(NOTIF_ENABLED_KEY) === 'true';
    if (hasActiveQueue && wasEnabled && Notification.permission === 'granted') {
        setToggleState(true);
        subscribeToPush()
            .then(() => syncPushSession(undefined, queue))
            .catch(() => setToggleState(false));
    } else {
        setToggleState(false);
        deleteServerSubscription();
    }

    toggle.addEventListener('click', handleNotificationToggle);
}

function postSubscriptionToServer(subscription) {
    const key = subscription.getKey('p256dh');
    const auth = subscription.getKey('auth');

    return fetch(`/api/feeds/${FEED_ID}/push-subscriptions`, {
        method: 'POST',
        headers: { 'X-API-Key': getApiKey(), 'Content-Type': 'application/json' },
        body: JSON.stringify({
            endpoint: subscription.endpoint,
            p256dh: btoa(String.fromCharCode(...new Uint8Array(key))),
            auth: btoa(String.fromCharCode(...new Uint8Array(auth))),
        }),
    });
}

function deleteSubscriptionOnServer(endpoint) {
    return fetch(`/api/feeds/${FEED_ID}/push-subscriptions`, {
        method: 'DELETE',
        headers: { 'X-API-Key': getApiKey(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ endpoint }),
    });
}

async function deleteServerSubscription() {
    try {
        const reg = await navigator.serviceWorker.ready;
        const subscription = await reg.pushManager.getSubscription();
        if (!subscription) {
            return;
        }
        await deleteSubscriptionOnServer(subscription.endpoint);
    } catch {
        // Best-effort; server prunes via 410 Gone on next send.
    }
}

/**
 * Sync active jobIds with the server's push session.
 * @param {string[]} [jobIds] - specific jobIds to track; derived from uploadQueue when omitted
 * @param {Array} uploadQueue - current upload queue (required; every caller passes a real queue)
 */
export function syncPushSession(jobIds, uploadQueue) {
    if (!isNotificationToggleEnabled()) {
        return;
    }
    const ids = jobIds || uploadQueue.filter(e => e.jobId && isActiveWork(e)).map(e => e.jobId);
    const uploadsRemaining = uploadQueue.filter(e => (e.status === 'queued' || isInUploadPhase(e)) && !e.validationError).length;
    if (ids.length === 0 && uploadsRemaining === 0) {
        return;
    }
    fetch(`/api/feeds/${FEED_ID}/push-sessions`, {
        method: 'POST',
        headers: { 'X-API-Key': getApiKey(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ jobIds: ids, uploadsRemaining }),
    }).catch(() => {});
}

export function setNotificationToggleVisible(visible) {
    if (!isNotificationToggleAvailable()) {
        return;
    }
    document.getElementById('notif-toggle').hidden = !visible;
}

export function resetNotificationToggle() {
    if (!isNotificationToggleAvailable()) {
        return;
    }
    const wasEnabled = isNotificationToggleEnabled();
    setToggleState(false);
    if (wasEnabled) {
        deleteServerSubscription();
    }
}

export function isNotificationToggleEnabled() {
    if (!isNotificationToggleAvailable()) {
        return false;
    }

    return document.getElementById('notif-toggle').getAttribute('aria-pressed') === 'true';
}

function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(base64);
    const array = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) {
        array[i] = raw.charCodeAt(i);
    }

    return array;
}

async function handleNotificationToggle() {
    const toggle = document.getElementById('notif-toggle');
    const isEnabled = toggle.getAttribute('aria-pressed') === 'true';

    toggle.disabled = true;

    if (isEnabled) {
        setToggleState(false);
        if (FAKE_PWA) {
            toggle.disabled = false;
        } else {
            try {
                const reg = await navigator.serviceWorker.ready;
                const subscription = await reg.pushManager.getSubscription();
                if (subscription) {
                    await deleteSubscriptionOnServer(subscription.endpoint);
                    await subscription.unsubscribe();
                }
            } catch (e) {
                console.warn('Failed to unsubscribe from push notifications:', e);
                setToggleState(true);
                showToast("Couldn't disable notifications", 5000, 'notif-toggle-error');
            } finally {
                toggle.disabled = false;
            }
        }
    } else {
        const permission = await Notification.requestPermission();
        if (permission !== 'granted') {
            toggle.disabled = false;

            return;
        }

        setToggleState(true);
        if (FAKE_PWA) {
            showBatteryOptimizationHint();
            toggle.disabled = false;
        } else {
            try {
                await subscribeToPush();
                syncPushSession(undefined, getQueue());
                showBatteryOptimizationHint();
            } catch (e) {
                console.warn('Failed to subscribe to push notifications:', e);
                setToggleState(false);
                showToast("Couldn't enable notifications", 5000, 'notif-toggle-error');
            } finally {
                toggle.disabled = false;
            }
        }
    }
}

function applicationServerKeyMatches(existingKeyBuffer, expectedBytes) {
    if (!existingKeyBuffer) {
        return false;
    }
    const existing = new Uint8Array(existingKeyBuffer);
    if (existing.length !== expectedBytes.length) {
        return false;
    }
    for (let i = 0; i < expectedBytes.length; i++) {
        if (existing[i] !== expectedBytes[i]) {
            return false;
        }
    }

    return true;
}

async function ensureCompatibleSubscription(reg) {
    const existing = await reg.pushManager.getSubscription();
    if (!existing) {
        return;
    }
    const expected = urlBase64ToUint8Array(VAPID_PUBLIC_KEY);
    if (applicationServerKeyMatches(existing.options?.applicationServerKey, expected)) {
        return;
    }
    try {
        await deleteSubscriptionOnServer(existing.endpoint);
    } catch {
        // Best-effort; server prunes via 410 Gone on next send.
    }
    await existing.unsubscribe();
}

async function subscribeToPush() {
    const reg = await navigator.serviceWorker.ready;
    await ensureCompatibleSubscription(reg);
    const subscription = await reg.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(VAPID_PUBLIC_KEY),
    });
    const response = await postSubscriptionToServer(subscription);
    if (!response.ok) {
        await subscription.unsubscribe();
        throw new Error(`Server rejected subscription: ${response.status}`);
    }
}

function showBatteryOptimizationHint() {
    if (localStorage.getItem(NOTIF_HINT_SHOWN_KEY)) {
        return;
    }
    if (!FAKE_PWA && !/android/i.test(navigator.userAgent)) {
        return;
    }
    localStorage.setItem(NOTIF_HINT_SHOWN_KEY, 'true');

    showToast('Tip: disable battery optimization for reliable notifications', 10000);
}

/**
 * Fire a local notification when the queue finishes (FAKE_PWA dev mode only).
 * @param {{completed: number, failed: number}} stats
 */
export function notifyQueueComplete(stats) {
    if (!isNotificationToggleEnabled() || !FAKE_PWA) {
        return;
    }

    const { completed, failed } = stats;

    let body;
    if (completed > 0 && failed > 0) {
        body = `${completed} pushed, ${failed} failed`;
    } else if (completed > 0) {
        body = completed === 1 ? '1 episode pushed' : `${completed} episodes pushed`;
    } else if (failed > 0) {
        body = failed === 1 ? '1 episode failed' : `${failed} episodes failed`;
    } else {
        return;
    }

    const iconUrl = document.getElementById('feed-artwork')?.src;
    const title = document.getElementById('feed-name')?.textContent?.trim() || 'FeatherPod';

    if (Notification.permission === 'granted') {
        new Notification(title, { body, icon: iconUrl || undefined, data: { feedId: FEED_ID } });
    }

    resetNotificationToggle();
}
