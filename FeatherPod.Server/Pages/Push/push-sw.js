const SW_VERSION = '{{SW_VERSION}}'; // eslint-disable-line no-unused-vars -- triggers SW update on new builds
const CACHE_NAME = 'push-page';
const SHARE_CACHE_NAME = 'share-target';
const JS_VALIDATE_TIMEOUT_MS = 3000;

const CACHED_ASSET_SUFFIXES = [
    '/app.js',
    '/app.css',
    '/modules/config.js',
    '/modules/events.js',
    '/modules/utils.js',
    '/modules/auth.js',
    '/modules/artwork.js',
    '/modules/wake-lock.js',
    '/modules/notifications.js',
    '/modules/progress.js',
    '/modules/state.js',
    '/modules/queue-ui.js',
    '/modules/queue.js',
    '/modules/history.js',
    '/modules/editing.js',
    '/modules/server-sync.js',
    '/modules/youtube.js',
];

self.addEventListener('install', (event) => {
    event.waitUntil((async () => {
        const scope = new URL(self.registration.scope).pathname;
        const cache = await caches.open(CACHE_NAME);
        for (const suffix of CACHED_ASSET_SUFFIXES) {
            try {
                await cache.add(scope + suffix);
            } catch (e) {
                // Best-effort -- don't prevent installation if pre-caching fails
            }
        }
        self.skipWaiting();
    })());
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        await clients.claim();
        const scope = new URL(self.registration.scope).pathname;
        const validPaths = new Set(CACHED_ASSET_SUFFIXES.map(s => scope + s));
        const cache = await caches.open(CACHE_NAME);
        const keys = await cache.keys();
        await Promise.all(keys.map(req => {
            if (!validPaths.has(new URL(req.url).pathname)) {
                return cache.delete(req);
            }
        }));
    })());
});

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

    // Share target POST -- unchanged
    if (event.request.method === 'POST' && url.pathname.endsWith('/push')) {
        event.respondWith((async () => {
            try {
                const formData = await event.request.formData();

                const sharedText = formData.get('shared_text');
                if (sharedText) {
                    const redirectUrl = new URL(event.request.url);
                    redirectUrl.searchParams.set('yt', sharedText);
                    return Response.redirect(redirectUrl.href, 303);
                }

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

    if (event.request.method !== 'GET') return;

    const isNavigation = url.pathname.endsWith('/push') && event.request.mode === 'navigate';
    const isCss = url.pathname.endsWith('/push/app.css');
    const isJs = url.pathname.endsWith('/push/app.js') || url.pathname.includes('/push/modules/');

    // HTML -- network only (always fresh after deploy)
    if (isNavigation) {
        event.respondWith(
            fetch(event.request).catch(() => new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } }))
        );
        return;
    }

    // CSS -- stale-while-revalidate + postMessage on ETag change
    if (isCss) {
        event.respondWith((async () => {
            const cache = await caches.open(CACHE_NAME);
            const cached = await cache.match(event.request);

            const revalidate = fetch(event.request).then(async (response) => {
                if (!response.ok) return response;
                const newEtag = response.headers.get('etag');
                const oldEtag = cached?.headers.get('etag');
                await cache.put(event.request, response.clone());
                if (newEtag && oldEtag && newEtag !== oldEtag) {
                    const allClients = await clients.matchAll({ type: 'window' });
                    for (const client of allClients) {
                        client.postMessage({ type: 'css-updated' });
                    }
                }
                return response;
            }).catch(() => null);

            if (cached) {
                event.waitUntil(revalidate);
                return cached;
            }

            return (await revalidate) || new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } });
        })());
        return;
    }

    // JS -- cache-then-validate with timeout (conditional GET via If-None-Match)
    if (isJs) {
        event.respondWith((async () => {
            const cache = await caches.open(CACHE_NAME);
            const cached = await cache.match(event.request);
            const cachedEtag = cached?.headers.get('etag');

            const controller = new AbortController();
            const timeoutId = setTimeout(() => controller.abort(), JS_VALIDATE_TIMEOUT_MS);

            try {
                const init = { signal: controller.signal };
                if (cachedEtag) {
                    init.headers = { 'If-None-Match': cachedEtag };
                }
                const response = await fetch(event.request, init);
                clearTimeout(timeoutId);

                if (response.status === 304 && cached) {
                    return cached;
                }
                if (response.ok) {
                    await cache.put(event.request, response.clone());
                    return response;
                }

                return cached || new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } });
            } catch {
                clearTimeout(timeoutId);

                return cached || new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } });
            }
        })());
    }
});
