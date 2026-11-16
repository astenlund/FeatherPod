import { FEED_ID } from './fixture-server.js';

/**
 * Route-intercepted stand-in for the FeatherPod API the push page calls, recording every request so tests can assert on
 * what the page sent. Only the endpoints the history and editing flows reach have behavior; the event streams answer 204
 * (which closes an EventSource for good), and anything else answers 404 and is kept in `unexpected`.
 */

export const VALID_KEY = 'fp_tester_0123456789abcdef';

const feedApi = '/api/feeds/' + FEED_ID;

/**
 * @typedef {Object} StubEpisode
 * @property {string} id
 * @property {string} title
 * @property {string} fileName
 * @property {string|null} [note]
 */

/**
 * Install the API stub on a page.
 * @param {import('@playwright/test').Page} page
 * @param {Object} options
 * @param {StubEpisode[]} options.episodes - Episodes the recent-uploads endpoints list
 * @param {(episode: StubEpisode, patch: Object) => Object} [options.patchResponse] - Builds a PATCH response body from
 *     the stored episode and the request body; defaults to the stored episode with the patch applied
 * @returns {Promise<{ patches: Array<{ episodeId: string, body: Object }>, keyChecks: string[], unexpected: string[] }>}
 */
export async function installPushApi(page, { episodes, patchResponse }) {
    const store = new Map(episodes.map(e => [e.id, { ...e }]));
    const calls = { patches: [], keyChecks: [], unexpected: [] };

    const json = (route, status, body) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    await page.route(url => new URL(url).pathname.startsWith('/api/'), async route => {
        const request = route.request();
        const { pathname } = new URL(request.url());
        const method = request.method();

        if (pathname === '/api/users/me') {
            const key = request.headers()['x-api-key'] ?? '';
            calls.keyChecks.push(key);

            return key === VALID_KEY
                ? json(route, 200, { id: 'tester', role: 'Admin', ownedFeeds: [] })
                : json(route, 401, { error: 'Invalid API key' });
        }

        if (pathname === feedApi + '/events' || pathname.startsWith('/api/jobs/') && pathname.endsWith('/progress')) {
            return route.fulfill({ status: 204 });
        }

        if (method === 'GET' && pathname === feedApi + '/episodes/recent-uploads') {
            return json(route, 200, [...store.values()]);
        }

        if (method === 'GET' && pathname === feedApi + '/jobs') {
            return json(route, 200, []);
        }

        const suggest = pathname.match(new RegExp('^' + feedApi + '/episodes/([^/]+)/suggest-title$'));
        if (method === 'POST' && suggest) {
            return json(route, 200, { suggestedTitle: 'Suggested title' });
        }

        const episodeMatch = pathname.match(new RegExp('^' + feedApi + '/episodes/([^/]+)$'));
        if (method === 'PATCH' && episodeMatch && store.has(episodeMatch[1])) {
            const episodeId = episodeMatch[1];
            const body = request.postDataJSON();
            calls.patches.push({ episodeId, body });
            const stored = store.get(episodeId);
            const updated = patchResponse ? patchResponse(stored, body) : { ...stored, ...body };
            store.set(episodeId, updated);

            return json(route, 200, updated);
        }

        if (pathname === feedApi + '/push-sessions' || pathname === feedApi + '/push-subscriptions') {
            return route.fulfill({ status: 204 });
        }

        calls.unexpected.push(method + ' ' + pathname);

        return json(route, 404, { error: 'Not stubbed' });
    });

    return calls;
}
