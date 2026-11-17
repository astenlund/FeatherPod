import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    ATTEMPT_ORDER,
    TRANSCRIPT_RETRY_KIND,
    attemptOf,
    compareJobAttempt,
    isEarlierAttemptAnswer,
    isNormalizationFailure,
    isServerRetryable,
    isUploadRetryable,
    offersRetry,
    isTranscriptRetrySettled,
    offersTranscriptRetry,
    transcriptRetryEpisodeId
} from '../../FeatherPod.Server/Pages/Push/modules/job-attempts.js';

test('a job or entry without a usable attempt is on its first attempt', () => {
    for (const holder of [null, undefined, {}, { attempt: null }, { attempt: 0 }, { attempt: -2 }, { attempt: 1.5 }, { attempt: '2' }]) {
        assert.equal(attemptOf(holder), 1, JSON.stringify(holder));
    }
    assert.equal(attemptOf({ attempt: 3 }), 3);
});

test('an answer is ordered against the attempt the entry follows', () => {
    const entry = { attempt: 2 };

    assert.equal(compareJobAttempt(entry, { attempt: 1 }), ATTEMPT_ORDER.EARLIER);
    assert.equal(compareJobAttempt(entry, { attempt: 2 }), ATTEMPT_ORDER.SAME);
    assert.equal(compareJobAttempt(entry, { attempt: 3 }), ATTEMPT_ORDER.LATER);
    assert.equal(compareJobAttempt({}, {}), ATTEMPT_ORDER.SAME, 'entries and answers from before attempts existed agree');
    assert.equal(compareJobAttempt({}, { attempt: 2 }), ATTEMPT_ORDER.LATER);
});

test('only an answer for an earlier attempt is stale, and a missing answer is not', () => {
    const entry = { attempt: 2 };

    assert.ok(isEarlierAttemptAnswer(entry, { attempt: 1, status: 'Failed' }));
    assert.ok(isEarlierAttemptAnswer(entry, { status: 'Failed' }), 'an answer without an attempt is the first attempt');
    assert.ok(!isEarlierAttemptAnswer(entry, { attempt: 2 }));
    assert.ok(!isEarlierAttemptAnswer(entry, { attempt: 3 }));
    assert.ok(!isEarlierAttemptAnswer(entry, null));
});

test('a normalization failure is a Failed upload job with a normalization error', () => {
    assert.ok(isNormalizationFailure({ status: 'Failed', normalizationError: 'ffmpeg exited 1' }));
    assert.ok(!isNormalizationFailure({ status: 'Failed', normalizationError: null }));
    assert.ok(!isNormalizationFailure({ status: 'Failed', normalizationError: '' }));
    assert.ok(!isNormalizationFailure({ status: 'Completed', normalizationError: 'x' }));
    assert.ok(!isNormalizationFailure({ status: 'Cancelled', normalizationError: 'x' }));
    assert.ok(!isNormalizationFailure({ status: 'Failed', normalizationError: 'x', kind: TRANSCRIPT_RETRY_KIND }));
    assert.ok(!isNormalizationFailure(null));
});

test('a failed entry whose job failed in normalization is retried on the server, with or without its file', () => {
    const withoutFile = { status: 'failed', jobId: 'j1', normalizationFailed: true, file: null };
    const withFile = { ...withoutFile, file: {} };

    assert.ok(isServerRetryable(withoutFile));
    assert.ok(isServerRetryable(withFile));
    assert.ok(offersRetry(withoutFile));
});

test('a failed entry without a job, or whose job did not fail in normalization, is not retried on the server', () => {
    assert.ok(!isServerRetryable({ status: 'failed', jobId: null, normalizationFailed: true }));
    assert.ok(!isServerRetryable({ status: 'failed', jobId: 'j1' }));
    assert.ok(!isServerRetryable({ status: 'failed', jobId: 'j1', normalizationFailed: true, kind: TRANSCRIPT_RETRY_KIND }));
    assert.ok(!isServerRetryable({ status: 'completed', jobId: 'j1', normalizationFailed: true }));
});

test('an upload retry needs the file and a passed validation', () => {
    assert.ok(isUploadRetryable({ status: 'failed', file: {} }));
    assert.ok(isUploadRetryable({ status: 'cancelled', file: {} }));
    assert.ok(!isUploadRetryable({ status: 'failed', file: null }));
    assert.ok(!isUploadRetryable({ status: 'failed', file: {}, validationError: true }));
    assert.ok(!isUploadRetryable({ status: 'normalizing', file: {} }));
});

test('a failed entry without its file whose job did not fail in normalization offers no retry', () => {
    assert.ok(!offersRetry({ status: 'failed', jobId: 'j1', file: null }));
    assert.ok(!offersRetry({ status: 'failed', jobId: null, file: {}, validationError: true }));
    assert.ok(offersRetry({ status: 'failed', jobId: null, file: {} }));
});

test('a completed entry with a failed transcript offers a transcript retry of its episode', () => {
    // The episode the job is bound to wins over a resolved one, which can be another episode sharing the file name
    assert.equal(transcriptRetryEpisodeId({ status: 'completed', transcriptionStatus: 'Failed', episode: { id: 'ep-1' }, episodeId: 'ep-0' }), 'ep-0');
    assert.equal(transcriptRetryEpisodeId({ status: 'completed', transcriptionStatus: 'Failed', episode: { id: 'ep-1' }, episodeId: null }), 'ep-1');
    assert.equal(transcriptRetryEpisodeId({ status: 'completed', transcriptionStatus: 'Failed', episode: null, episodeId: 'ep-2' }), 'ep-2');
    assert.equal(transcriptRetryEpisodeId({ status: 'completed', transcriptionStatus: 'Failed', episode: null, episodeId: null }), null);
    assert.equal(transcriptRetryEpisodeId({ status: 'completed', transcriptionStatus: 'Completed', episodeId: 'ep-1' }), null);
    assert.equal(transcriptRetryEpisodeId({ status: 'normalizing', transcriptionStatus: 'Failed', episodeId: 'ep-1' }), null);
});

test('an active transcript retry, or one that published the transcript, settles the episode; one that failed again does not', () => {
    const retry = (fields) => ({ kind: 'TranscriptRetry', episodeId: 'ep-1', status: 'normalizing', transcriptionStatus: 'Running', ...fields });
    const isActive = e => e.status === 'normalizing';
    assert.ok(isTranscriptRetrySettled([retry()], 'ep-1', isActive));
    assert.ok(isTranscriptRetrySettled([retry({ status: 'completed', transcriptionStatus: 'Completed' })], 'ep-1', isActive));
    assert.ok(!isTranscriptRetrySettled([retry({ status: 'completed', transcriptionStatus: 'Failed' })], 'ep-1', isActive));
    assert.ok(!isTranscriptRetrySettled([retry()], 'ep-2', isActive));
    assert.ok(!isTranscriptRetrySettled([{ kind: null, episodeId: 'ep-1', status: 'completed', transcriptionStatus: 'Completed' }], 'ep-1', isActive));
});

test('the history menu offers a transcript retry only for an episode whose transcript failed', () => {
    assert.ok(offersTranscriptRetry({ transcriptStatus: 'Failed' }));
    assert.ok(!offersTranscriptRetry({ transcriptStatus: 'Available' }));
    assert.ok(!offersTranscriptRetry({ transcriptStatus: null }));
    assert.ok(!offersTranscriptRetry({}));
    assert.ok(!offersTranscriptRetry(null));
});
