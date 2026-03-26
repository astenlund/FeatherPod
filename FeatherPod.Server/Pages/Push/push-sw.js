const CACHE_NAME = 'push-page';
const SHARE_CACHE_NAME = 'share-target';

self.addEventListener('install', (event) => {
    event.waitUntil((async () => {
        const scope = new URL(self.registration.scope).pathname;
        const cache = await caches.open(CACHE_NAME);
        const urls = [
            scope,
            `${scope}/app.js`,
            `${scope}/app.css`,
            `${scope}/modules/config.js`,
            `${scope}/modules/events.js`,
            `${scope}/modules/utils.js`,
            `${scope}/modules/auth.js`,
            `${scope}/modules/artwork.js`,
            `${scope}/modules/wake-lock.js`,
            `${scope}/modules/notifications.js`,
            `${scope}/modules/progress.js`,
            `${scope}/modules/state.js`,
            `${scope}/modules/queue-ui.js`,
            `${scope}/modules/queue.js`,
            `${scope}/modules/history.js`,
            `${scope}/modules/editing.js`,
            `${scope}/modules/server-sync.js`,
        ];
        for (const url of urls) {
            try {
                await cache.add(url);
            } catch (e) {
                // Best-effort — don't prevent installation if pre-caching fails
            }
        }
        self.skipWaiting();
    })());
});

self.addEventListener('activate', (event) => event.waitUntil(clients.claim()));

self.addEventListener('push', (event) => {
    if (!event.data) return;

    const data = event.data.json();
    const { title, body, icon, feedId } = data;

    event.waitUntil(
        self.registration.showNotification(title || 'FeatherPod', {
            body: body || '',
            icon: icon || '',
            data: { feedId },
        })
    );
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();

    const feedId = event.notification.data?.feedId;
    const targetUrl = feedId ? `/${feedId}/push` : '/';

    event.waitUntil((async () => {
        const windowClients = await clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = windowClients.find(c => new URL(c.url).pathname === targetUrl);

        if (existing) {
            await existing.focus();
        } else {
            await clients.openWindow(targetUrl);
        }
    })());
});

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    if (event.request.method === 'POST' && url.pathname.endsWith('/push')) {
        event.respondWith((async () => {
            try {
                const formData = await event.request.formData();
                const files = formData.getAll('audio');

                if (files.length > 0) {
                    const cache = await caches.open(SHARE_CACHE_NAME);
                    for (const file of files) {
                        const key = `/shared/${Date.now()}-${Math.random().toString(36).slice(2)}-${file.name}`;
                        await cache.put(key, new Response(file));
                    }
                }
            } catch (e) {
                // Fall through to redirect even if parsing fails
            }

            return Response.redirect(event.request.url, 303);
        })());
        return;
    }

    // Stale-while-revalidate for push page navigation and assets (JS, CSS)
    if (event.request.method === 'GET') {
        const isNavigation = url.pathname.endsWith('/push') && event.request.mode === 'navigate';
        const isAsset = url.pathname.endsWith('/push/app.js') || url.pathname.endsWith('/push/app.css') || url.pathname.includes('/push/modules/');

        if (isNavigation || isAsset) {
            event.respondWith((async () => {
                const cache = await caches.open(CACHE_NAME);
                const cached = await cache.match(event.request);

                const networkFetch = fetch(event.request).then(response => {
                    if (response.ok) {
                        cache.put(event.request, response.clone());
                    }
                    return response;
                }).catch(() => null);

                if (cached) {
                    event.waitUntil(networkFetch);
                    return cached;
                }

                return (await networkFetch) || new Response('Offline', {
                    status: 503,
                    headers: { 'Content-Type': 'text/plain' }
                });
            })());
        }
    }
});
