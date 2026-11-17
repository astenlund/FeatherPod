import { test, expect } from '@playwright/test';
import { FEED_ID, startFixtureServer } from './fixture-server.js';
import { VALID_KEY, installPushApi } from './push-api.js';

test('terminal transcript retry preserves the episode id when filenames coincide', async ({ page }) => {
    // Arrange
    const fixture = await startFixtureServer();
    const now = Date.now();
    const original = {
        id: 'ep-1', title: 'Original', fileName: 'episode.mp3', fileSize: 3,
        duration: '00:01:00', publishedDate: new Date(now - 60000).toISOString(),
        uploadedAt: new Date(now - 60000).toISOString(), transcriptStatus: 'Failed'
    };
    const other = { ...original, id: 'ep-2', title: 'Other', fileSize: 6, uploadedAt: new Date(now).toISOString(), transcriptStatus: 'Available' };
    const calls = await installPushApi(page, { episodes: [other, original] });
    const retryTargets = [];
    await page.route('**/transcript/retry', async route => {
        retryTargets.push(new URL(route.request().url()).pathname.split('/').at(-3));
        await route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'Recorded retry target', reason: 'not-retryable' }) });
    });
    await page.addInitScript(({ feedId, key }) => {
        window.localStorage.setItem('featherpod_api_key_local_' + feedId, key);
    }, { feedId: FEED_ID, key: VALID_KEY });
    try {
        await page.goto(fixture.pushUrl);
        await expect(page.locator('#select-file')).toBeEnabled();

        // Act
        const result = await page.evaluate(async ({ feedId, episode }) => {
            const queue = await import('/' + feedId + '/push/modules/queue.js');
            const job = {
                jobId: 'retry-job', status: 'Processing', attempt: 1, kind: 'TranscriptRetry',
                episodeId: episode.id, fileName: episode.fileName, title: episode.title,
                normalizationComplete: true, transcriptionStatus: 'Running', stage: 'Completed',
                queuedAt: new Date().toISOString()
            };
            const entry = queue.createServerJobEntry(job);
            queue.getQueue().push(entry);
            await queue.finalizeTerminalEntry(entry, { ...job, status: 'Completed', transcriptionStatus: 'Failed', transcriptionError: 'Speech failed again' });
            const resolved = { boundId: entry.episodeId, resolvedId: entry.episode?.id, status: entry.status, transcriptionStatus: entry.transcriptionStatus };
            await queue.retryEntryTranscript(entry.id);

            return resolved;
        }, { feedId: FEED_ID, episode: original });

        // Assert
        expect(calls.unexpected).toEqual([]);
        expect(result.status).toBe('completed');
        expect(result.transcriptionStatus).toBe('Failed');
        expect(result.boundId).toBe('ep-1');
        expect({ resolvedId: result.resolvedId, retryTargets }).toEqual({ resolvedId: 'ep-1', retryTargets: ['ep-1'] });
    } finally {
        await fixture.close();
    }
});
