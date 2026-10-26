// The page the service worker serves for any navigation with no connection (offline.html): the lessons
// saved on this device (offline-lessons.js, functional.md FR-023), by rank, each with links to its files.
// Links go to /lessons/files/{id}, or for a converted lesson (adr/0012) its lesson page /lessons/{code},
// which service-worker.js answers from the saved copy.
(function () {
    var target = document.getElementById('saved-lessons');

    function element(tag, text) {
        var e = document.createElement(tag);
        if (text !== undefined) e.textContent = text;
        return e;
    }

    caches.open('vlms-lessons')
        .then(function (cache) { return cache.match('/vlms-saved-index.json'); })
        .then(function (r) { return r ? r.json() : { lessons: [] }; })
        .catch(function () { return { lessons: [] }; })
        .then(function (index) {
            var rank = null;
            var lessons = null;
            index.lessons.forEach(function (lesson) {
                if (lesson.rank !== rank) {
                    rank = lesson.rank;
                    target.appendChild(element('h2', rank));
                    lessons = element('ul');
                    target.appendChild(lessons);
                }
                var item = element('li');
                if (lesson.page) {
                    var page = element('a', lesson.title);
                    page.href = lesson.page;
                    item.appendChild(page);
                    lessons.appendChild(item);
                    return;
                }
                item.appendChild(element('strong', lesson.title));
                var files = element('ul');
                lesson.files.forEach(function (file) {
                    var link = element('a', file.name);
                    link.href = file.url || '/lessons/files/' + file.id;
                    var fileItem = element('li');
                    fileItem.appendChild(link);
                    files.appendChild(fileItem);
                });
                item.appendChild(files);
                lessons.appendChild(item);
            });
        });
})();
