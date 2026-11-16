import { test as base, expect } from '@playwright/test';
import { FEED_ID, renderPushPage, startFixtureServer } from './fixture-server.js';
import { VALID_KEY, installPushApi } from './push-api.js';

/**
 * History context menu and episode editing through the real push.js startup: the page is served from disk by the
 * fixture server, the API is stubbed by route interception, and every interaction goes through the page's own wiring
 * (registerHistoryContextMenuCallback and registerEpisodeRenamedCallback in push.js), not callbacks registered by a test.
 */

const API_KEY_LOCAL_KEY = 'featherpod_api_key_local_' + FEED_ID;
const QUEUE_STORAGE_KEY = 'featherpod_queue_' + FEED_ID;

/** How long history.js waits before a held touch opens the context menu. */
const LONG_PRESS_MS = 500;

const test = base.extend({
    pushServer: [async ({}, use) => {
        const server = await startFixtureServer();
        await use(server);
        await server.close();
    }, { scope: 'worker' }],
});

/** An episode as the recent-uploads endpoints serialize it (Duration is a TimeSpan string, dates are ISO strings). */
function episode(overrides = {}) {
    return {
        id: 'ep-1',
        title: 'Original title',
        fileName: 'episode.mp3',
        note: null,
        duration: '00:10:00',
        fileSize: 1024 * 1024,
        publishedDate: '2026-11-01T08:00:00Z',
        uploadedAt: new Date().toISOString(),
        ...overrides,
    };
}

/** Two episodes; the history list preselects the first, so tests that check targeting act on the second. */
function twoEpisodes() {
    return [episode(), episode({ id: 'ep-2', title: 'Second title', fileName: 'second.mp3' })];
}

/** A completed queue entry for `ep-1`, saved the way saveQueueState writes it, so startup restores it. */
function completedQueueEntry(title) {
    return {
        id: 'q-1',
        fileName: 'episode.mp3',
        title,
        fileSize: 1024 * 1024,
        status: 'completed',
        progress: 100,
        stage: 'Completed',
        jobId: 'job-1',
        episodeId: 'ep-1',
        episode: { id: 'ep-1', title, fileName: 'episode.mp3' },
        startedAt: Date.now(),
        normalizationComplete: true,
        transcriptionStatus: null,
        transcriptionError: null,
    };
}

/**
 * Seed browser storage before the page's scripts run.
 * @param {import('@playwright/test').Page} page
 * @param {{ key?: string, queue?: Object[] }} seed
 */
async function seedStorage(page, { key, queue }) {
    await page.addInitScript(([keyName, keyValue, queueName, queueValue]) => {
        if (window.sessionStorage.getItem('seeded')) {
            return;
        }
        window.sessionStorage.setItem('seeded', '1');
        if (keyValue) {
            window.localStorage.setItem(keyName, keyValue);
        }
        if (queueValue) {
            window.localStorage.setItem(queueName, queueValue);
        }
    }, [API_KEY_LOCAL_KEY, key ?? null, QUEUE_STORAGE_KEY, queue ? JSON.stringify(queue) : null]);
}

/** Expand the history panel on its All browsers tab and return the history item for `episodeId`. */
async function openHistoryItem(page, episodeId) {
    const toggle = page.locator('#history-toggle');
    await expect(toggle).toBeVisible();
    if (await toggle.getAttribute('aria-expanded') !== 'true') {
        await toggle.click();
    }
    await page.locator('#filter-browser').click();
    const item = page.locator('#history-list .upload-item[data-id="' + episodeId + '"]');
    await expect(item).toBeVisible();

    return item;
}

/** Open the rename modal for a history item through its context menu. */
async function openRenameModal(page, item) {
    await item.click({ button: 'right' });
    await page.locator('#context-menu .context-menu-item[data-action="rename"]').click();
    await expect(page.locator('#rename-modal-overlay')).toBeVisible();
}

/** Open the rename modal's note panel; its button sits in the suggestion area, shown once a suggestion arrives. */
async function openNotePanel(page) {
    await expect(page.locator('#rename-suggestion-text')).toHaveText('Suggested title');
    await page.locator('#rename-note').click();
    await expect(page.locator('#rename-note-input')).toBeVisible();
}

/**
 * Put a finger down on the middle of an element.
 * @param {import('@playwright/test').Locator} locator
 */
