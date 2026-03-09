self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(clients.claim()));

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    if (event.request.method === 'POST' && url.pathname.endsWith('/push')) {
        event.respondWith((async () => {
            try {
                const formData = await event.request.formData();
                const files = formData.getAll('audio');

                if (files.length > 0) {
                    const cache = await caches.open('share-target');
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

    // Stale-while-revalidate for push page navigation
    if (event.request.method === 'GET' && url.pathname.endsWith('/push')
        && event.request.mode === 'navigate') {
        event.respondWith((async () => {
            const cache = await caches.open('push-page');
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
});
