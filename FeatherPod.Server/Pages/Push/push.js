const FEED_ID = '{{FEED_ID}}';
const ALLOWED_EXTENSIONS = ['.mp3', '.m4a', '.wav', '.ogg', '.flac', '.aac'];
let apiKey = null;
const states = ['no-key', 'ready', 'uploading', 'success', 'error'];

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
        showState('ready');
    } else {
        const storedKey = sessionStorage.getItem('featherpod_api_key_' + FEED_ID);
        if (storedKey) {
            apiKey = storedKey;
            showState('ready');
        } else {
            showState('no-key');
        }
    }
    document.getElementById('select-file').focus();
}

document.getElementById('select-file').addEventListener('click', () => {
    document.getElementById('file-input').click();
});

document.getElementById('upload-another').addEventListener('click', () => {
    document.getElementById('file-input').value = '';
    showState('ready');
    document.getElementById('select-file').focus();
});

document.getElementById('try-another').addEventListener('click', () => {
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
            xhr.open('POST', '/api/feeds/' + FEED_ID + '/episodes');
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            showSuccess(episode, file.name);
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

/**
 * @param {{ fileName?: string, fileSize?: number }} episode
 * @param {string} fileName
 */
function showSuccess(episode, fileName) {
    showState('success');
    document.getElementById('ep-filename').textContent = episode.fileName || fileName;
    document.getElementById('ep-size').textContent = formatBytes(episode.fileSize || 0);
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

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);
