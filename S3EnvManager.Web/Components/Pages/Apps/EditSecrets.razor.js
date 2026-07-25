function beforeUnloadHandler(event) {
	event.preventDefault();
	event.returnValue = '';
}

export function registerBeforeUnload() {
	window.addEventListener('beforeunload', beforeUnloadHandler);
}

export function unregisterBeforeUnload() {
	window.removeEventListener('beforeunload', beforeUnloadHandler);
}