async function touchStart(locator) {
    const box = await locator.boundingBox();
    const x = box.x + box.width / 2;
    const y = box.y + box.height / 2;
    await locator.evaluate((element, [clientX, clientY]) => {
        const touch = new window.Touch({ identifier: 1, target: element, clientX, clientY });
        element.dispatchEvent(new window.TouchEvent('touchstart', { touches: [touch], targetTouches: [touch], changedTouches: [touch], bubbles: true, cancelable: true }));
    }, [x, y]);
}

/**
 * Lift the finger from an element.
 * @param {import('@playwright/test').Locator} locator
 */
async function touchEnd(locator) {
    await locator.evaluate(element => {
        element.dispatchEvent(new window.TouchEvent('touchend', { touches: [], targetTouches: [], changedTouches: [], bubbles: true, cancelable: true }));
    });
}

test('the fixture fills every placeholder in the push page template', async ({ pushServer, page }) => {
    // Arrange
    const html = await renderPushPage();

    // Act
    const response = await page.request.get(pushServer.pushUrl);

    // Assert -- a placeholder the server learned to fill but this fixture does not would reach the page unrendered
    expect(html).not.toContain('{{');
    expect(await response.text()).toBe(html);
});

test('a first visit without a key reaches the history context menu after the key is entered', async ({ pushServer, page }) => {
    // Arrange
    const api = await installPushApi(page, { episodes: [episode()] });
    await page.goto(pushServer.pushUrl);
    await expect(page.locator('#no-key')).toBeVisible();

    // Act
    await page.locator('#paste-key-btn').click();
    await page.locator('#key-textarea').fill(VALID_KEY);
    await page.locator('#save-key-btn').click();
    await expect(page.locator('#no-key')).toBeHidden();
    const item = await openHistoryItem(page, 'ep-1');
    await openRenameModal(page, item);

    // Assert
    expect(api.keyChecks).toContain(VALID_KEY);
    await expect(page.locator('#rename-input')).toHaveValue('Original title');
    expect(api.unexpected).toEqual([]);
});

test('a right click on a history item opens the context menu at the pointer for that item', async ({ pushServer, page }) => {
    // Arrange -- act on the second episode, since the list preselects the first
    const api = await installPushApi(page, { episodes: twoEpisodes() });
    await seedStorage(page, { key: VALID_KEY });
    await page.goto(pushServer.pushUrl);
    const first = await openHistoryItem(page, 'ep-1');
    const item = page.locator('#history-list .upload-item[data-id="ep-2"]');
    // Record where the pointer actually was, since the panel can still be settling when the click lands
    await page.evaluate(() => {
        window.addEventListener('contextmenu', e => {
            window.lastContextMenuPointer = { x: e.clientX, y: e.clientY };
        }, { once: true, capture: true });
    });

    // Act
    await item.click({ button: 'right', position: { x: 20, y: 10 } });

    // Assert
    const menu = page.locator('#context-menu');
    await expect(menu).toBeVisible();
    const menuBox = await menu.boundingBox();
    const pointer = await page.evaluate(() => window.lastContextMenuPointer);
    const viewport = page.viewportSize();
    // showContextMenu places the menu at the pointer, kept 8px inside the viewport
    const clamp = (at, menuSize, viewportSize) => Math.max(8, Math.min(at, viewportSize - menuSize - 8));
    // The pointer reports sub-pixel coordinates, so allow one pixel of rounding
    expect(Math.abs(menuBox.x - clamp(pointer.x, menuBox.width, viewport.width))).toBeLessThanOrEqual(1);
    expect(Math.abs(menuBox.y - clamp(pointer.y, menuBox.height, viewport.height))).toBeLessThanOrEqual(1);
    await expect(item).toHaveClass(/upload-item--selected/);
    await expect(first).not.toHaveClass(/upload-item--selected/);
    await page.locator('#context-menu .context-menu-item[data-action="rename"]').click();
    await expect(page.locator('#rename-input')).toHaveValue('Second title');
    expect(api.unexpected).toEqual([]);
});

