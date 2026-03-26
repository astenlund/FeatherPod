const listeners = {};

export function on(event, callback) {
    (listeners[event] ||= []).push(callback);
}

export function off(event, callback) {
    const list = listeners[event];
    if (list) {
        listeners[event] = list.filter(fn => fn !== callback);
    }
}

export function emit(event, data) {
    for (const fn of listeners[event] || []) {
        fn(data);
    }
}
