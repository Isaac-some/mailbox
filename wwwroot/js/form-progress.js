document.addEventListener('submit', event => {
    const form = event.target;
    if (!form.matches('form[data-busy-label]') || event.defaultPrevented || !form.checkValidity()) return;
    const button = form.querySelector('button[type="submit"]');
    if (!button) return;
    button.disabled = true;
    button.textContent = form.dataset.busyLabel;
});