test.describe('on a touch screen', () => {
    test.use({ hasTouch: true });

    test('a long press on a history item opens the context menu for that item', async ({ pushServer, page }) => {
        // Arrange -- press the second episode, since the list preselects the first
        const api = await installPushApi(page, { episodes: twoEpisodes() });
        await seedStorage(page, { key: VALID_KEY });
        await page.goto(pushServer.pushUrl);
        const first = await openHistoryItem(page, 'ep-1');
        const item = page.locator('#history-list .upload-item[data-id="ep-2"]');

        // Act -- hold until the menu opens, then lift
        await touchStart(item);
        await expect(page.locator('#context-menu')).toBeVisible();
        await touchEnd(item);

        // Assert -- lifting after the menu opened keeps it open, and it acts on the pressed item
        await expect(page.locator('#context-menu')).toBeVisible();
        await expect(item).toHaveClass(/upload-item--selected/);
        await expect(first).not.toHaveClass(/upload-item--selected/);
        await page.locator('#context-menu .context-menu-item[data-action="rename"]').click();
        await expect(page.locator('#rename-input')).toHaveValue('Second title');
        expect(api.unexpected).toEqual([]);
    });

    test('a short touch on a history item does not open the context menu', async ({ pushServer, page }) => {
        // Arrange -- a fake clock (time still flows for startup) lets the test step past the long-press timer exactly
        await page.clock.install();
        await installPushApi(page, { episodes: [episode()] });
        await seedStorage(page, { key: VALID_KEY });
        await page.goto(pushServer.pushUrl);
        const item = await openHistoryItem(page, 'ep-1');

        // Act
        await touchStart(item);
        await touchEnd(item);
        await page.clock.runFor(LONG_PRESS_MS + 1);

        // Assert
        await expect(page.locator('#context-menu')).toBeHidden();
    });
});

test('a title change sends only the title and renames the history item and the queue entry', async ({ pushServer, page }) => {
    // Arrange
    const api = await installPushApi(page, { episodes: [episode({ note: 'Kept note' })] });
    await seedStorage(page, { key: VALID_KEY, queue: [completedQueueEntry('Original title')] });
    await page.goto(pushServer.pushUrl);
    await expect(page.locator('#queue-item-q-1 .queue-item-name')).toHaveText('Original title');
    const item = await openHistoryItem(page, 'ep-1');
    await openRenameModal(page, item);

    // Act
    await page.locator('#rename-input').fill('Renamed title');
    await page.locator('#rename-save').click();

    // Assert
    await expect(page.locator('#rename-modal-overlay')).toBeHidden();
    expect(api.patches).toEqual([{ episodeId: 'ep-1', body: { title: 'Renamed title' } }]);
    await expect(item.locator('.upload-title')).toHaveText('Renamed title');
    await expect(page.locator('#queue-item-q-1 .queue-item-name')).toHaveText('Renamed title');
    expect(api.unexpected).toEqual([]);
});

test('a note-only save sends only the note and leaves the shown titles alone', async ({ pushServer, page }) => {
    // Arrange -- the server answers with a title that changed since the list loaded (an AI title that arrived late)
    const api = await installPushApi(page, {
        episodes: [episode()],
        patchResponse: (stored, patch) => ({ ...stored, ...patch, title: 'Server title' }),
    });
    await seedStorage(page, { key: VALID_KEY, queue: [completedQueueEntry('Original title')] });
    await page.goto(pushServer.pushUrl);
    const item = await openHistoryItem(page, 'ep-1');
    await openRenameModal(page, item);
    await openNotePanel(page);

    // Act
    await page.locator('#rename-note-input').fill('A note');
    await page.locator('#rename-save').click();

    // Assert -- the note-only save keeps the history item and the queue entry as shown, while the episode's in-memory
    // record takes the server's title, so reopening the editor shows it
    await expect(page.locator('#rename-modal-overlay')).toBeHidden();
    expect(api.patches).toEqual([{ episodeId: 'ep-1', body: { note: 'A note' } }]);
    await expect(item.locator('.upload-title')).toHaveText('Original title');
    await expect(page.locator('#queue-item-q-1 .queue-item-name')).toHaveText('Original title');
    await openRenameModal(page, item);
    await expect(page.locator('#rename-input')).toHaveValue('Server title');
    await expect(page.locator('#rename-note-input')).toHaveValue('A note');
    expect(api.unexpected).toEqual([]);
});

test('emptying a note sends an empty note to clear it', async ({ pushServer, page }) => {
    // Arrange
    const api = await installPushApi(page, { episodes: [episode({ note: 'Old note' })] });
    await seedStorage(page, { key: VALID_KEY });
    await page.goto(pushServer.pushUrl);
    const item = await openHistoryItem(page, 'ep-1');
    await openRenameModal(page, item);
    await openNotePanel(page);
    await expect(page.locator('#rename-note-input')).toHaveValue('Old note');

    // Act
    await page.locator('#rename-note-input').fill('');
    await page.locator('#rename-save').click();

    // Assert
    await expect(page.locator('#rename-modal-overlay')).toBeHidden();
    expect(api.patches).toEqual([{ episodeId: 'ep-1', body: { note: '' } }]);
    expect(api.unexpected).toEqual([]);
});
