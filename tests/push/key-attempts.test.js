import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as flush } from 'node:timers/promises';

import { KEY_VERDICT, createKeyAttempts } from '../../FeatherPod.Server/Pages/Push/modules/key-attempts.js';

/**
 * Track whether a promise has resolved, and with what.
 * @param {Promise<any>} promise
 */
function track(promise) {
    const state = { done: false, value: undefined };
    promise.then(value => {
        state.done = true;
        state.value = value;
    });

    return state;
}

test('each new attempt supersedes the previous one', () => {
    const attempts = createKeyAttempts();
    const first = attempts.begin();
    const second = attempts.begin();

    assert.equal(attempts.isSuperseded(first), true);
    assert.equal(attempts.isSuperseded(second), false);
    assert.equal(attempts.current, second);
});

test('an attempt is open only while it is current and has no verdict', () => {
    const attempts = createKeyAttempts();
    const first = attempts.begin();
    assert.equal(attempts.isOpen(first), true);

    attempts.settle(first, KEY_VERDICT.ACCEPTED);
    assert.equal(attempts.isOpen(first), false);

    const second = attempts.begin();
    assert.equal(attempts.isOpen(first), false);
    assert.equal(attempts.isOpen(second), true);

    attempts.begin();
    assert.equal(attempts.isOpen(second), false);
});

test('a superseded attempt cannot record a verdict', () => {
    const attempts = createKeyAttempts();
    const first = attempts.begin();
    attempts.begin();

    assert.equal(attempts.settle(first, KEY_VERDICT.REJECTED), false);
    assert.equal(attempts.verdict, KEY_VERDICT.PENDING);
});

test('an attempt records only its first verdict', () => {
    const attempts = createKeyAttempts();
    const attempt = attempts.begin();

    assert.equal(attempts.settle(attempt, KEY_VERDICT.ACCEPTED), true);
    assert.equal(attempts.settle(attempt, KEY_VERDICT.REJECTED), false);
    assert.equal(attempts.verdict, KEY_VERDICT.ACCEPTED);
});

test('pending is not a verdict', () => {
    const attempts = createKeyAttempts();
    const attempt = attempts.begin();

    assert.equal(attempts.settle(attempt, KEY_VERDICT.PENDING), false);
    assert.equal(attempts.settle(attempt, KEY_VERDICT.REJECTED), true);
});

test('a new attempt starts pending', () => {
    const attempts = createKeyAttempts();
    attempts.settle(attempts.begin(), KEY_VERDICT.ACCEPTED);
    attempts.begin();

    assert.equal(attempts.verdict, KEY_VERDICT.PENDING);
});

test('waitForVerdict waits while the attempt is pending and resolves true once it is accepted', async () => {
    const attempts = createKeyAttempts();
    const attempt = attempts.begin();
    const wait = track(attempts.waitForVerdict(attempt));

    await flush();
    assert.equal(wait.done, false);

    attempts.settle(attempt, KEY_VERDICT.ACCEPTED);
    await flush();
    assert.deepEqual(wait, { done: true, value: true });
});

test('waitForVerdict resolves false once the attempt is rejected', async () => {
    const attempts = createKeyAttempts();
    const attempt = attempts.begin();
    const wait = track(attempts.waitForVerdict(attempt));

    attempts.settle(attempt, KEY_VERDICT.REJECTED);
    await flush();
    assert.deepEqual(wait, { done: true, value: false });
});

test('waitForVerdict resolves false once the attempt is superseded, even if the newer one is accepted', async () => {
    const attempts = createKeyAttempts();
    const attempt = attempts.begin();
    const wait = track(attempts.waitForVerdict(attempt));

    attempts.settle(attempts.begin(), KEY_VERDICT.ACCEPTED);
    await flush();
    assert.deepEqual(wait, { done: true, value: false });
});

test('waitForVerdict resolves at once for a settled or superseded attempt', async () => {
    const attempts = createKeyAttempts();
    const accepted = attempts.begin();
    attempts.settle(accepted, KEY_VERDICT.ACCEPTED);
    assert.equal(await attempts.waitForVerdict(accepted), true);

    attempts.begin();
    assert.equal(await attempts.waitForVerdict(accepted), false);
});

test('whenAccepted waits through a rejection and a new pending attempt until a key is accepted', async () => {
    const attempts = createKeyAttempts();
    const rejected = attempts.begin();
    const wait = track(attempts.whenAccepted());

    attempts.settle(rejected, KEY_VERDICT.REJECTED);
    const prompt = attempts.begin();
    await flush();
    assert.equal(wait.done, false);

    attempts.settle(prompt, KEY_VERDICT.ACCEPTED);
    await flush();
    assert.equal(wait.done, true);
});

test('whenAccepted resolves at once while the current attempt is accepted', async () => {
    const attempts = createKeyAttempts();
    attempts.settle(attempts.begin(), KEY_VERDICT.ACCEPTED);

    await attempts.whenAccepted();
});
