import { Buffer } from 'node:buffer';
import { test as base, expect } from '@playwright/test';
import { FEED_ID, startFixtureServer } from './fixture-server.js';
import { VALID_KEY } from './push-api.js';

/**
 * Normalization and transcript retry through the real push.js startup. The API is a small stateful stand-in installed
 * on the browser context by route interception, so every tab of a test shares one server: its jobs change when a test
 * retries, cancels or advances them, and every request the pages send is recorded. Job progress streams answer 204 by
 * default (which closes an EventSource for good), so the page monitors jobs by polling GET /api/jobs/{jobId}, unless a
 * test scripts the stream.
 */

const API_KEY_LOCAL_KEY = 'featherpod_api_key_local_' + FEED_ID;
const QUEUE_STORAGE_KEY = 'featherpod_queue_' + FEED_ID;
const feedApi = '/api/feeds/' + FEED_ID;
const MINUTE = 60 * 1000;
/** Long enough for several poll rounds (the page polls a job every 2 s). */
const POLL_TIMEOUT = 15_000;

const test = base.extend({
    pushServer: [async ({}, use) => {
        const server = await startFixtureServer();
        await use(server);
        await server.close();
    }, { scope: 'worker' }],
});

/** A job as the server serializes it (JobStatusResponse), on its first attempt and failed in normalization. */
function failedJob(overrides = {}) {
    return {
        jobId: 'job-1',
        status: 'Failed',
        attempt: 1,
        kind: null,
        stage: 'Failed',
        progressPercent: null,
        fileName: 'episode.mp3',
        title: 'Episode',
        error: 'Normalization failed: ffmpeg exited with code 1',
        normalizationError: 'ffmpeg exited with code 1',
        normalizationComplete: true,
        transcriptionStatus: null,
        transcriptionError: null,
        episodeId: null,
        queuedAt: new Date(Date.now() - 10 * MINUTE).toISOString(),
        completedAt: new Date(Date.now() - 9 * MINUTE).toISOString(),
        ...overrides,
    };
}

/** The failed queue entry for failedJob, saved the way saveQueueState writes it, so startup restores it without a file. */
function failedQueueEntry(overrides = {}) {
    return {
        id: 'q-1',
        fileName: 'episode.mp3',
        title: 'Episode',
        fileSize: 4096,
        status: 'failed',
        progress: 0,
        stage: 'Failed',
        jobId: 'job-1',
        attempt: 1,
        kind: null,
        normalizationFailed: true,
        clientUploadId: null,
        episodeId: null,
        episode: null,
        error: 'Normalization failed: ffmpeg exited with code 1',
        localSourceIndex: null,
        validationError: false,
        startedAt: Date.now() - 10 * MINUTE,
        normalizationComplete: true,
        transcriptionStatus: null,
        transcriptionError: null,
        ...overrides,
    };
}

/** An episode as the recent-uploads endpoints serialize it. */
function episode(overrides = {}) {
    return {
        id: 'ep-1',
        title: 'Episode',
        fileName: 'episode.mp3',
        note: null,
        duration: '00:10:00',
        fileSize: 1024 * 1024,
        publishedDate: '2026-11-01T08:00:00Z',
        uploadedAt: new Date().toISOString(),
        transcriptStatus: 'Failed',
        ...overrides,
    };
}

/** The 202 body of a transcript retry: a new transcription-only job bound to the episode. */
function transcriptRetryJob(overrides = {}) {
    return {
        jobId: 'job-t1',
        status: 'Processing',
        attempt: 1,
        kind: 'TranscriptRetry',
        stage: 'Completed',
        progressPercent: null,
        fileName: 'episode.mp3',
        title: 'Episode',
        error: null,
        normalizationError: null,
        normalizationComplete: true,
        transcriptionStatus: 'Queued',
        transcriptionError: null,
        episodeId: 'ep-1',
        queuedAt: new Date().toISOString(),
        completedAt: null,
        ...overrides,
    };
}

/**
 * Install the stateful API stand-in on a browser context.
 * @param {import('@playwright/test').BrowserContext} context
 * @param {Object} options
 * @param {Object[]} [options.jobs] - The server's jobs
 * @param {Object[]} [options.episodes] - Episodes the recent-uploads endpoints list
 */
