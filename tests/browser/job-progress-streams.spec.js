import { Buffer } from 'node:buffer';
import { test, expect } from '@playwright/test';
import { FEED_ID, startFixtureServer } from './fixture-server.js';
import { VALID_KEY } from './push-api.js';

/**
 * The cap on job progress streams (MAX_JOB_PROGRESS_STREAMS in queue.js), checked over real connections. The fixture
 * server speaks plain HTTP/1.1, where Chromium opens at most six connections per host, and it serves the requests that
 * hold connections (the feed events stream, every job progress stream and the upload) for real; only short API calls
 * are answered by route interception, which opens no connection. Without the cap, five monitored jobs plus the feed
 * stream fill the pool and a new upload never reaches the server.
 */

const API_KEY_LOCAL_KEY = 'featherpod_api_key_local_' + FEED_ID;
const STREAM_CAP = 3;
const RESTORED_JOB_IDS = ['job-a', 'job-b', 'job-c', 'job-d', 'job-e'];
const UPLOADED_JOB_ID = 'job-new';

/** A job as GET /api/feeds/{feedId}/jobs and GET /api/jobs/{jobId} serialize it, still normalizing. */
function processingJob(jobId) {
    return {
        jobId,
        status: 'Processing',
        stage: 'Normalizing',
        progressPercent: 10,
        fileName: jobId + '.mp3',
        title: null,
        queuedAt: new Date().toISOString(),
        normalizationComplete: false,
        transcriptionStatus: null,
    };
}

/**
 * The real-network half of the API: event streams that stay open until the test ends them, and the upload endpoint.
 * Records which progress streams are open, the most open at once, and the uploads that arrived.
 */
function createStreamingApi() {
    const openProgress = new Map();
    const state = { maxOpenProgress: 0, progressRequests: [], uploads: 0 };

    function writeEvent(response, event, data) {
        response.write('event: ' + event + '\ndata: ' + JSON.stringify(data) + '\n\n');
    }

    async function handleApi(request, response, urlPath) {
        if (urlPath === '/api/feeds/' + FEED_ID + '/events') {
            response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
            response.write(': open\n\n');

            return;
        }

        const progress = urlPath.match(/^\/api\/jobs\/([^/]+)\/progress$/);
        if (progress) {
            const jobId = progress[1];
            state.progressRequests.push(jobId);
            openProgress.set(jobId, response);
            state.maxOpenProgress = Math.max(state.maxOpenProgress, openProgress.size);
            request.on('close', () => {
                if (openProgress.get(jobId) === response) {
                    openProgress.delete(jobId);
                }
            });
            response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
            writeEvent(response, 'progress', processingJob(jobId));

            return;
        }

        if (request.method === 'POST' && urlPath === '/api/feeds/' + FEED_ID + '/episodes') {
            for await (const chunk of request) {
                void chunk;
            }
            state.uploads++;
            response.writeHead(202, { 'Content-Type': 'application/json' });
            response.end(JSON.stringify({ jobId: UPLOADED_JOB_ID, title: null }));

            return;
        }

        response.writeHead(404, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ error: 'Not served over the network in this test' }));
    }

    return {
        handleApi,
        state,
        openProgressJobIds: () => [...openProgress.keys()],
        /** Report a job cancelled on its open stream, which ends that job's monitoring and closes the stream. */
        cancelOverStream(jobId) {
            writeEvent(openProgress.get(jobId), 'progress', { ...processingJob(jobId), status: 'Cancelled' });
        },
    };
}

/** The short API calls, answered in the browser by route interception; the rest go to the fixture server. */
async function installInterceptedApi(page) {
    const json = (route, status, body) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    const isNetworkPath = pathname => pathname === '/api/feeds/' + FEED_ID + '/events' || /^\/api\/jobs\/[^/]+\/progress$/.test(pathname) || pathname === '/api/feeds/' + FEED_ID + '/episodes';

    await page.route(url => {
        const { pathname } = new URL(url);

        return pathname.startsWith('/api/') && !isNetworkPath(pathname);
    }, async route => {
        const { pathname } = new URL(route.request().url());

        if (pathname === '/api/users/me') {
            return json(route, 200, { id: 'tester', role: 'Admin', ownedFeeds: [] });
        }
        if (pathname === '/api/feeds/' + FEED_ID + '/jobs') {
            return json(route, 200, RESTORED_JOB_IDS.map(processingJob));
        }
        const job = pathname.match(/^\/api\/jobs\/([^/]+)$/);
        if (job) {
            return json(route, 200, processingJob(job[1]));
        }
        if (pathname === '/api/feeds/' + FEED_ID + '/episodes/recent-uploads') {
            return json(route, 200, []);
        }

        return route.fulfill({ status: 204 });
    });
}

test('job progress streams stay under the cap, a new upload still reaches the server, and a freed slot is reused', async ({ page }) => {
    // Arrange
    const api = createStreamingApi();
    const server = await startFixtureServer({ handleApi: api.handleApi });
    try {
        await page.addInitScript(([keyName, keyValue]) => window.localStorage.setItem(keyName, keyValue), [API_KEY_LOCAL_KEY, VALID_KEY]);
        await installInterceptedApi(page);
        await page.goto(server.pushUrl);
        await expect(page.locator('#queue .queue-item')).toHaveCount(RESTORED_JOB_IDS.length);
        await expect.poll(() => api.openProgressJobIds().length).toBe(STREAM_CAP);

        // Act
        await page.locator('#queue-file-input').setInputFiles({ name: 'new.mp3', mimeType: 'audio/mpeg', buffer: Buffer.alloc(4096) });
        await expect.poll(() => api.state.uploads, { timeout: 10_000 }).toBe(1);
        await expect.poll(() => api.state.progressRequests.length).toBeGreaterThanOrEqual(STREAM_CAP);
        const deferredJobIds = [...RESTORED_JOB_IDS, UPLOADED_JOB_ID].filter(id => !api.state.progressRequests.includes(id));
        const cancelledJobId = api.openProgressJobIds()[0];
        api.cancelOverStream(cancelledJobId);

        // Assert
        await expect.poll(() => api.state.progressRequests.some(id => deferredJobIds.includes(id)), { timeout: 10_000 }).toBe(true);
        await expect.poll(() => api.openProgressJobIds().includes(cancelledJobId)).toBe(false);
        expect(api.state.maxOpenProgress).toBe(STREAM_CAP);
        expect(api.openProgressJobIds().length).toBeLessThanOrEqual(STREAM_CAP);
    } finally {
        await page.close();
        await server.close();
    }
});
