// The lesson editor (docs/adr/0013 decision 6): a word-processor-style editor that saves Markdown, built on the vendored
// Toast UI Editor (wwwroot/lib/toastui-editor, MIT). Used only by Pages/Lessons/LessonEditor.razor, as a JavaScript
// module. Its own toolbar and mode switch are not used: the page's labelled toolbar (role="toolbar") drives it, with
// arrow-key movement between the buttons, and its dialogs ask for a link's address or a picture and its description.
//
// Pictures: lesson text refers to a picture by its bare file name. In the editor each name is swapped for the address the
// page gives for it, so the browser can show it, and swapped back when the text is read. A picture inserted here is first
// sent to the server (POST /curriculum/suggestions/{id}/pictures, antiforgery token in the RequestVerificationToken
// header), which resizes and stores it and answers with its file name and address.

let editor = null;
let pictures = {};
let token = '';
let dotnet = null;

function loadScript(src) {
  if (window.toastui && window.toastui.Editor) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.onload = resolve;
    script.onerror = reject;
    document.head.appendChild(script);
  });
}

function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function toEditor(markdown) {
  let result = markdown || '';
  for (const [name, url] of Object.entries(pictures)) {
    result = result.replace(new RegExp('\\]\\(' + escapeRegExp(name) + '\\)', 'g'), '](' + url + ')');
  }
  return result;
}

function fromEditor(markdown) {
  let result = markdown || '';
  for (const [name, url] of Object.entries(pictures)) {
    result = result.replace(new RegExp('\\]\\(' + escapeRegExp(url) + '\\)', 'g'), '](' + name + ')');
  }
  // The editor writes an empty paragraph as a <br> tag, which lesson text (raw HTML disabled) would show as text.
  result = result.replace(/^\s*<br\s*\/?>\s*$/gim, '');
  return result.replace(/\n{3,}/g, '\n\n').trim() + '\n';
}

function editable(host) {
  return host.querySelector('.toastui-editor-ww-container .ProseMirror') || host.querySelector('[contenteditable]');
}

function labelEditable(host, label) {
  const element = editable(host);
  if (!element) return;
  element.setAttribute('role', 'textbox');
  element.setAttribute('aria-multiline', 'true');
  element.setAttribute('aria-label', label);
}

export async function create(host, toolbar, markdown, pictureUrls, antiforgeryToken, dotnetRef, scriptUrl) {
  await loadScript(scriptUrl);
  pictures = Object.assign({}, pictureUrls || {});
  token = antiforgeryToken || '';
  dotnet = dotnetRef;
  if (editor) {
    editor.destroy();
  }
  editor = new window.toastui.Editor({
    el: host,
    initialEditType: 'wysiwyg',
    previewStyle: 'tab',
    hideModeSwitch: true,
    toolbarItems: [],
    usageStatistics: false,
    autofocus: false,
    height: 'auto',
    minHeight: '380px',
    initialValue: toEditor(markdown)
  });
  // The library's own hidden Markdown pane and toolbar are not used.
  host.querySelectorAll('.toastui-editor-toolbar, .toastui-editor-md-container, .toastui-editor-mode-switch').forEach(e => { e.hidden = true; });
  labelEditable(host, host.dataset.label || 'Lesson text');
  wireToolbar(host, toolbar);
}

function wireToolbar(host, toolbar) {
  const buttons = Array.from(toolbar.querySelectorAll('button[data-command]'));
  buttons.forEach((button, index) => {
    button.tabIndex = index === 0 ? 0 : -1;
    // Keep the selection in the text when a button is pressed with the mouse.
    button.addEventListener('mousedown', e => e.preventDefault());
    button.addEventListener('click', () => run(button.dataset.command, host));
    button.addEventListener('keydown', e => {
      let next = null;
      if (e.key === 'ArrowRight') next = (index + 1) % buttons.length;
      else if (e.key === 'ArrowLeft') next = (index - 1 + buttons.length) % buttons.length;
      else if (e.key === 'Home') next = 0;
      else if (e.key === 'End') next = buttons.length - 1;
      if (next === null) return;
      e.preventDefault();
      buttons.forEach(b => { b.tabIndex = -1; });
      buttons[next].tabIndex = 0;
      buttons[next].focus();
    });
  });
}

