import { FEED_ID, VAPID_PUBLIC_KEY, NOTIF_ENABLED_KEY, NOTIF_HINT_SHOWN_KEY, FAKE_PWA } from './config.js';
import { isActiveWork } from './utils.js';
import { getApiKey } from './auth.js';
import { getQueue } from './queue.js';

export function isInstalledPwa() {
    return FAKE_PWA || window.matchMedia('(display-mode: standalone)').matches;
}

function isPushSupported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

export function isNotificationToggleAvailable() {
    if (isNotificationToggleAvailable._cached !== undefined) {
        return isNotificationToggleAvailable._cached;
    }
    const toggle = document.getElementById('notif-toggle');
    isNotificationToggleAvailable._cached = !!(toggle && VAPID_PUBLIC_KEY && isPushSupported() && isInstalledPwa());

    return isNotificationToggleAvailable._cached;
}

/**
 * Initialize the push notification toggle button.
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
        toggle.setAttribute('aria-pressed', 'true');
        refreshPushSubscription();
        syncPushSession(undefined, queue);
    } else {
        localStorage.removeItem(NOTIF_ENABLED_KEY);
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

function refreshPushSubscription() {
    navigator.serviceWorker.ready.then(reg => reg.pushManager.getSubscription()).then(subscription => {
        if (!subscription) {
            return;
        }
        postSubscriptionToServer(subscription);
    }).catch(() => {});
}

function deleteServerSubscription() {
    navigator.serviceWorker.ready.then(reg => reg.pushManager.getSubscription()).then(subscription => {
        if (!subscription) {
            return;
        }
        fetch(`/api/feeds/${FEED_ID}/push-subscriptions`, {
            method: 'DELETE',
            headers: { 'X-API-Key': getApiKey(), 'Content-Type': 'application/json' },
            body: JSON.stringify({ endpoint: subscription.endpoint }),
        });
    }).catch(() => {});
}

/**
 * Sync active jobIds with the server's push session.
 * @param {string[]} [jobIds] - specific jobIds to track
 * @param {Array} uploadQueue - current upload queue
 */
export function syncPushSession(jobIds, uploadQueue) {
    if (!isNotificationToggleEnabled()) {
        return;
    }
    const ids = jobIds || uploadQueue.filter(e => e.jobId && isActiveWork(e)).map(e => e.jobId);
    const uploadsRemaining = uploadQueue.filter(e => (e.status === 'queued' || e.status === 'uploading') && !e.validationError).length;
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
    const toggle = document.getElementById('notif-toggle');
    const wasEnabled = toggle.getAttribute('aria-pressed') === 'true';
    toggle.setAttribute('aria-pressed', 'false');
    localStorage.removeItem(NOTIF_ENABLED_KEY);
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
        toggle.setAttribute('aria-pressed', 'false');
        localStorage.removeItem(NOTIF_ENABLED_KEY);
        if (FAKE_PWA) {
            toggle.disabled = false;
        } else {
            try {
                const reg = await navigator.serviceWorker.ready;
                const subscription = await reg.pushManager.getSubscription();
                if (subscription) {
                    await fetch(`/api/feeds/${FEED_ID}/push-subscriptions`, {
                        method: 'DELETE',
                        headers: { 'X-API-Key': getApiKey(), 'Content-Type': 'application/json' },
                        body: JSON.stringify({ endpoint: subscription.endpoint }),
                    });
                    await subscription.unsubscribe();
                }
            } catch (e) {
                console.warn('Failed to unsubscribe from push notifications:', e);
                toggle.setAttribute('aria-pressed', 'true');
                localStorage.setItem(NOTIF_ENABLED_KEY, 'true');
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

        toggle.setAttribute('aria-pressed', 'true');
        localStorage.setItem(NOTIF_ENABLED_KEY, 'true');
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
                toggle.setAttribute('aria-pressed', 'false');
                localStorage.removeItem(NOTIF_ENABLED_KEY);
            } finally {
                toggle.disabled = false;
            }
        }
    }
}

async function subscribeToPush() {
    const reg = await navigator.serviceWorker.ready;
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

    const hint = document.createElement('div');
    hint.className = 'notif-hint';
    hint.textContent = 'Tip: disable battery optimization for reliable notifications';
    hint.addEventListener('click', () => hint.remove());
    document.body.appendChild(hint);
    setTimeout(() => hint.remove(), 10000);
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
