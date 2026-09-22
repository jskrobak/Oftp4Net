export function setColorMode(colorMode) {
    const dark = colorMode === 'dark'
        || (colorMode === 'auto' && window.matchMedia('(prefers-color-scheme: dark)').matches);

    document.documentElement.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
}
