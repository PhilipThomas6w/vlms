// Lessons saved for offline use (functional.md FR-023, adr/0011). Plain progressive enhancement of
// the lesson library: LessonLibrary.razor renders a hidden [data-offline-lesson] block per lesson with
// its id, title, rank and files in data attributes; this script shows it, keeps the saved copies in the
// "vlms-lessons" cache (read by service-worker.js and offline.js) and a small index of what is saved.
// A converted lesson (adr/0012 decision 7) is saved as its self-contained page (data-page,
// /lessons/{code}/offline) and its pictures, with its content version (data-version): when the
// server's version differs, the page and pictures are fetched again. Each saved file carries its own
// url; records saved before that have none and are files at /lessons/files/{id}.
// Event delegation on the document plus a re-scan on Blazor's `enhancedload`, so it keeps working when
// enhanced navigation replaces the page's DOM.
(function () {
    var CACHE = 'vlms-lessons';
    var INDEX = '/vlms-saved-index.json';

    if (!('caches' in window) || !('serviceWorker' in navigator)) return;

    // Reads and writes of the index must not interleave (a refresh at page load and a click on Save).
    var queue = Promise.resolve();
    function serial(work) {
        var result = queue.then(work);
        queue = result.catch(function () { });
        return result;
    }

    function fileUrl(file) { return file.url || '/lessons/files/' + file.id; }

    // Everything a saved lesson keeps in the cache: its files (or pictures) and, if converted, its page.
    function urlsOf(lesson) {
        var urls = lesson.files.map(fileUrl);
        if (lesson.page) urls.push(lesson.page);
        return urls;
    }

    function readIndex(cache) {
        return cache.match(INDEX).then(function (r) { return r ? r.json() : { lessons: [] }; })
            .catch(function () { return { lessons: [] }; });
    }

    function writeIndex(cache, index) {
        index.lessons.sort(function (a, b) { return a.rankPosition - b.rankPosition || a.position - b.position; });
        return cache.put(INDEX, new Response(JSON.stringify(index), { headers: { 'Content-Type': 'application/json' } }));
    }

    // One lesson file (or page, or picture) from the server into the cache; a redirect means the sign-in has lapsed.
    function fetchUrl(cache, url) {
        return fetch(url, { credentials: 'same-origin' }).then(function (r) {
            if (!r.ok || r.redirected) throw new Error(url);
            return cache.put(url, r);
        });
    }

    function fetchUrls(cache, urls) {
        return urls.reduce(function (done, url) {
            return done.then(function () { return fetchUrl(cache, url); });
        }, Promise.resolve());
    }

    function deleteUrls(cache, urls) {
        return Promise.all(urls.map(function (url) { return cache.delete(url); }));
    }

    function save(lesson) {
        return serial(function () {
            var persist = navigator.storage && navigator.storage.persist ? navigator.storage.persist().catch(function () { }) : Promise.resolve();
            return persist.then(function () { return caches.open(CACHE); }).then(function (cache) {
                return fetchUrls(cache, urlsOf(lesson))
                    .catch(function (error) {
                        return deleteUrls(cache, urlsOf(lesson)).then(function () { throw error; });
                    })
                    .then(function () { return readIndex(cache); })
                    .then(function (index) {
                        index.lessons = index.lessons.filter(function (l) { return l.id !== lesson.id; });
                        index.lessons.push(lesson);
                        return writeIndex(cache, index);
                    });
            });
        });
    }

    function remove(id) {
        return serial(function () {
            return caches.open(CACHE).then(function (cache) {
                return readIndex(cache).then(function (index) {
                    var gone = index.lessons.filter(function (l) { return l.id === id; });
                    index.lessons = index.lessons.filter(function (l) { return l.id !== id; });
                    return writeIndex(cache, index).then(function () {
                        return Promise.all(gone.map(function (l) { return deleteUrls(cache, urlsOf(l)); }));
                    });
                });
            });
        });
    }

    // Saved lessons follow the library: a lesson that has gone is removed, a file that has gone is
    // removed, a new file is fetched, and a converted lesson whose content version changed is fetched
    // again in full. Skipped with no connection, or if the sign-in has lapsed (the
    // answer is then not JSON); saved lessons stay on the device after sign-out either way.
    function refresh() {
        if (!navigator.onLine) return Promise.resolve();
        return serial(function () {
            return caches.open(CACHE).then(function (cache) {
                return readIndex(cache).then(function (index) {
                    if (index.lessons.length === 0) return;
                    var query = index.lessons.map(function (l) { return 'id=' + l.id; }).join('&');
                    return fetch('/lessons/saved?' + query, { credentials: 'same-origin', headers: { Accept: 'application/json' }, redirect: 'manual' })
                        .then(function (r) {
                            var type = r.headers.get('Content-Type') || '';
                            return r.ok && type.indexOf('json') >= 0 ? r.json() : null;
                        })
                        .then(function (current) {
                            if (!current) return;
                            var byId = {};
                            current.forEach(function (l) { byId[l.id] = l; });
                            return index.lessons.reduce(function (done, saved) {
                                return done.then(function () { return sync(cache, index, saved, byId[saved.id]); });
                            }, Promise.resolve()).then(function () { return writeIndex(cache, index); });
                        });
                });
            });
        }).then(scan, function () { });
    }

    function sync(cache, index, saved, now) {
        var savedUrls = urlsOf(saved);
        if (!now) {
            index.lessons = index.lessons.filter(function (l) { return l.id !== saved.id; });
            return deleteUrls(cache, savedUrls);
        }
        var record = lessonRecord(now);
        var nowUrls = urlsOf(record);
        var changed = (record.version || null) !== (saved.version || null);
        var removed = savedUrls.filter(function (url) { return nowUrls.indexOf(url) < 0; });
        return deleteUrls(cache, removed)
            .then(function () {
                // A changed lesson in full; otherwise what is new, and anything the device has lost.
                if (changed) return fetchUrls(cache, nowUrls);
                return Promise.all(nowUrls.map(function (url) { return cache.match(url); })).then(function (found) {
                    return fetchUrls(cache, nowUrls.filter(function (url, i) { return !found[i]; }));
                });
            })
            .then(function () { Object.assign(saved, record); })
            .catch(function () { /* keep what is saved; the next visit tries again */ });
    }

    // A lesson as /lessons/saved describes it, in the form the index keeps.
    function lessonRecord(l) {
        var files = l.page
            ? (l.pictureIds || []).map(function (id) { return { id: id, name: '', url: '/lessons/pictures/' + id }; })
            : l.files.map(function (f) { return { id: f.id, name: f.displayName || f.name || f.fileName, url: '/lessons/files/' + f.id }; });
        return { id: l.id, code: l.code, title: l.title, rank: l.rank, rankPosition: l.rankPosition, position: l.position, files: files, page: l.page || null, version: l.version || null };
    }

    // ---- The buttons ----

    function isIos() {
        return /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    }

    function isStandalone() {
        return navigator.standalone === true || (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches);
    }

    function lessonOf(block) {
        return {
            id: Number(block.dataset.id), code: block.dataset.code, title: block.dataset.title, rank: block.dataset.rank,
            rankPosition: Number(block.dataset.rankPosition), position: Number(block.dataset.position),
            files: JSON.parse(block.dataset.files), page: block.dataset.page || null, version: block.dataset.version || null
        };
    }

    function show(block, saved) {
        var button = block.querySelector('[data-offline-action]');
        var status = block.querySelector('[data-offline-status]');
        block.hidden = false;
        button.disabled = false;
        button.removeAttribute('aria-busy');
        button.dataset.offlineAction = saved ? 'remove' : 'save';
        button.textContent = saved ? 'Remove' : 'Save to this device';
        status.textContent = saved ? 'Saved on this device' : '';
    }

    function scan() {
        var blocks = document.querySelectorAll('[data-offline-lesson]');
        if (blocks.length === 0) return Promise.resolve();
        if (isIos() && !isStandalone()) {
            // One line at the top of the page (LessonLibrary.razor); the save buttons stay hidden, because
            // Safari may delete what a site saves unless the app is on the home screen.
            var line = document.querySelector('[data-offline-home-screen]');
            if (line) line.hidden = false;
            return Promise.resolve();
        }
        return caches.open(CACHE).then(readIndex).then(function (index) {
            blocks.forEach(function (block) {
                var id = Number(block.dataset.id);
                show(block, index.lessons.some(function (l) { return l.id === id; }));
            });
        });
    }

    document.addEventListener('click', function (e) {
        var button = e.target.closest('[data-offline-action]');
        if (!button) return;
        var block = button.closest('[data-offline-lesson]');
        var lesson = lessonOf(block);
        var status = block.querySelector('[data-offline-status]');
        if (button.dataset.offlineAction === 'remove') {
            remove(lesson.id).then(function () { show(block, false); });
            return;
        }
        button.disabled = true;
        button.setAttribute('aria-busy', 'true');
        button.textContent = 'Saving…';
        save(lesson).then(function () { show(block, true); }, function () {
            show(block, false);
            status.textContent = 'Could not save. Try again.';
        });
    });

    scan().then(refresh);
    if (window.Blazor && Blazor.addEventListener) {
        Blazor.addEventListener('enhancedload', scan);
    }
})();
