// Pre-paint theme restore. Blocking <head> script — applies data-theme before first paint.
(function () {
    var stored = localStorage.getItem('theme');
    var theme = stored;
    if (!theme) {
        theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    document.documentElement.setAttribute('data-theme', theme);
})();
