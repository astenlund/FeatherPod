import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const { File, FormData, Request, Response, URL } = globalThis;
const shareUrl = 'https://example.test/my-feed/push';
const source = await readFile(new URL('../../FeatherPod.Server/Pages/Push/push-sw.js', import.meta.url), 'utf8');

/** Run the real service worker fetch handler with multipart data and an in-memory cache. */
async function share(files, text) {
    const handlers = new Map();
    const cachedFiles = [];
    const context = vm.createContext({
        self: { addEventListener: (name, handler) => handlers.set(name, handler) },
        URL,
        Response,
        caches: {
            open: async cacheName => ({
                put: async (key, response) => cachedFiles.push({ cacheName, key, response })
            })
        }
    });
    vm.runInContext(source, context);

    const form = new FormData();
    for (const file of files) {
        form.append('audio', file);
    }
    if (text !== undefined) {
        form.append('shared_text', text);
    }

    let pending;
    handlers.get('fetch')({
        request: new Request(shareUrl, { method: 'POST', body: form }),
        respondWith: response => { pending = response; }
    });
    assert.ok(pending, 'The service worker must intercept the share POST');

    return { response: await pending, cachedFiles };
}

for (const text of [undefined, 'episode.mp3', 'https://youtu.be/dQw4w9WgXcQ']) {
    test(`caches shared audio and redirects to the queue with accompanying text: ${text}`, async () => {
        const file = new File(['audio bytes'], 'episode.mp3', { type: 'audio/mpeg' });

        const { response, cachedFiles } = await share([file], text);

        assert.equal(response.status, 303);
        assert.equal(response.headers.get('location'), shareUrl);
        assert.equal(cachedFiles.length, 1);
        assert.equal(cachedFiles[0].cacheName, 'share-target');
        assert.ok(cachedFiles[0].key.endsWith('-episode.mp3'));
        assert.equal(cachedFiles[0].response.headers.get('content-type'), file.type);
        assert.equal(await cachedFiles[0].response.text(), await file.text());
    });
}

test('preserves every audio attachment when multiple files accompany shared text', async () => {
    const files = ['first.mp3', 'second.mp3'].map(name => new File([name], name, { type: 'audio/mpeg' }));

    const { response, cachedFiles } = await share(files, 'Two episodes');

    assert.equal(response.headers.get('location'), shareUrl);
    assert.equal(cachedFiles.length, files.length);
    for (const [index, cached] of cachedFiles.entries()) {
        assert.ok(cached.key.endsWith('-' + files[index].name));
        assert.equal(await cached.response.text(), await files[index].text());
    }
});

test('continues routing text-only shares to the YouTube import flow', async () => {
    const text = 'https://youtu.be/dQw4w9WgXcQ';

    const { response, cachedFiles } = await share([], text);

    assert.equal(response.status, 303);
    const redirect = new URL(response.headers.get('location'));
    assert.equal(redirect.searchParams.get('yt'), text);
    redirect.searchParams.delete('yt');
    assert.equal(redirect.href, shareUrl);
    assert.equal(cachedFiles.length, 0);
});

test('opens the push page normally for an empty share', async () => {
    const { response, cachedFiles } = await share([]);

    assert.equal(response.status, 303);
    assert.equal(response.headers.get('location'), shareUrl);
    assert.equal(cachedFiles.length, 0);
});