async function installRetryApi(context, { jobs = [], episodes = [] } = {}) {
    const server = {
        jobs: new Map(jobs.map(j => [j.jobId, { ...j }])),
        episodes,
        calls: { retries: [], retryAttempts: [], transcriptRetries: [], cancels: [], uploads: [], listings: 0, progress: [], unexpected: [] },
        /**
         * Answers POST /api/jobs/{jobId}/retry; by default, like the server, refuses a retry naming an attempt the job has moved
         * past (409) and resets a normalization-failed job to its next attempt
         */
        retryResponse: (jobId, attempt) => {
            const job = server.jobs.get(jobId);
            if (job && attempt !== null && Number(attempt) !== job.attempt) {
                return { status: 409, body: { error: 'The job changed while it was being retried', reason: 'conflict' } };
            }
            if (!job || job.status !== 'Failed' || !job.normalizationError) {
                return { status: 409, body: { error: 'Nothing to retry', reason: 'not-retryable' } };
            }
            const reset = { ...job, status: 'Queued', attempt: job.attempt + 1, stage: 'Queued', progressPercent: null, error: null, normalizationError: null, normalizationComplete: false, queuedAt: new Date().toISOString(), completedAt: null };
            server.jobs.set(jobId, reset);

            return { status: 202, body: reset };
        },
        /** Answers POST .../episodes/{episodeId}/transcript/retry; by default starts transcriptRetryJob */
        transcriptRetryResponse: () => {
            const job = transcriptRetryJob();
            server.jobs.set(job.jobId, job);

            return { status: 202, body: job };
        },
        /** The recent-jobs listing; by default every job the server holds */
        listing: () => [...server.jobs.values()],
        /** An event-stream body for a job's progress stream, or null to answer 204 */
        progressBody: () => null,
        /** Change a job on the server */
        update(jobId, changes) {
            server.jobs.set(jobId, { ...server.jobs.get(jobId), ...changes });
        },
    };

    const json = (route, status, body) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    await context.route(url => new URL(url).pathname.startsWith('/api/'), async route => {
        const request = route.request();
        const url = new URL(request.url());
        const { pathname } = url;
        const method = request.method();

        if (pathname === '/api/users/me') {
            return json(route, 200, { id: 'tester', role: 'Admin', ownedFeeds: [] });
        }
        if (pathname === feedApi + '/events') {
            return route.fulfill({ status: 204 });
        }
        const progress = pathname.match(/^\/api\/jobs\/([^/]+)\/progress$/);
        if (progress) {
            server.calls.progress.push(progress[1]);
            const body = server.progressBody(progress[1]);

            return body === null ? route.fulfill({ status: 204 }) : route.fulfill({ status: 200, contentType: 'text/event-stream', body });
        }
        if (method === 'GET' && pathname === feedApi + '/jobs') {
            server.calls.listings++;

            return json(route, 200, server.listing());
        }
        if (method === 'GET' && pathname === feedApi + '/episodes/recent-uploads') {
            return json(route, 200, server.episodes);
        }
        const retry = pathname.match(/^\/api\/jobs\/([^/]+)\/retry$/);
        if (method === 'POST' && retry) {
            server.calls.retries.push(retry[1]);
            server.calls.retryAttempts.push(new URL(route.request().url()).searchParams.get('attempt'));
            const { status, body } = server.retryResponse(retry[1], url.searchParams.get('attempt'));

            return json(route, status, body);
        }
        const cancel = pathname.match(/^\/api\/jobs\/([^/]+)\/cancel$/);
        if (method === 'POST' && cancel) {
            const attempt = url.searchParams.get('attempt');
            server.calls.cancels.push({ jobId: cancel[1], attempt });
            const job = server.jobs.get(cancel[1]);
            if (!job) {
                return json(route, 404, { error: 'Job not found' });
            }
            if (attempt !== null && Number(attempt) !== job.attempt) {
                return json(route, 409, { error: 'Job cannot be cancelled in its current state' });
            }
            server.update(cancel[1], { status: 'Cancelled' });

            return json(route, 200, server.jobs.get(cancel[1]));
        }
        const job = pathname.match(/^\/api\/jobs\/([^/]+)$/);
        if (method === 'GET' && job) {
            return server.jobs.has(job[1]) ? json(route, 200, server.jobs.get(job[1])) : json(route, 404, { error: 'Job not found' });
        }
        const transcriptRetry = pathname.match(new RegExp('^' + feedApi + '/episodes/([^/]+)/transcript/retry$'));
        if (method === 'POST' && transcriptRetry) {
            server.calls.transcriptRetries.push(transcriptRetry[1]);
            const { status, body } = server.transcriptRetryResponse(transcriptRetry[1]);

            return json(route, status, body);
        }
        if (method === 'POST' && pathname === feedApi + '/episodes') {
            const jobId = 'job-upload-' + (server.calls.uploads.length + 1);
            server.calls.uploads.push(jobId);
            const uploaded = failedJob({ jobId, status: 'Queued', stage: 'Queued', error: null, normalizationError: null, normalizationComplete: false, queuedAt: new Date().toISOString(), completedAt: null });
            server.jobs.set(jobId, uploaded);

            return json(route, 202, uploaded);
        }
        if (pathname === feedApi + '/push-sessions' || pathname === feedApi + '/push-subscriptions') {
            return route.fulfill({ status: 204 });
        }

        server.calls.unexpected.push(method + ' ' + pathname);

        return json(route, 404, { error: 'Not stubbed' });
    });

    return server;
}

