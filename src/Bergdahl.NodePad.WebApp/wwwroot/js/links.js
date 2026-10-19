// "Insert link" dialog for the editor toolbar, in place of Toast UI's own link popup: type a URL as before,
// or pick a note in the tree to link to it with a path relative to the open note.
// Loaded before app.js; uses its state (editor, currentPath) and todoEscape from todo.js only when
// the user acts, after all scripts have loaded.

const linkOverlay = document.getElementById('link-overlay');
const linkUrlInput = document.getElementById('link-url');
const linkTextInput = document.getElementById('link-text');
const linkTree = document.getElementById('link-tree');

let linkEditor = null;   // the editor the link goes into
let linkTextAuto = '';   // link text filled in from a picked note, replaced when another note is picked

// Toolbar item for createEditor() in app.js; a new element each time, as every editor needs its own
function createLinkToolbarItem() {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'toastui-editor-toolbar-icons link';
    button.setAttribute('aria-label', 'Insert link');
    button.addEventListener('click', () => openLinkDialog(editor));
    return { name: 'link', tooltip: 'Insert link', el: button };
}

// Path from the open note to another note, such as Settings.md or ../Folder/folder.md
function relativeNoteLink(fromPath, toPath) {
    const from = fromPath.split('/').slice(0, -1);
    const to = toPath.split('/');
    let common = 0;
    while (common < from.length && common < to.length - 1 && from[common] === to[common]) common++;
    return [...from.slice(common).map(() => '..'), ...to.slice(common)].join('/');
}

function renderLinkTree(nodes) {
    return '<ul>' + nodes.map(node => {
        if (node.type === 'file') {
            const name = node.name.replace(/\.md$/i, '');
            return `<li><button type="button" class="link-tree-item" data-path="${todoEscape(node.path)}" data-name="${todoEscape(name)}">
                    <span class="material-symbols-rounded" aria-hidden="true">description</span>${todoEscape(name)}</button></li>`;
        }
        // Folders on the way to the open note start expanded
        const open = currentPath.startsWith(node.path + '/') ? ' open' : '';
        return `<li><details${open}><summary class="link-tree-item">
                <span class="material-symbols-rounded folder-closed" aria-hidden="true">folder</span>
                <span class="material-symbols-rounded folder-open" aria-hidden="true">folder_open</span>${todoEscape(node.name)}</summary>
                ${renderLinkTree(node.children || [])}</details></li>`;
    }).join('') + '</ul>';
}

async function openLinkDialog(targetEditor) {
    linkEditor = targetEditor;
    linkUrlInput.value = '';
    // Like Toast UI's popup, selected text becomes the link text
    linkTextInput.value = targetEditor.getSelectedText() || '';
    linkTextAuto = '';
    linkTree.innerHTML = '<p class="hint">Loading notes…</p>';
    linkOverlay.classList.add('active');
    linkUrlInput.focus();
    try {
        const res = await fetch('/api/pages/structure?sorted=true&dirsFirst=true');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        linkTree.innerHTML = renderLinkTree(await res.json());
    } catch (err) {
        linkTree.innerHTML = `<p class="hint">Could not load the notes: ${todoEscape(err.message)}</p>`;
    }
}

function closeLinkDialog() {
    linkOverlay.classList.remove('active');
    linkEditor?.focus();
    linkEditor = null;
}

function insertLink() {
    let url = linkUrlInput.value.trim();
    if (!url) {
        linkUrlInput.focus();
        return;
    }
    const text = linkTextInput.value.trim() || url;
    // Spaces and brackets end a Markdown link: use <...> in Markdown, %20 and the like in WYSIWYG
    if (/[\s()]/.test(url) && !/^<.*>$/.test(url)) {
        url = linkEditor.isMarkdownMode() && !/[<>]/.test(url)
            ? `<${url}>`
            : url.replace(/[\s()]/g, c => '%' + c.charCodeAt(0).toString(16).toUpperCase().padStart(2, '0'));
    }
    linkEditor.exec('addLink', { linkUrl: url, linkText: text });
    closeLinkDialog();
}

function pickLinkNote(item) {
    linkTree.querySelectorAll('.link-tree-item.selected').forEach(el => el.classList.remove('selected'));
    item.classList.add('selected');
    linkUrlInput.value = relativeNoteLink(currentPath, item.dataset.path);
    // Use the note name as link text, unless the user typed (or selected) their own
    if (!linkTextInput.value.trim() || linkTextInput.value === linkTextAuto) {
        linkTextInput.value = linkTextAuto = item.dataset.name;
    }
}

linkTree.addEventListener('click', e => {
    const item = e.target.closest('button[data-path]');
    if (item) pickLinkNote(item);
});
// Double-click picks the note and inserts the link straight away
linkTree.addEventListener('dblclick', e => {
    const item = e.target.closest('button[data-path]');
    if (!item) return;
    pickLinkNote(item);
    insertLink();
});

document.getElementById('link-insert-btn').addEventListener('click', insertLink);
document.getElementById('link-cancel-btn').addEventListener('click', closeLinkDialog);
document.getElementById('link-close-btn').addEventListener('click', closeLinkDialog);
linkOverlay.addEventListener('click', e => {
    if (e.target === linkOverlay) closeLinkDialog();
});
linkOverlay.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
        e.preventDefault();
        closeLinkDialog();
    } else if (e.key === 'Enter' && e.target.tagName === 'INPUT') {
        e.preventDefault();
        insertLink();
    }
});
