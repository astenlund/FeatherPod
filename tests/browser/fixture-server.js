import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Serves the real push page from disk the way FeatherPod.Server's push routes do (GeneratePushPageHtml, ServePushAsset,
 * ServePushModuleAsset in Program.cs) for a feed without artwork, so browser tests run the actual push.js startup with no
 * .NET server or storage. Everything under /api/ is left to the test's route interception; this server answers it 404.
 */

const pushDir = fileURLToPath(new URL('../../FeatherPod.Server/Pages/Push/', import.meta.url));

export const FEED_ID = 'test-feed';
export const FEED_TITLE = 'Test Feed';

/**
 * Template values for a feed without artwork in production mode with default configuration: no PWA head (so no service
 * worker registration), no icon cache buster, backdrop or artwork class, dev flags off, progress smoothing on
 * (PushPage:ProgressSmoothing defaults to true), no VAPID key (so no notification toggle).
 */
const templateValues = {
    PWA_HEAD: '',
    FEED_ID,
    FEED_TITLE,
    ICON_CACHE_BUSTER: '',
    DROP_ZONE_CLASS: '',
    BACKDROP_SRC: '',
    ICON_ETAG: '',
    IS_DEV: 'false',
    PROGRESS_SMOOTHING: 'true',
    VAPID_PUBLIC_KEY: '',
};

/** Render push.html with every known placeholder replaced; unknown placeholders are left for the test to detect. */
export async function renderPushPage() {
    const html = await readFile(path.join(pushDir, 'push.html'), 'utf8');

    return Object.entries(templateValues).reduce((text, [name, value]) => text.replaceAll('{{' + name + '}}', value), html);
}

/** Map a request path to a push-page asset on disk and its content type, or null when it is not one. */
function resolveAsset(urlPath) {
    const base = '/' + FEED_ID + '/push/';
    if (urlPath === base + 'app.js') {
        return { file: path.join(pushDir, 'push.js'), type: 'application/javascript' };
    }
    if (urlPath === base + 'app.css') {
        return { file: path.join(pushDir, 'push.css'), type: 'text/css' };
    }
    if (urlPath.startsWith(base + 'modules/')) {
        const name = urlPath.slice((base + 'modules/').length);
        if (!/^[a-z0-9-]+\.js$/.test(name)) {
            return null;
        }

        return { file: path.join(pushDir, 'modules', name), type: 'application/javascript' };
    }

    return null;
}

async function handle(request, response) {
    const urlPath = new URL(request.url, 'http://fixture').pathname;

    if (urlPath === '/' + FEED_ID + '/push') {
        response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' });
        response.end(await renderPushPage());

        return;
    }

    const asset = resolveAsset(urlPath);
    if (asset) {
        try {
            const content = await readFile(asset.file);
            response.writeHead(200, { 'Content-Type': asset.type, 'Cache-Control': 'no-cache' });
            response.end(content);
        } catch {
            response.writeHead(404);
            response.end();
        }

        return;
    }

    response.writeHead(404, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ error: 'Not served by the push-page fixture' }));
}

/**
 * Start the fixture server on a free loopback port. 127.0.0.1 keeps the page a secure context, as the real deployment's
 * HTTPS origin is.
 * @returns {Promise<{ origin: string, pushUrl: string, close: () => Promise<void> }>}
 */
export async function startFixtureServer() {
    const server = createServer((request, response) => {
        handle(request, response).catch(() => {
            response.writeHead(500);
            response.end();
        });
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const { port } = server.address();
    const origin = 'http://127.0.0.1:' + port;

    return {
        origin,
        pushUrl: origin + '/' + FEED_ID + '/push',
        close: () => new Promise(resolve => server.close(() => resolve())),
    };
}