/**
 * Seed the key, and optionally a saved queue, before the first page of the context runs its scripts. Later pages and
 * reloads find the storage as the earlier pages left it.
 * @param {import('@playwright/test').BrowserContext} context
 * @param {{ queue?: Object[] }} [seed]
 */
async function seedStorage(context, { queue } = {}) {
    await context.addInitScript(([keyName, keyValue, queueName, queueValue]) => {
        if (window.localStorage.getItem('test-seeded')) {
            return;
        }
        window.localStorage.setItem('test-seeded', '1');
        window.localStorage.setItem(keyName, keyValue);
        if (queueValue) {
            window.localStorage.setItem(queueName, queueValue);
        }
    }, [API_KEY_LOCAL_KEY, VALID_KEY, QUEUE_STORAGE_KEY, queue ? JSON.stringify(queue) : null]);
}

/** Record, from now on, whether a queue item of the page shows as failed again once one has shown as normalizing. */
async function watchForFailedItems(page) {
    await page.evaluate(() => {
        window.sawFailedQueueItem = false;
        let sawNormalizing = false;
        const check = () => {
            sawNormalizing ||= !!window.document.querySelector('#queue-list .queue-item--normalizing');
            if (sawNormalizing && window.document.querySelector('#queue-list .queue-item--failed')) {
                window.sawFailedQueueItem = true;
            }
        };
        new window.MutationObserver(check).observe(window.document.getElementById('queue-list'), { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
    });
}

/** Trigger the page's recent-jobs merge the way tab reactivation does. */
async function mergeRecentJobs(page) {
    await page.evaluate(() => window.document.dispatchEvent(new window.Event('visibilitychange')));
}

const queueItems = page => page.locator('#queue-list .queue-item');
const retryButton = item => item.locator('.queue-item-action--retry');

test('Retry on a restored failed entry without its file retries the job on the server, and the entry follows the new attempt', async ({ pushServer, context, page }) => {
    // Arrange
    const api = await installRetryApi(context, { jobs: [failedJob()] });
    await seedStorage(context, { queue: [failedQueueEntry()] });
    await page.goto(pushServer.pushUrl);
    const item = queueItems(page).first();
    await expect(item).toHaveClass(/queue-item--failed/);

    // Act
    await retryButton(item).click();

    // Assert: the retry names the attempt the entry showed, so a click on a view the job has moved past is refused
    await expect(queueItems(page)).toHaveCount(1);
    await expect(queueItems(page).first()).toHaveClass(/queue-item--normalizing/);
    expect(api.calls.retries).toEqual(['job-1']);
    expect(api.calls.retryAttempts).toEqual(['1']);
    api.update('job-1', { status: 'Completed', stage: 'Completed', episodeId: 'ep-1', completedAt: new Date().toISOString() });
    await expect(queueItems(page).first()).toHaveClass(/queue-item--completed/, { timeout: POLL_TIMEOUT });
    expect(api.calls.uploads).toEqual([]);
    const saved = await page.evaluate(key => JSON.parse(window.localStorage.getItem(key)), QUEUE_STORAGE_KEY);
    expect(saved[0].attempt).toBe(2);
    expect(api.calls.unexpected).toEqual([]);
});

test('a failed entry saved before normalization retry existed gains Retry once the server reports its normalization failure', async ({ pushServer, context, page }) => {
    // Arrange: the saved entry predates the normalizationFailed field
    const api = await installRetryApi(context, { jobs: [failedJob()] });
    await seedStorage(context, { queue: [failedQueueEntry({ normalizationFailed: undefined })] });
    await page.goto(pushServer.pushUrl);
    const item = queueItems(page).first();
    await expect(item).toHaveClass(/queue-item--failed/);

    // Act
    await retryButton(item).click();

    // Assert
    expect(api.calls.retries).toEqual(['job-1']);
    expect(api.calls.unexpected).toEqual([]);
});

test('Retry uploads the held file again only when the server reports the source gone', async ({ pushServer, context, page }) => {
    // Arrange
    const api = await installRetryApi(context);
    api.retryResponse = () => ({ status: 410, body: { error: 'The uploaded source is gone; upload the file again', reason: 'source-gone' } });
    await seedStorage(context);
    await page.goto(pushServer.pushUrl);
    await page.locator('#file-input').setInputFiles({ name: 'episode.mp3', mimeType: 'audio/mpeg', buffer: Buffer.alloc(4096) });
    await expect.poll(() => api.calls.uploads.length).toBe(1);
    api.update('job-upload-1', { status: 'Failed', stage: 'Failed', error: 'Normalization failed', normalizationError: 'ffmpeg exited with code 1' });
    const item = queueItems(page).first();
    await expect(item).toHaveClass(/queue-item--failed/, { timeout: POLL_TIMEOUT });

    // Act
    await retryButton(item).click();

    // Assert
    await expect.poll(() => api.calls.uploads.length).toBe(2);
    expect(api.calls.retries).toEqual(['job-upload-1']);
    await expect(queueItems(page)).toHaveCount(1);
    await expect(queueItems(page).first()).toHaveClass(/queue-item--normalizing/);
    expect(api.calls.unexpected).toEqual([]);
});

test('a refused normalization retry shows the server error on the entry, which stays retryable on the attempt the server moved to', async ({ pushServer, context, page }) => {
    // Arrange: as the real server does on a failed send, the refused retry leaves the job Failed on its next attempt
    const api = await installRetryApi(context, { jobs: [failedJob()] });
    const defaultRetry = api.retryResponse;
    api.retryResponse = jobId => {
        api.update(jobId, { attempt: 2, error: 'Retry not started: the queue was unreachable', normalizationError: 'Retry not started: the queue was unreachable' });
        api.retryResponse = defaultRetry;

        return { status: 503, body: { error: 'The retry could not be queued; try again', reason: 'queue-failed' } };
    };
    await seedStorage(context, { queue: [failedQueueEntry()] });
    await page.goto(pushServer.pushUrl);
    const item = queueItems(page).first();
    await expect(item).toHaveClass(/queue-item--failed/);

    // Act
    await retryButton(item).click();
    await expect(queueItems(page).first().locator('.queue-item-status')).toHaveText('The retry could not be queued; try again');
    await expect(queueItems(page).first()).toHaveClass(/queue-item--failed/);
    await expect(retryButton(queueItems(page).first())).toBeEnabled();
    await expect.poll(() => page.evaluate(key => JSON.parse(window.localStorage.getItem(key))[0].attempt, QUEUE_STORAGE_KEY)).toBe(2);
    await retryButton(queueItems(page).first()).click();

    // Assert: the second retry names the attempt the server moved to, and is accepted
    await expect(queueItems(page).first()).toHaveClass(/queue-item--normalizing/);
    expect(api.calls.retryAttempts).toEqual(['1', '2']);
    expect(api.calls.uploads).toEqual([]);
});

test('a refused normalization retry whose job is running a later attempt makes the entry follow that attempt', async ({ pushServer, context, page }) => {
    // Arrange: the send reported failure, yet the job is already running its next attempt (or another client retried it)
    const api = await installRetryApi(context, { jobs: [failedJob()] });
    api.retryResponse = jobId => {
        api.update(jobId, { attempt: 2, status: 'Processing', stage: 'Normalizing', error: null, normalizationError: null, normalizationComplete: false, completedAt: null });

        return { status: 503, body: { error: 'The retry could not be queued; try again', reason: 'queue-failed' } };
    };
    const pageErrors = [];
    page.on('pageerror', e => pageErrors.push(e.message));
    await seedStorage(context, { queue: [failedQueueEntry()] });
    await page.goto(pushServer.pushUrl);
    const item = queueItems(page).first();
    await expect(item).toHaveClass(/queue-item--failed/);

    // Act
    await retryButton(item).click();

    // Assert
    await expect(queueItems(page).first()).toHaveClass(/queue-item--normalizing/);
    await expect.poll(() => page.evaluate(key => JSON.parse(window.localStorage.getItem(key))[0].attempt, QUEUE_STORAGE_KEY)).toBe(2);
    expect(pageErrors).toEqual([]);
});

test('a snapshot and a stream status from before the reset leave the retried entry following its new attempt', async ({ pushServer, context, page }) => {
    // Arrange -- the stream and the listing keep reporting the first attempt as failed after the retry
    const preReset = failedJob();
    const api = await installRetryApi(context, { jobs: [preReset] });
    await seedStorage(context, { queue: [failedQueueEntry()] });
    await page.goto(pushServer.pushUrl);
    const item = queueItems(page).first();
    await expect(item).toHaveClass(/queue-item--failed/);
    await watchForFailedItems(page);
    api.progressBody = () => 'event: progress\ndata: ' + JSON.stringify(preReset) + '\n\nevent: done\ndata: {}\n\n';
    api.listing = () => [preReset];

    // Act
    await retryButton(item).click();
    await expect.poll(() => api.calls.progress.length).toBeGreaterThan(0);
    const listingsBefore = api.calls.listings;
    await mergeRecentJobs(page);
    await expect.poll(() => api.calls.listings).toBeGreaterThan(listingsBefore);
    api.listing = () => [...api.jobs.values()];
    api.update('job-1', { status: 'Processing', stage: 'Normalizing', progressPercent: 40 });

    // Assert
    await expect(queueItems(page).first().locator('.queue-item-status')).toHaveText('Normalizing', { timeout: POLL_TIMEOUT });
    api.update('job-1', { status: 'Completed', stage: 'Completed', episodeId: 'ep-1', completedAt: new Date().toISOString() });
    await expect(queueItems(page).first()).toHaveClass(/queue-item--completed/, { timeout: POLL_TIMEOUT });
    expect(await page.evaluate(() => window.sawFailedQueueItem)).toBe(false);
    await expect(queueItems(page)).toHaveCount(1);
});

test('a retry running past the original job\'s first hour stays in the queue after a reload', async ({ pushServer, context, page }) => {
    // Arrange -- the device clock is pinned, so the reload can happen 20 minutes later
    const t0 = Date.now();
    await page.clock.setFixedTime(t0);
    const api = await installRetryApi(context, { jobs: [failedJob({ queuedAt: new Date(t0 - 50 * MINUTE).toISOString() })] });
    await seedStorage(context, { queue: [failedQueueEntry({ startedAt: t0 - 50 * MINUTE })] });
    await page.goto(pushServer.pushUrl);
    await retryButton(queueItems(page).first()).click();
    await expect(queueItems(page).first()).toHaveClass(/queue-item--normalizing/);
    api.update('job-1', { status: 'Processing', stage: 'Normalizing', progressPercent: 50 });

    // Act
    await page.clock.setFixedTime(t0 + 20 * MINUTE);
    await page.reload();

    // Assert -- the restored entry itself, not one a recent-jobs merge built anew for the job
    await expect(queueItems(page)).toHaveCount(1);
    await expect(page.locator('#queue-item-q-1')).toHaveClass(/queue-item--normalizing/);
    const saved = await page.evaluate(key => JSON.parse(window.localStorage.getItem(key)), QUEUE_STORAGE_KEY);
    expect(saved[0].startedAt).toBeGreaterThanOrEqual(t0);
});

test('a stale tab dismissing the failed entry leaves the retry running, and the browser\'s tabs keep showing the retried job', async ({ pushServer, context }) => {
    // Arrange -- two tabs show the failed first attempt; the retrying tab retries it, the stale tab never merges in between
    const api = await installRetryApi(context, { jobs: [failedJob()] });
    await seedStorage(context, { queue: [failedQueueEntry()] });
    const staleTab = await context.newPage();
    await staleTab.goto(pushServer.pushUrl);
    await expect(queueItems(staleTab).first()).toHaveClass(/queue-item--failed/);
    const retryingTab = await context.newPage();
    await retryingTab.goto(pushServer.pushUrl);
    await retryButton(queueItems(retryingTab).first()).click();
    await expect(queueItems(retryingTab).first()).toHaveClass(/queue-item--normalizing/);
    api.update('job-1', { status: 'Processing', stage: 'Normalizing', progressPercent: 20 });

    // Act
    await queueItems(staleTab).first().locator('.queue-item-action--cancel').click();
    await expect(queueItems(staleTab)).toHaveCount(0);
    await expect.poll(() => api.calls.cancels.length).toBe(1);
    await retryingTab.reload();
    const newTab = await context.newPage();
    await newTab.goto(pushServer.pushUrl);
    await mergeRecentJobs(staleTab);

    // Assert
    expect(api.calls.cancels).toEqual([{ jobId: 'job-1', attempt: '1' }]);
    expect(api.jobs.get('job-1').status).toBe('Processing');
    for (const tab of [retryingTab, newTab, staleTab]) {
        await expect(queueItems(tab)).toHaveCount(1);
        await expect(queueItems(tab).first()).toHaveClass(/queue-item--normalizing/);
    }
    expect(api.calls.unexpected).toEqual([]);
});

test('Retry transcript on an entry completed with a failed transcript adds an entry for the retry, which can be retried again', async ({ pushServer, context, page }) => {
    // Arrange
    const completed = failedJob({ status: 'Completed', stage: 'Completed', error: null, normalizationError: null, episodeId: 'ep-1', transcriptionStatus: 'Failed', transcriptionError: 'Speech failed' });
    const api = await installRetryApi(context, { jobs: [completed], episodes: [episode()] });
    await seedStorage(context, {
        queue: [failedQueueEntry({ status: 'completed', stage: 'Completed', progress: 100, error: null, normalizationFailed: false, episodeId: 'ep-1', episode: { id: 'ep-1', title: 'Episode', fileName: 'episode.mp3' }, transcriptionStatus: 'Failed', transcriptionError: 'Speech failed' })]
    });
    await page.goto(pushServer.pushUrl);
    const original = page.locator('#queue-item-q-1');
    await expect(retryButton(original)).toHaveText('Retry transcript');

    // Act
    await retryButton(original).click();

    // Assert
    await expect(queueItems(page)).toHaveCount(2);
    expect(api.calls.transcriptRetries).toEqual(['ep-1']);
    const retryEntry = queueItems(page).first();
    await expect(retryEntry.locator('.queue-item-status')).toHaveText('Transcribing');
    await expect(retryEntry.locator('.queue-item-progress')).toHaveClass(/indeterminate/);
    await expect(retryButton(original)).toHaveCount(0);
    api.update('job-t1', { status: 'Completed', transcriptionStatus: 'Failed', transcriptionError: 'Speech failed again', completedAt: new Date().toISOString() });
    await expect(retryEntry).toHaveClass(/queue-item--completed/, { timeout: POLL_TIMEOUT });
    await expect(retryButton(retryEntry)).toHaveText('Retry transcript');
    await expect(retryButton(original)).toHaveText('Retry transcript');
    expect(api.calls.unexpected).toEqual([]);
});

test('after a transcript retry succeeds, the entry it was started from no longer offers Retry transcript', async ({ pushServer, context, page }) => {
    // Arrange
    const completed = failedJob({ status: 'Completed', stage: 'Completed', error: null, normalizationError: null, episodeId: 'ep-1', transcriptionStatus: 'Failed', transcriptionError: 'Speech failed' });
    const api = await installRetryApi(context, { jobs: [completed], episodes: [episode()] });
    await seedStorage(context, {
        queue: [failedQueueEntry({ status: 'completed', stage: 'Completed', progress: 100, error: null, normalizationFailed: false, episodeId: 'ep-1', episode: { id: 'ep-1', title: 'Episode', fileName: 'episode.mp3' }, transcriptionStatus: 'Failed', transcriptionError: 'Speech failed' })]
    });
    await page.goto(pushServer.pushUrl);
    await retryButton(page.locator('#queue-item-q-1')).click();
    await expect(queueItems(page)).toHaveCount(2);
    api.update('job-t1', { status: 'Completed', transcriptionStatus: 'Completed', transcriptionError: null, completedAt: new Date().toISOString() });
    await expect(queueItems(page).first()).toHaveClass(/queue-item--completed/, { timeout: POLL_TIMEOUT });

    // Act: reload, so both entries render from the saved queue in one pass
    await page.reload();

    // Assert
    const retryEntry = queueItems(page).filter({ hasNot: page.locator('#queue-item-q-1') }).first();
    await expect(queueItems(page)).toHaveCount(2);
    await expect(retryEntry).toHaveClass(/queue-item--completed/);
    await expect(retryButton(retryEntry)).toHaveCount(0);
    await expect(page.locator('#queue-item-q-1')).toHaveClass(/queue-item--completed/);
    await expect(retryButton(page.locator('#queue-item-q-1'))).toHaveCount(0);
    expect(api.calls.unexpected).toEqual([]);
});

test('the history context menu offers Retry transcript for a failed transcript only, and it adds a queue entry', async ({ pushServer, context, page }) => {
    // Arrange
    const api = await installRetryApi(context, { episodes: [episode(), episode({ id: 'ep-2', title: 'Second', fileName: 'second.mp3', transcriptStatus: 'Available' })] });
    await seedStorage(context);
    await page.goto(pushServer.pushUrl);
    const toggle = page.locator('#history-toggle');
    await expect(toggle).toBeVisible();
    if (await toggle.getAttribute('aria-expanded') !== 'true') {
        await toggle.click();
    }
    await page.locator('#filter-browser').click();
    const menuItem = page.locator('#context-menu .context-menu-item[data-action="retry-transcript"]');
    await page.locator('#history-list .upload-item[data-id="ep-2"]').click({ button: 'right' });
    await expect(page.locator('#context-menu')).toBeVisible();
    await expect(menuItem).toBeHidden();
    await page.keyboard.press('Escape');
    await page.locator('#history-list .upload-item[data-id="ep-1"]').click({ button: 'right' });

    // Act
    await expect(menuItem).toBeVisible();
    await menuItem.click();

    // Assert
    await expect(queueItems(page)).toHaveCount(1);
    await expect(queueItems(page).first().locator('.queue-item-status')).toHaveText('Transcribing');
    expect(api.calls.transcriptRetries).toEqual(['ep-1']);
    expect(api.calls.unexpected).toEqual([]);
});

test('a refused transcript retry is shown to the user and adds no entry', async ({ pushServer, context, page }) => {
    // Arrange
    const api = await installRetryApi(context, { episodes: [episode()] });
    api.transcriptRetryResponse = () => ({ status: 409, body: { error: 'A transcript retry of this episode is already running', reason: 'retry-active' } });
    await seedStorage(context);
    await page.goto(pushServer.pushUrl);
    const toggle = page.locator('#history-toggle');
    await expect(toggle).toBeVisible();
    if (await toggle.getAttribute('aria-expanded') !== 'true') {
        await toggle.click();
    }
    await page.locator('#filter-browser').click();
    await page.locator('#history-list .upload-item[data-id="ep-1"]').click({ button: 'right' });

    // Act
    await page.locator('#context-menu .context-menu-item[data-action="retry-transcript"]').click();

    // Assert
    await expect(page.locator('.notif-hint.transcript-retry-error')).toContainText('A transcript retry of this episode is already running');
    await expect(queueItems(page)).toHaveCount(0);
    expect(api.calls.transcriptRetries).toEqual(['ep-1']);
});
