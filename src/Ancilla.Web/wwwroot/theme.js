// Applies a theme immediately and remembers it in a cookie, so the server can render the
// right data-theme on the next page load (no flash of the wrong theme).
window.haloTheme = {
    get: () => document.documentElement.dataset.theme || "basic",
    set: (id) => {
        document.documentElement.dataset.theme = id;
        document.cookie = `theme=${encodeURIComponent(id)}; path=/; max-age=31536000; samesite=lax`;
    },
};