function run(command, host) {
  if (!editor) return;
  switch (command) {
    case 'bold': editor.exec('bold'); break;
    case 'italic': editor.exec('italic'); break;
    case 'heading': editor.exec('heading', { level: 2 }); break;
    case 'subheading': editor.exec('heading', { level: 3 }); break;
    case 'bulletList': editor.exec('bulletList'); break;
    case 'orderedList': editor.exec('orderedList'); break;
    case 'quote': editor.exec('blockQuote'); break;
    case 'link': openLinkDialog(host); return;
    case 'picture': openPictureDialog(host); return;
  }
  editor.focus();
}

function dialog(id) {
  return document.getElementById(id);
}

function openLinkDialog(host) {
  const box = dialog('link-dialog');
  const address = box.querySelector('input[name="address"]');
  const error = box.querySelector('[data-error]');
  address.value = '';
  error.textContent = '';
  box.querySelector('form').onsubmit = e => {
    e.preventDefault();
    const url = address.value.trim();
    if (!/^(https?:|mailto:|tel:)/i.test(url)) {
      error.textContent = 'Enter an address starting with https://';
      address.focus();
      return;
    }
    box.close();
    const text = editor.getSelectedText() || url;
    editor.exec('addLink', { linkUrl: url, linkText: text });
    editor.focus();
  };
  box.querySelector('[data-cancel]').onclick = () => { box.close(); editor.focus(); };
  box.showModal();
  address.focus();
}

function openPictureDialog(host) {
  const box = dialog('picture-dialog');
  const file = box.querySelector('input[type="file"]');
  const description = box.querySelector('input[name="description"]');
  const error = box.querySelector('[data-error]');
  const submit = box.querySelector('button[type="submit"]');
  file.value = '';
  description.value = '';
  error.textContent = '';
  box.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    if (!file.files || file.files.length === 0) {
      error.textContent = 'Choose a picture.';
      file.focus();
      return;
    }
    const alt = description.value.trim();
    if (!alt) {
      error.textContent = 'Describe the picture for anyone who cannot see it.';
      description.focus();
      return;
    }
    submit.disabled = true;
    try {
      const id = await dotnet.invokeMethodAsync('EnsureDraftAsync', getMarkdown());
      const form = new FormData();
      form.append('file', file.files[0]);
      const response = await fetch('/curriculum/suggestions/' + id + '/pictures', {
        method: 'POST',
        body: form,
        headers: { 'RequestVerificationToken': token },
        credentials: 'same-origin'
      });
      const answer = await response.json().catch(() => ({}));
      if (!response.ok) {
        error.textContent = answer.message || 'The picture could not be uploaded. Try again.';
        return;
      }
      pictures[answer.fileName] = answer.url;
      box.close();
      editor.exec('addImage', { imageUrl: answer.url, altText: alt });
      editor.focus();
    } catch {
      error.textContent = 'The picture could not be uploaded. Try again.';
    } finally {
      submit.disabled = false;
    }
  };
  box.querySelector('[data-cancel]').onclick = () => { box.close(); editor.focus(); };
  box.showModal();
  file.focus();
}

export function getMarkdown() {
  return editor ? fromEditor(editor.getMarkdown()) : '';
}

export function setMarkdown(markdown) {
  if (!editor) return;
  editor.setMarkdown(toEditor(markdown), false);
}

export function focus() {
  if (editor) editor.focus();
}

export function addPictures(pictureUrls) {
  Object.assign(pictures, pictureUrls || {});
}

export function dispose() {
  if (editor) {
    editor.destroy();
    editor = null;
  }
  dotnet = null;
}
