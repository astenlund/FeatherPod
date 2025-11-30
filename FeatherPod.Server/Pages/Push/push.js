const FEED_ID = '{{FEED_ID}}';
const ALLOWED_EXTENSIONS = ['.mp3', '.m4a', '.wav', '.ogg', '.flac', '.aac'];
let apiKey = null;
const states = ['no-key', 'ready', 'uploading', 'normalizing', 'success', 'error'];
const JOB_STORAGE_KEY = 'featherpod_job_' + FEED_ID;

/** @param {string} stateName */
function showState(stateName) {
    states.forEach(s => document.getElementById(s).style.display = s === stateName ? 'block' : 'none');
}

/** @param {File} file */
function isValidAudioFile(file) {
    const extension = '.' + file.name.split('.').pop().toLowerCase();

    return ALLOWED_EXTENSIONS.includes(extension);
}

function init() {
    const fragment = window.location.hash.slice(1);
    if (fragment) {
        apiKey = fragment;
        sessionStorage.setItem('featherpod_api_key_' + FEED_ID, apiKey);
        history.replaceState(null, '', window.location.pathname);
    } else {
        const storedKey = sessionStorage.getItem('featherpod_api_key_' + FEED_ID);
        if (storedKey) {
            apiKey = storedKey;
        } else {
            showState('no-key');
            return;
        }
    }

    // Try to restore previous job state (e.g., after page refresh)
    if (restoreJobState()) {
        return;
    }

    showState('ready');
    document.getElementById('select-file').focus();
}

document.getElementById('select-file').addEventListener('click', () => {
    document.getElementById('file-input').click();
});

document.getElementById('upload-another').addEventListener('click', () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    document.getElementById('select-file').focus();
});

document.getElementById('try-another').addEventListener('click', () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    document.getElementById('select-file').focus();
});

document.getElementById('file-input').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');

        return;
    }
    await uploadFile(file);
});

// Drag and drop support
const dropZone = document.getElementById('drop-zone');

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropZone.classList.add('drag-over');
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
});

dropZone.addEventListener('drop', async (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
    const file = e.dataTransfer.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');

        return;
    }
    await uploadFile(file);
});

/** @param {File} file */
async function uploadFile(file) {
    showState('uploading');
    document.getElementById('file-name').textContent = file.name;
    document.getElementById('upload-status').textContent = 'Uploading...';
    document.getElementById('upload-progress').style.width = '0%';

    const progressContainer = document.querySelector('.progress-container');
    progressContainer.setAttribute('aria-valuenow', '0');

    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhr.upload.addEventListener('progress', (e) => {
                if (e.lengthComputable) {
                    const percent = Math.round((e.loaded / e.total) * 100);
                    document.getElementById('upload-progress').style.width = percent + '%';
                    progressContainer.setAttribute('aria-valuenow', percent.toString());
                    document.getElementById('upload-status').textContent =
                        'Uploading... ' + formatBytes(e.loaded) + ' / ' + formatBytes(e.total);
                }
            });
            xhr.onload = () => resolve({ status: xhr.status, body: xhr.responseText });
            xhr.onerror = () => reject(new Error('Network error'));
            xhr.open('POST', '/api/feeds/' + FEED_ID + '/episodes?normalize=true');
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            // Sync success (server-side normalization disabled, or edge case)
            saveJobState({ status: 'success', fileName: file.name });
            showSuccess(file.name);
        } else if (response.status === 202) {
            // Async normalization - start polling
            const jobResponse = JSON.parse(response.body);
            saveJobState({
                status: 'normalizing',
                jobId: jobResponse.jobId,
                fileName: file.name
            });
            await pollNormalizationJob(jobResponse.jobId, file.name);
        } else if (response.status === 401) {
            showError('Invalid API key');
        } else if (response.status === 403) {
            showError('API key does not have access to this feed');
        } else {
            const error = JSON.parse(response.body);
            showError(error.error || 'Upload failed');
        }
    } catch (err) {
        showError(err.message || 'Upload failed');
    }
}

/** @param {string} fileName */
function showSuccess(fileName) {
    showState('success');
    document.getElementById('ep-filename').textContent = fileName;
    document.getElementById('upload-another').focus();
}

/** @param {string} message */
function showError(message) {
    showState('error');
    document.getElementById('error-message').textContent = message;
    document.getElementById('try-another').focus();
}

/** @param {number} bytes */
function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));

    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

/**
 * @typedef {Object} JobStatus
 * @property {string} status - Queued, Processing, Completed, Failed
 * @property {string} [stage] - Queued, Downloading, Analyzing, Normalizing, Uploading, Finalizing
 * @property {number} [progressPercent]
 * @property {string} [error]
 */

/**
 * @param {string} jobId
 * @param {string} fileName
 */
async function pollNormalizationJob(jobId, fileName) {
    showState('normalizing');
    document.getElementById('normalizing-file-name').textContent = fileName;
    const statusEl = document.getElementById('normalizing-status');

    const pollInterval = 2000;

    while (true) {
        try {
            const response = await fetch('/api/jobs/' + jobId, {
                headers: { 'X-API-Key': apiKey }
            });

            if (!response.ok) {
                saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
                showError('Failed to check job status');
                return;
            }

            const job = await response.json();

            if (job.status === 'Completed') {
                saveJobState({ status: 'success', fileName });
                showSuccess(fileName);
                return;
            } else if (job.status === 'Failed') {
                const errorMsg = job.error || 'Normalization failed';
                saveJobState({ status: 'error', fileName, error: errorMsg });
                showError(errorMsg);
                return;
            }

            // Update progress display
            if (job.stage) {
                const showPercent = (job.stage === 'Analyzing' || job.stage === 'Normalizing') && job.progressPercent != null;
                statusEl.textContent = job.stage + '...' + (showPercent ? ' ' + job.progressPercent + '%' : '');
            }

            await new Promise(resolve => setTimeout(resolve, pollInterval));
        } catch (err) {
            saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
            showError('Failed to check job status');
            return;
        }
    }
}

/** @param {object} state */
function saveJobState(state) {
    sessionStorage.setItem(JOB_STORAGE_KEY, JSON.stringify(state));
}

function clearJobState() {
    sessionStorage.removeItem(JOB_STORAGE_KEY);
}

function restoreJobState() {
    const saved = sessionStorage.getItem(JOB_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const job = JSON.parse(saved);

    if (job.status === 'success') {
        showSuccess(job.fileName);
        return true;
    } else if (job.status === 'error') {
        showError(job.error);
        return true;
    } else if (job.status === 'normalizing') {
        void pollNormalizationJob(job.jobId, job.fileName);
        return true;
    }

    return false;
}

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);
