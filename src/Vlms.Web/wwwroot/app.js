// The app's only script besides Blazor's: the navigation drawer below 64rem (MainLayout.razor's
// "Menu" button) and closing the account menu. Event delegation on the document, so it keeps working
// when Blazor's enhanced navigation replaces the layout's DOM.
(function () {
    function setNav(open) {
        document.documentElement.toggleAttribute('data-nav-open', open);
        document.querySelectorAll('[data-nav-toggle]').forEach(function (b) { b.setAttribute('aria-expanded', open ? 'true' : 'false'); });
    }

    document.addEventListener('click', function (e) {
        var toggle = e.target.closest('[data-nav-toggle]');
        if (toggle) {
            setNav(toggle.getAttribute('aria-expanded') !== 'true');
            return;
        }
        // A click outside an open account menu closes it.
        document.querySelectorAll('details.account[open]').forEach(function (d) {
            if (!d.contains(e.target)) d.removeAttribute('open');
        });
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        var account = document.querySelector('details.account[open]');
        if (account) {
            account.removeAttribute('open');
            account.querySelector('summary').focus();
            return;
        }
        if (document.documentElement.hasAttribute('data-nav-open')) {
            setNav(false);
            var toggle = document.querySelector('[data-nav-toggle]');
            if (toggle) toggle.focus();
        }
    });

    // After an enhanced navigation the drawer and account menu start closed.
    function onNavigated() {
        setNav(false);
        document.querySelectorAll('details.account[open]').forEach(function (d) { d.removeAttribute('open'); });
    }
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', onNavigated);
    } else {
        document.addEventListener('DOMContentLoaded', function () {
            if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', onNavigated);
        });
    }
})();
