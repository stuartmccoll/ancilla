// Applies a theme immediately and remembers it in a cookie, so the server can render the
// right data-theme on the next page load (no flash of the wrong theme).
window.haloTheme = {
    get: () => document.documentElement.dataset.theme || "basic",
    set: (id) => {
        document.documentElement.dataset.theme = id;
        document.cookie = `theme=${encodeURIComponent(id)}; path=/; max-age=31536000; samesite=lax`;
    },
    // Back to the default, and the cookie deleted.
    forget: () => {
        document.documentElement.dataset.theme = "basic";
        document.cookie = "theme=; path=/; max-age=0; samesite=lax";
    },
};

// Saves text the server built (an export) as a file, without the data ever leaving the page.
window.ancillaDownload = (name, mime, text) => {
    const url = URL.createObjectURL(new Blob([text], { type: mime + ";charset=utf-8" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};
