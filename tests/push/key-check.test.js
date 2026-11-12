import { test } from 'node:test';
import assert from 'node:assert/strict';

import { KEY_CHECK_STATUS, classifyKeyCheckStatus, isKeyCheckInconclusive, retryKeyCheck, unverifiedKeyMessage } from '../../FeatherPod.Server/Pages/Push/modules/key-check.js';

const noWait = async () => {};
const verdict = { networkError: false, serverError: false };
const serverError = { networkError: false, serverError: true };
const networkError = { networkError: true, serverError: false };

test('treats only 401 as a rejected key', () => {
    assert.equal(classifyKeyCheckStatus(200), KEY_CHECK_STATUS.OK);
    assert.equal(classifyKeyCheckStatus(401), KEY_CHECK_STATUS.REJECTED);
});

test('treats every other failure status as a server error, not a verdict on the key', () => {
    for (const status of [400, 403, 404, 429, 500, 502, 503, 504]) {
        assert.equal(classifyKeyCheckStatus(status), KEY_CHECK_STATUS.SERVER_ERROR, String(status));
    }
});

test('a server or network error is inconclusive, a verdict is not', () => {
    assert.equal(isKeyCheckInconclusive(serverError), true);
    assert.equal(isKeyCheckInconclusive(networkError), true);
    assert.equal(isKeyCheckInconclusive(verdict), false);
});

test('retries a server error and returns the first conclusive result', async () => {
    const results = [serverError, serverError, { ...verdict, valid: true }];
    let calls = 0;

    const result = await retryKeyCheck(async () => results[calls++], { wait: noWait });

    assert.equal(calls, 3);
    assert.equal(result.valid, true);
});

test('stops after the allowed retries and returns the last inconclusive result', async () => {
    let calls = 0;

    const result = await retryKeyCheck(async () => {
        calls++;

        return serverError;
    }, { retries: 2, wait: noWait });

    assert.equal(calls, 3);
    assert.equal(result.serverError, true);
});

test('does not retry a rejected key', async () => {
    let calls = 0;

    await retryKeyCheck(async () => {
        calls++;

        return verdict;
    }, { wait: noWait });

    assert.equal(calls, 1);
});

test('waits the configured delay before each retry', async () => {
    const waits = [];

    await retryKeyCheck(async () => networkError, { retries: 2, delayMs: 250, wait: async ms => waits.push(ms) });

    assert.deepEqual(waits, [250, 250]);
});

test('names the cause when a key is kept unchecked', () => {
    assert.equal(unverifiedKeyMessage(serverError, 'saved key'), 'Server error, using saved key');
    assert.equal(unverifiedKeyMessage(networkError, 'URL key'), 'Server unreachable, using URL key');
});
