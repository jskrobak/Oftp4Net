export function submitForm(formId) {
    document.getElementById(formId)?.submit();
}

// HxSidebarItem does not accept a target attribute, so links marked with the class are opened in a new tab here.
export function openMarkedLinksInNewTab(cssClass) {
    document.querySelectorAll(`a.${cssClass}, .${cssClass} a`).forEach(link => {
        link.target = '_blank';
        link.rel = 'noopener';
    });
}
