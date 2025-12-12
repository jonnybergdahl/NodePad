const menu = document.getElementById('menu');

// Toolbar buttons
const btnCreateFile = document.getElementById('btn-create-file');
const btnCreateFolder = document.getElementById('btn-create-folder');
const btnEdit = document.getElementById('btn-edit');
const btnSave = document.getElementById('btn-save');
const btnCancel = document.getElementById('btn-cancel');
const btnDelete = document.getElementById('btn-delete');
const btnRename = document.getElementById('btn-rename');


// Breadcrumbs
const breadcrumbsEl = document.getElementById('breadcrumbs');
let serverBreadcrumbs = null;

// Tags UI elements
const tagsBar = document.getElementById('tags-bar');
const tagsView = document.getElementById('tags-view');
const tagsEdit = document.getElementById('tags-edit');
const tagsInput = document.getElementById('tags-input');

async function fetchMetaTags(path) {
    try {
        const res = await fetch(`/api/pages/meta?path=${encodeURIComponent(path)}`);
        if (!res.ok) return [];
        const data = await res.json();
        if (Array.isArray(data)) return data;
        // Fallback if server ever returns CSV string
        if (typeof data === 'string') return data.split(',').map(t => t.trim()).filter(Boolean);
        return [];
    } catch { return []; }
}

function renderTagsChips(tags) {
    if (!tagsView) return;
    const items = (tags && tags.length) ? tags : [];
    if (items.length === 0) {
        tagsView.innerHTML = '<span class="tag-chip" title="No tags">No tags</span>';
        return;
    }
    // Render each tag as a selectable chip; mark selected if it is part of the active tagFilter
    const active = new Set(tagFilter.map(normalizeTag));
    tagsView.innerHTML = items.map(t => {
        const norm = normalizeTag(t);
        const selClass = active.has(norm) ? ' selected' : '';
        return `<span class="tag-chip selectable${selClass}" data-tag="${norm}" title="Filter by tag: ${t}">${t}</span>`;
    }).join(' ');
}

async function updateTagsUI() {
    if (!tagsBar) return;
    const hasFile = !!currentPath;
    tagsBar.style.display = hasFile ? 'block' : 'none';
    if (!hasFile) return;

    const tags = await fetchMetaTags(currentPath);
    if (isEditMode) {
        if (tagsEdit) tagsEdit.style.display = 'block';
        if (tagsView) tagsView.style.display = 'none';
        if (tagsInput) tagsInput.value = (tags || []).join(', ');
    } else {
        if (tagsEdit) tagsEdit.style.display = 'none';
        if (tagsView) tagsView.style.display = 'flex';
        renderTagsChips(tags || []);
    }
}

// Make page tags clickable to apply filter
if (tagsView) {
    tagsView.addEventListener('click', (e) => {
        const chip = e.target.closest('.tag-chip.selectable');
        if (!chip) return;
        const t = normalizeTag(chip.getAttribute('data-tag'));
        const set = new Set(tagFilter);
        if (set.has(t)) set.delete(t); else set.add(t);
        tagFilter = Array.from(set);
        // Refresh tags UI to reflect selected state
        updateTagsUI();
        // Ask server for filtered/sorted structure
        loadMenu();
    });
}

// Theme switcher
const themeSelect = document.getElementById('theme-select');
const prefersDarkQuery = window.matchMedia('(prefers-color-scheme: dark)');

function applyTheme(mode) {
    const root = document.documentElement;
    if (mode === 'light') {
        root.setAttribute('data-theme', 'light');
    } else if (mode === 'dark') {
        root.setAttribute('data-theme', 'dark');
    } else {
        // system: remove explicit override and rely on media query
        root.removeAttribute('data-theme');
    }
}

function getEffectiveTheme() {
    // Returns 'dark' or 'light' based on data-theme or system preference
    const root = document.documentElement;
    const explicit = root.getAttribute('data-theme');
    if (explicit === 'dark') return 'dark';
    if (explicit === 'light') return 'light';
    return prefersDarkQuery.matches ? 'dark' : 'light';
}

function initTheme() {
    const saved = localStorage.getItem('theme');
    if (saved === 'light' || saved === 'dark') {
        applyTheme(saved);
        if (themeSelect) themeSelect.value = saved;
    } else {
        // No saved preference: follow system
        applyTheme(); // removes override
        const effective = prefersDarkQuery.matches ? 'dark' : 'light';
        if (themeSelect) themeSelect.value = effective;
    }
}

function applyToastUITheme() {
    const mode = getEffectiveTheme();
    // Recreate viewer with chosen theme
    if (viewer) {
        const markdown = typeof currentContent === 'string' && currentContent.length >= 0
            ? currentContent
            : (viewer.getMarkdown ? viewer.getMarkdown() : '');
        if (viewer.destroy) viewer.destroy();
        viewer = null;
        createViewer(markdown);
    }
    // Recreate editor if in edit mode
    if (isEditMode && editor) {
        const md = editor.getMarkdown();
        const editorContainer = document.getElementById('editor-edit');
        const height = editorContainer ? editorContainer.style.height || 'calc(100vh - 250px)' : 'calc(100vh - 250px)';
        editor.destroy();
        const _csp = resolveCodeSyntaxPlugin();
        editor = new toastui.Editor({
            el: editorContainer,
            initialEditType: 'markdown',
            previewStyle: 'vertical',
            height,
            initialValue: md,
            theme: mode === 'dark' ? 'dark' : 'default',
            plugins: _csp ? [[_csp, { highlighter: Prism }]] : [],
            hooks: {
                addImageBlobHook: async (blob, callback) => {
                    try {
                        const fd = new FormData();
                        fd.append('image', blob, blob.name || 'image');
                        const resp = await fetch(`/api/uploads/images?pagePath=${encodeURIComponent(currentPath || '')}`, { method: 'POST', body: fd });
                        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
                        const data = await resp.json();
                        if (data && data.url) {
                            callback(data.url, blob.name || 'image');
                        } else {
                            throw new Error('Invalid response');
                        }
                    } catch (err) {
                        console.error('Image upload failed', err);
                        if (typeof showToast === 'function') showToast('Image upload failed', 'danger');
                    }
                }
            }
        });
    }
}

if (themeSelect) {
    themeSelect.addEventListener('change', (e) => {
        const mode = e.target.value;
        localStorage.setItem('theme', mode);
        applyTheme(mode);
        applyToastUITheme();
    });
}

// Update if system preference changes while following system (no explicit choice saved)
prefersDarkQuery.addEventListener('change', () => {
    const saved = localStorage.getItem('theme');
    if (!saved) {
        applyTheme();
        // Also update the select to reflect the new effective theme
        const effective = prefersDarkQuery.matches ? 'dark' : 'light';
        if (themeSelect) themeSelect.value = effective;
        applyToastUITheme();
    }
});

initTheme();

// Resolve Toast UI Code Syntax Highlight plugin from various possible globals (CDN variants)
function resolveCodeSyntaxPlugin() {
    try {
        const t = window.toastui;
        if (t && t.Editor && t.Editor.plugin && t.Editor.plugin.codeSyntaxHighlight) {
            return t.Editor.plugin.codeSyntaxHighlight;
        }
        // Some CDN builds expose the plugin as a standalone global
        if (window.toastuiEditorPluginCodeSyntaxHighlight) {
            return window.toastuiEditorPluginCodeSyntaxHighlight;
        }
        // Older builds may expose under EditorPlugin namespace
        if (t && t.EditorPlugin && t.EditorPlugin.codeSyntaxHighlight) {
            return t.EditorPlugin.codeSyntaxHighlight;
        }
    } catch (e) { /* ignore */ }
    return null;
}

// Modal elements
const modalOverlay = document.getElementById('modal-overlay');
const modalTitle = document.getElementById('modal-title');
const modalMessage = document.getElementById('modal-message');
const modalInput = document.getElementById('modal-input');
const modalConfirmBtn = document.getElementById('modal-confirm-btn');
const modalCancelBtn = document.getElementById('modal-cancel-btn');
const modalCloseBtn = document.getElementById('modal-close-btn');

// Toast UI Viewer/Editor helpers with theme support
const DEFAULT_WELCOME = '<h1>Welcome to NodePad!</h1>Select a file from the menu or create a new one.';
let viewer = null;
function createViewer(initialMarkdown) {
    const mode = getEffectiveTheme();
    const el = document.querySelector('#editor');
    const _csp = resolveCodeSyntaxPlugin();
    viewer = new toastui.Editor.factory({
        el,
        viewer: true,
        initialValue: typeof initialMarkdown === 'string' ? initialMarkdown : DEFAULT_WELCOME,
        theme: mode === 'dark' ? 'dark' : 'default',
        plugins: _csp ? [[_csp, { highlighter: Prism }]] : []
    });
}

createViewer();

// Attempt to load default index.md on startup
async function initDefaultPage() {
    try {
        const resp = await fetch(`/api/pages/content?path=${encodeURIComponent('index.md')}`);
        if (resp.ok) {
            const content = await resp.text();
            currentPath = 'index.md';
            currentContent = content;
            viewer.setMarkdown(content);
            updateToolbar();
            // Sync selection highlight in menu
            loadMenu();
        }
        // If not ok (e.g., 404), keep the default welcome text.
    } catch (e) {
        // Ignore errors on initial load; keep default welcome.
        console.warn('Init default page failed:', e);
    }
}

let editor = null;
let isEditMode = false;
let currentPath = '';
let currentContent = '';
let originalContent = '';
let currentFolderPath = '';
let selectedFolderPath = '';

// Modal Helper Functions
function showModal(title, message, showInput = false, defaultValue = '') {
    return new Promise((resolve) => {
        modalTitle.textContent = title;
        modalMessage.textContent = message;

        if (showInput) {
            modalInput.style.display = 'block';
            modalInput.value = defaultValue;
            setTimeout(() => modalInput.focus(), 100);
        } else {
            modalInput.style.display = 'none';
        }

        modalOverlay.classList.add('active');

        const handleConfirm = () => {
            const value = showInput ? modalInput.value : true;
            cleanup();
            resolve(value);
        };

        const handleCancel = () => {
            cleanup();
            resolve(null);
        };

        const handleKeyDown = (e) => {
            if (e.key === 'Enter') {
                handleConfirm();
            } else if (e.key === 'Escape') {
                handleCancel();
            }
        };

        const cleanup = () => {
            modalOverlay.classList.remove('active');
            modalConfirmBtn.removeEventListener('click', handleConfirm);
            modalCancelBtn.removeEventListener('click', handleCancel);
            modalCloseBtn.removeEventListener('click', handleCancel);
            document.removeEventListener('keydown', handleKeyDown);
        };

        modalConfirmBtn.addEventListener('click', handleConfirm);
        modalCancelBtn.addEventListener('click', handleCancel);
        modalCloseBtn.addEventListener('click', handleCancel);
        document.addEventListener('keydown', handleKeyDown);
    });
}

function showToast(message, type = 'info') {
    const toastContainer = document.getElementById('toast-container');

    const icons = {
        success: '✅',
        error: '❌',
        info: 'ℹ️',
        warning: '⚠️'
    };

    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.innerHTML = `
        <span class="toast-icon">${icons[type]}</span>
        <span class="toast-message">${message}</span>
        <button class="toast-close">&times;</button>
    `;

    toastContainer.appendChild(toast);

    const closeBtn = toast.querySelector('.toast-close');
    closeBtn.addEventListener('click', () => {
        toast.remove();
    });

    setTimeout(() => {
        toast.remove();
    }, 5000);
}

function hasUnsavedChanges() {
    if (!isEditMode || !editor) return false;
    const currentEditorContent = editor.getMarkdown();
    return currentEditorContent !== originalContent;
}

function renderBreadcrumbs() {
    if (!breadcrumbsEl) return;

    // Helper to clear and re-render
    breadcrumbsEl.innerHTML = '';

    // Add Home crumb always
    const homeCrumb = document.createElement('span');
    homeCrumb.className = 'crumb';
    homeCrumb.textContent = 'Home';
    homeCrumb.addEventListener('click', () => {
        selectedFolderPath = '';
        // Keep current file if one is open; just clear folder selection
        currentFolderPath = '';
        loadMenu();
        updateToolbar();
    });
    breadcrumbsEl.appendChild(homeCrumb);

    function addSep() {
        const sep = document.createElement('span');
        sep.className = 'sep';
        sep.textContent = '›';
        breadcrumbsEl.appendChild(sep);
    }

    // If server-provided breadcrumbs are available, render those and return
    if (Array.isArray(serverBreadcrumbs) && serverBreadcrumbs.length > 0) {
        for (let i = 0; i < serverBreadcrumbs.length; i++) {
            const item = serverBreadcrumbs[i];
            addSep();
            const crumb = document.createElement('span');
            crumb.className = 'crumb';
            crumb.textContent = item.name || item.Name || '';
            const isLast = i === serverBreadcrumbs.length - 1;
            if (!isLast) {
                crumb.addEventListener('click', () => {
                    const p = (item.path || item.Path || '').replace(/\\/g, '/');
                    selectedFolderPath = p;
                    currentFolderPath = p;
                    loadMenu();
                    updateToolbar();
                });
            } else {
                crumb.addEventListener('click', () => {
                    const p = (item.path || item.Path || '').replace(/\\/g, '/');
                    if (p && p.endsWith('.md')) {
                        loadFile(p);
                    }
                });
            }
            breadcrumbsEl.appendChild(crumb);
        }
        return;
    }

    // Determine what path to render: prefer file path, else selected folder
    const pathToShow = currentPath || selectedFolderPath || '';
    if (!pathToShow) {
        return; // Only Home
    }

    const isFile = !!currentPath;
    const parts = pathToShow.split('/').filter(Boolean);
    let acc = '';

    // If file, exclude last part for folder crumbs
    const loopCount = isFile ? parts.length - 1 : parts.length;

    for (let i = 0; i < loopCount; i++) {
        addSep();
        acc = i === 0 ? parts[0] : acc + '/' + parts[i];
        const crumb = document.createElement('span');
        crumb.className = 'crumb';
        crumb.textContent = parts[i];
        crumb.addEventListener('click', () => {
            selectedFolderPath = acc;
            currentFolderPath = acc;
            // Do not change currentPath when navigating folders
            loadMenu();
            updateToolbar();
        });
        breadcrumbsEl.appendChild(crumb);
    }

    if (isFile) {
        const fileName = parts[parts.length - 1];
        if (fileName) {
            addSep();
            const fileCrumb = document.createElement('span');
            fileCrumb.className = 'crumb';
            fileCrumb.textContent = fileName;
            fileCrumb.addEventListener('click', () => {
                // Reload file explicitly
                loadFile(pathToShow);
            });
            breadcrumbsEl.appendChild(fileCrumb);
        }
    }
}

function updateToolbar() {
    if (isEditMode) {
        // Hide creation actions while editing
        btnCreateFile.style.display = 'none';
        btnCreateFolder.style.display = 'none';

        btnEdit.style.display = 'none';
        btnSave.style.display = 'inline-flex';
        btnCancel.style.display = 'inline-flex';
        btnDelete.style.display = 'none';
    } else {
        // Show creation actions when not editing
        btnCreateFile.style.display = 'inline-flex';
        btnCreateFolder.style.display = 'inline-flex';

        btnEdit.style.display = currentPath ? 'inline-flex' : 'none';
        btnSave.style.display = 'none';
        btnCancel.style.display = 'none';
        btnDelete.style.display = (currentPath || selectedFolderPath) ? 'inline-flex' : 'none';
        if (btnRename) btnRename.style.display = (currentPath || selectedFolderPath) ? 'inline-flex' : 'none';
    }
    // Keep breadcrumbs in sync with current selection
    renderBreadcrumbs();
    // Keep tags UI in sync
    updateTagsUI();
}

btnCreateFile.addEventListener('click', async function() {
    if (hasUnsavedChanges()) {
        const save = await showModal('Unsaved Changes', 'Do you want to save your changes before creating a new file?', false);
        if (save === true) {
            await saveFile();
        }
    }

    const name = await showModal('Create New Document', 'Enter the name of the document:', true);
    if (!name) return;

    const finalName = name.endsWith('.md') ? name : `${name}.md`;
    const createPath = currentFolderPath ? `${currentFolderPath}/${finalName}` : finalName;

    createEntity(createPath, 'file');
});

btnCreateFolder.addEventListener('click', async function() {
    const name = await showModal('Create New Node', 'Enter the name of the node:', true);
    if (!name) return;

    const createPath = currentFolderPath ? `${currentFolderPath}/${name}` : name;

    createEntity(createPath, 'directory');
});

btnEdit.addEventListener('click', function() {
    if (!currentPath) {
        showToast('Please select a file first', 'warning');
        return;
    }

    document.querySelector('#editor').style.display = 'none';

    const editorContainer = document.createElement('div');
    editorContainer.id = 'editor-edit';
    const editorEl = document.getElementById('editor');
    editorEl.parentNode.insertBefore(editorContainer, editorEl);

    // Skapa editor med syntax highlighting
    const _csp = resolveCodeSyntaxPlugin();
    editor = new toastui.Editor({
        el: editorContainer,
        initialEditType: 'markdown',
        previewStyle: 'vertical',
        height: 'calc(100vh - 250px)',
        initialValue: currentContent,
        theme: getEffectiveTheme() === 'dark' ? 'dark' : 'default',
        plugins: _csp ? [[_csp, { highlighter: Prism }]] : [],
        hooks: {
            addImageBlobHook: async (blob, callback) => {
                try {
                    const fd = new FormData();
                    fd.append('image', blob, blob.name || 'image');
                    const resp = await fetch(`/api/uploads/images?pagePath=${encodeURIComponent(currentPath || '')}`, { method: 'POST', body: fd });
                    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
                    const data = await resp.json();
                    if (data && data.url) {
                        callback(data.url, blob.name || 'image');
                    } else {
                        throw new Error('Invalid response');
                    }
                } catch (err) {
                    console.error('Image upload failed', err);
                    if (typeof showToast === 'function') showToast('Image upload failed', 'danger');
                }
            }
        }
    });

    originalContent = currentContent;

    isEditMode = true;
    updateToolbar();
});

btnCancel.addEventListener('click', async function() {
    if (hasUnsavedChanges()) {
        const confirmed = await showModal('Discard Changes?', 'You have unsaved changes. Do you want to discard them?', false);
        if (confirmed !== true) return;
    }

    cancelEdit();
});

function cancelEdit() {
    if (editor) {
        editor.destroy();
        editor = null;
    }
    const editorEdit = document.getElementById('editor-edit');
    if (editorEdit) {
        editorEdit.remove();
    }

    document.querySelector('#editor').style.display = 'block';
    viewer.setMarkdown(currentContent);

    isEditMode = false;
    originalContent = '';
    updateToolbar();
}

btnSave.addEventListener('click', saveFile);

btnDelete.addEventListener('click', async function() {
    // If a folder is selected, handle folder deletion
    if (selectedFolderPath) {
        const folderName = selectedFolderPath.split('/').pop();
        try {
            // Try non-recursive delete first
            const resp = await fetch(`/api/pages/delete?path=${encodeURIComponent(selectedFolderPath)}`, { method: 'DELETE' });
            if (resp.ok) {
                // If current file is inside this folder, clear it
                if (currentPath && (currentPath === selectedFolderPath || currentPath.startsWith(selectedFolderPath + '/'))) {
                    currentPath = '';
                    currentContent = DEFAULT_WELCOME;
                    viewer.setMarkdown(currentContent);
                    if (isEditMode) cancelEdit();
                }
                // Clear folder selection
                selectedFolderPath = '';
                currentFolderPath = '';
                await loadMenu();
                updateToolbar();
                showToast('Folder deleted', 'success');
                return;
            }
            if (resp.status === 409) {
                const confirmed = await showModal('Delete Folder?', `The folder "${folderName}" is not empty. Delete folder and all its contents?`, false);
                if (confirmed === true) {
                    const resp2 = await fetch(`/api/pages/delete?path=${encodeURIComponent(selectedFolderPath)}&recursive=true`, { method: 'DELETE' });
                    if (!resp2.ok) {
                        throw new Error(`HTTP error! status: ${resp2.status}`);
                    }
                    if (currentPath && (currentPath === selectedFolderPath || currentPath.startsWith(selectedFolderPath + '/'))) {
                        currentPath = '';
                        currentContent = DEFAULT_WELCOME;
                        viewer.setMarkdown(currentContent);
                        if (isEditMode) cancelEdit();
                    }
                    selectedFolderPath = '';
                    currentFolderPath = '';
                    await loadMenu();
                    updateToolbar();
                    showToast('Folder and its contents deleted', 'success');
                    return;
                }
                // Cancel: do nothing
                return;
            }

            // Fallback: if status is not 409, but the folder is non-empty, still prompt the user
            const nonEmpty = await isFolderNonEmpty(selectedFolderPath);
            if (nonEmpty) {
                const confirmed = await showModal('Delete Folder?', `The folder "${folderName}" is not empty. Delete folder and all its contents?`, false);
                if (confirmed === true) {
                    const resp2 = await fetch(`/api/pages/delete?path=${encodeURIComponent(selectedFolderPath)}&recursive=true`, { method: 'DELETE' });
                    if (!resp2.ok) {
                        throw new Error(`HTTP error! status: ${resp2.status}`);
                    }
                    if (currentPath && (currentPath === selectedFolderPath || currentPath.startsWith(selectedFolderPath + '/'))) {
                        currentPath = '';
                        currentContent = DEFAULT_WELCOME;
                        viewer.setMarkdown(currentContent);
                        if (isEditMode) cancelEdit();
                    }
                    selectedFolderPath = '';
                    currentFolderPath = '';
                    await loadMenu();
                    updateToolbar();
                    showToast('Folder and its contents deleted', 'success');
                    return;
                }
                return;
            }

            throw new Error(`HTTP error! status: ${resp.status}`);
        } catch (err) {
            console.error('Error deleting folder:', err);
            showToast(`Failed to delete folder: ${err.message}`, 'error');
        }
        return;
    }

    // Otherwise, delete file
    if (!currentPath) {
        showToast('No file or folder selected', 'warning');
        return;
    }

    const fileName = currentPath.split('/').pop();
    const confirmed = await showModal('Delete File?', `Are you sure you want to delete:\n${fileName}?`, false);

    if (confirmed === true) {
        deleteEntity(fileName, 'file', currentPath);
    }
});

if (btnRename) btnRename.addEventListener('click', async function() {
    try {
        if (!(currentPath || selectedFolderPath)) {
            showToast('No file or folder selected', 'warning');
            return;
        }
        const renamingFolder = !!selectedFolderPath;
        const oldPath = renamingFolder ? selectedFolderPath : currentPath;
        const defaultName = renamingFolder ? (oldPath.split('/').pop() || '') : ((oldPath.split('/').pop() || '').replace(/\.md$/i, ''));
        const input = await showModal('Rename', `Enter a new name for ${renamingFolder ? 'folder' : 'file'}:`, true, defaultName);
        if (input === null) return;
        let newName = (input || '').trim();
        if (!newName) return;
        // Normalize: if a full path was pasted, take only the last segment; server will also validate
        newName = newName.split(/[\\/]+/).filter(Boolean).pop() || '';
        if (!newName) return;
        const resp = await fetch(`/api/pages/rename?path=${encodeURIComponent(oldPath)}&newName=${encodeURIComponent(newName)}`, { method: 'POST' });
        if (!resp.ok) { throw new Error(`HTTP ${resp.status}`); }
        const data = await resp.json().catch(() => ({}));
        const newPath = (data && data.path) ? data.path : (renamingFolder ? oldPath : oldPath.replace(/[^/]+$/, (m) => newName.endsWith('.md') ? newName : (newName + '.md')));
        if (renamingFolder) {
            if (currentPath) {
                if (currentPath === oldPath) {
                    currentPath = '';
                } else if (currentPath.startsWith(oldPath + '/')) {
                    currentPath = newPath + currentPath.substring(oldPath.length);
                }
            }
            selectedFolderPath = newPath;
            currentFolderPath = newPath;
        } else {
            if (currentPath === oldPath) {
                try {
                    if (tagsIndex && tagsIndex[oldPath]) {
                        tagsIndex[newPath] = tagsIndex[oldPath];
                        delete tagsIndex[oldPath];
                    }
                } catch {}
                currentPath = newPath;
            }
        }
        try { expandAncestorsForPath(newPath, renamingFolder); } catch {}
        await loadMenu();
        updateToolbar();
        showToast('Renamed successfully', 'success');
    } catch (err) {
        console.error('Rename failed', err);
        showToast(`Rename failed: ${err.message}`, 'error');
    }
});

// Tag filtering state and index
let tagsIndex = {}; // path -> [tags]
let allKnownTags = new Map();
let tagFilter = []; // active filter tags (lowercased)
let tagsIndexBuilding = false;

function normalizeTag(t){ return (t || '').trim().toLowerCase(); }

function renderTagFilterBar() {
    // Build a sorted array of [norm, display] pairs
    const pairs = Array.from(allKnownTags.entries())
        .map(([norm, display]) => [norm, display || norm])
        .sort((a, b) => a[1].localeCompare(b[1]));
    const active = new Set(tagFilter.map(normalizeTag));
    const chipsHtml = pairs.length ? (
        `<div class="tag-suggestions" id="tag-suggestions">` +
        pairs.map(([norm, display])=>{
            const sel = active.has(norm) ? ' selected' : '';
            return `<span class="tag-suggestion${sel}" data-tag="${norm}" title="Toggle filter: ${display}">${display}</span>`;
        }).join(' ') +
        `</div>`
    ) : '<div class="tag-suggestions" id="tag-suggestions"><span class="menu-filter-label">No tags indexed yet</span></div>';
    return `
    <div class="menu-filter-bar">
        <div class="menu-filter-label">Filter by tag</div>
        ${chipsHtml}
    </div>`;
}

function attachTagFilterEvents() {
    const sugg = document.getElementById('tag-suggestions');

    if (sugg) {
        sugg.addEventListener('click', (e) => {
            const item = e.target.closest('.tag-suggestion');
            if (!item) return;
            const t = normalizeTag(item.getAttribute('data-tag'));
            const set = new Set(tagFilter);
            if (set.has(t)) {
                set.delete(t);
            } else {
                set.add(t);
            }
            tagFilter = Array.from(set);
            if (window.__lastStructure) {
                const data = window.__lastStructure;
                const tree = (tagFilter.length && Object.keys(tagsIndex).length) ? filterTreeByTags(data, tagFilter) : data;
                // Rely on server-side sorting; no client-side sorting applied
                menu.innerHTML = `<div id=\"menu-tree\" class=\"menu-tree\">${renderTree(tree)}</div>` + renderTagFilterBar();
                attachTagFilterEvents();
            }
        });
    }
}

function collectFilesFromStructure(nodes, acc) {
    for (const n of nodes) {
        if (n.type === 'file') acc.push(n.path);
        if (n.children && n.children.length) collectFilesFromStructure(n.children, acc);
    }
    return acc;
}

async function buildTagsIndexFromStructure(structure) {
    if (tagsIndexBuilding) return;
    tagsIndexBuilding = true;
    try {
        const res = await fetch('/api/pages/tags-index');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        tagsIndex = {};
        allKnownTags = new Map();
        for (const [p, list] of Object.entries(data || {})) {
            const normList = Array.isArray(list) ? list.map(normalizeTag) : [];
            tagsIndex[p] = normList;
            for (const t of normList) {
                if (t && !allKnownTags.has(t)) allKnownTags.set(t, t);
            }
        }
    } catch (err) {
        console.error('Failed to load tags index from server', err);
        // keep existing tagsIndex as-is on failure
    } finally {
        tagsIndexBuilding = false;
    }
}

function filterTreeByTags(nodes, requiredTags) {
    // returns new pruned tree
    const req = (requiredTags || []).map(normalizeTag);
    function matches(path) {
        const tags = tagsIndex[path] || [];
        return req.every(t => tags.includes(t));
    }
    function recur(list) {
        const out = [];
        for (const node of list) {
            if (node.type === 'file') {
                if (!req.length || matches(node.path)) out.push(node);
            } else {
                const children = node.children ? recur(node.children) : [];
                if (children.length) {
                    out.push({ ...node, children });
                }
            }
        }
        return out;
    }
    return recur(nodes);
}

// Note: No client-side sorting. The backend returns the structure already sorted.

function loadMenu() {
    const params = new URLSearchParams();
    params.set('sorted', 'true');
    params.set('dirsFirst', 'true');
    if (Array.isArray(tagFilter) && tagFilter.length > 0) {
        const csv = tagFilter.map(normalizeTag).filter(Boolean).join(',');
        if (csv) params.set('tags', csv);
    }
    return fetch(`/api/pages/structure?${params.toString()}`)
        .then(res => res.json())
        .then(async data => {
            window.__lastStructure = data;
            // build tags index in background if empty
            if (Object.keys(tagsIndex).length === 0 && !tagsIndexBuilding) {
                buildTagsIndexFromStructure(data).catch(() => {});
            }
            menu.innerHTML = `<div id=\"root-dropzone\" class=\"root-dropzone\" aria-label=\"Pages\"></div><div id=\"menu-tree\" class=\"menu-tree\">${renderTree(data)}</div>` + renderTagFilterBar();
            attachTagFilterEvents();
            try { attachRootDropzoneEvents(); } catch {}
            return data;
        })
        .catch(error => {
            console.error('Error loading menu:', error);
            // Fallback: attempt legacy fetch without server sort/filter
            return fetch('/api/pages/structure')
                .then(r => r.json())
                .then(fallbackData => {
                    window.__lastStructure = fallbackData;
                    const tree = (tagFilter.length && Object.keys(tagsIndex).length) ? filterTreeByTags(fallbackData, tagFilter) : fallbackData;
                    // Rely on backend ordering even in fallback; do not sort on client
                    menu.innerHTML = `<div id=\"root-dropzone\" class=\"root-dropzone\" aria-label=\"Pages\"></div><div id=\"menu-tree\" class=\"menu-tree\">${renderTree(tree)}</div>` + renderTagFilterBar();
                    attachTagFilterEvents();
                    try { attachRootDropzoneEvents(); } catch {}
                    showToast && showToast('Using fallback menu rendering', 'warning');
                    return fallbackData;
                })
                .catch(err2 => {
                    console.error('Fallback menu load failed:', err2);
                    showToast && showToast('Failed to load menu', 'error');
                });
        });
}

// Persisted expand state helpers
function getExpandedState() {
    try {
        const raw = localStorage.getItem('menuExpanded');
        return raw ? JSON.parse(raw) : {};
    } catch { return {}; }
}
function setExpandedState(state) {
    localStorage.setItem('menuExpanded', JSON.stringify(state));
}
function isExpanded(path, depth) {
    const state = getExpandedState();
    if (Object.prototype.hasOwnProperty.call(state, path)) return !!state[path];
    // Default: expand root level (depth === 0), collapse deeper levels
    return depth === 0;
}

// Ensure the folder chain for a path is expanded
function expandAncestorsForPath(path, isDirectory) {
    // Determine the folder path to expand
    let folderPath = isDirectory ? path : (path.includes('/') ? path.substring(0, path.lastIndexOf('/')) : '');
    if (!folderPath) return;
    const parts = folderPath.split('/').filter(Boolean);
    const state = getExpandedState();
    let acc = '';
    for (let i = 0; i < parts.length; i++) {
        acc = i === 0 ? parts[0] : acc + '/' + parts[i];
        state[acc] = true;
    }
    setExpandedState(state);
}

// Helper to locate a node by path in the tree structure
function findNodeByPath(nodes, targetPath) {
    for (const node of nodes) {
        if (node.path === targetPath) return node;
        if (node.children) {
            const found = findNodeByPath(node.children, targetPath);
            if (found) return found;
        }
    }
    return null;
}

// Returns true if the folder has any children (files or subfolders)
async function isFolderNonEmpty(path) {
    try {
        const res = await fetch('/api/pages/structure');
        if (!res.ok) return false;
        const data = await res.json();
        const node = findNodeByPath(data, path);
        if (!node) return false;
        return Array.isArray(node.children) && node.children.length > 0;
    } catch {
        return false;
    }
}

function renderTree(nodes, depth = 0) {
    return '<ul class="tree-level" data-depth="' + depth + '">' + nodes.map(node => {
        const isFolder = node.type !== 'file';
        const expanded = isFolder ? isExpanded(node.path, depth) : false;
        const hasChildren = !!(node.children && node.children.length);
        const isSelected = isFolder ? (node.path === selectedFolderPath) : (!selectedFolderPath && node.path === currentPath);
        const liClasses = [isFolder ? 'folder' : 'file', expanded && isFolder ? 'expanded' : 'collapsed'].filter(Boolean).join(' ');
        const caret = isFolder ? `<span class="caret" data-toggle="${node.path}" aria-label="Toggle" role="button">${expanded ? '▼' : '▶'}</span>` : '';
        const icon = isFolder ? '/assets/folder-outline.svg' : '/assets/file-outline.svg';
        const item = isFolder ?
            `<div class="menu-item folder-item ${isSelected ? 'selected' : ''}" data-folder-path="${node.path}">
                ${caret}
                <span class="folder-name"><img src="${icon}" class="icon" /> ${node.name}</span>
            </div>` :
            `<div class="menu-item ${isSelected ? 'selected' : ''}">
                ${caret}
                <a href="#" class="file-link"><img src="${icon}" class="icon" /> ${node.name}</a>
            </div>`;
        const children = isFolder && hasChildren ? renderTree(node.children, depth + 1) : (isFolder ? '<ul class="tree-level empty" data-depth="' + (depth + 1) + '"></ul>' : '');
        return `
        <li class="${liClasses}" data-path="${node.path}" data-type="${node.type}" draggable="true">
            ${item}
            ${children}
        </li>`;
    }).join('') + '</ul>';
}

// Drag and drop to move files and folders
let __dragSource = null;
let __dragSourceType = null;

menu.addEventListener('dragstart', function(e){
    const li = e.target.closest('li[data-path]');
    if (!li) return;
    __dragSource = li.getAttribute('data-path');
    __dragSourceType = li.getAttribute('data-type');
    try { e.dataTransfer.setData('text/plain', __dragSource); } catch {}
});

function getDropTargetDirPath(targetLi){
    if (!targetLi) return '';
    const type = targetLi.getAttribute('data-type');
    const path = targetLi.getAttribute('data-path') || '';
    if (type === 'directory') return path;
    // if file, use its parent directory
    const last = path.lastIndexOf('/');
    return last >= 0 ? path.substring(0, last) : '';
}

function isValidMove(sourcePath, sourceType, destDir){
    if (!sourcePath || destDir === null || destDir === undefined) return false;
    // cannot move into same folder
    const srcParent = sourcePath.includes('/') ? sourcePath.substring(0, sourcePath.lastIndexOf('/')) : '';
    if (srcParent === destDir) return false;
    // folders: prevent moving into itself or descendant
    if (sourceType !== 'file'){
        if (destDir === sourcePath) return false;
        if (destDir.startsWith(sourcePath + '/')) return false;
    }
    return true;
}

// Attach handlers for dedicated root drop zone
function attachRootDropzoneEvents(){
    const zone = document.getElementById('root-dropzone');
    if (!zone) return;
    const destDir = '';
    function canDrop(){
        return !!__dragSource && isValidMove(__dragSource, __dragSourceType, destDir);
    }
    zone.addEventListener('dragenter', function(e){
        if (!canDrop()) return;
        e.preventDefault();
        zone.classList.add('drag-over');
    });
    zone.addEventListener('dragover', function(e){
        if (!canDrop()) return;
        e.preventDefault();
        zone.classList.add('drag-over');
    });
    zone.addEventListener('dragleave', function(){
        zone.classList.remove('drag-over');
    });
    zone.addEventListener('drop', async function(e){
        e.preventDefault();
        const src = __dragSource;
        const srcType = __dragSourceType;
        __dragSource = null; __dragSourceType = null;
        zone.classList.remove('drag-over');
        if (!src) return;
        if (!isValidMove(src, srcType, destDir)) return;
        try {
            const resp = await fetch(`/api/pages/move?source=${encodeURIComponent(src)}&destination=${encodeURIComponent(destDir)}`, { method: 'POST' });
            if (!resp.ok) { throw new Error(`HTTP ${resp.status}`); }
            const data = await resp.json().catch(()=>({}));
            const newPath = data && data.path ? data.path : null;
            const movedType = data && data.type ? data.type : srcType;
            if (movedType === 'file'){
                if (currentPath === src) {
                    currentPath = newPath || (src.split('/').pop()||'');
                }
                try { if (tagsIndex && tagsIndex[src]) { tagsIndex[currentPath] = tagsIndex[src]; delete tagsIndex[src]; } } catch {}
                try { expandAncestorsForPath(currentPath, false); } catch {}
            } else {
                if (selectedFolderPath && (selectedFolderPath === src || selectedFolderPath.startsWith(src + '/'))){
                    selectedFolderPath = '';
                }
            }
            // Reload menu to reflect changes
            try { await loadMenu(false, true); } catch {}
        } catch (err){
            console.error('Move to root failed', err);
            showToast && showToast('Move failed', 'error');
        }
    });
}

menu.addEventListener('dragover', function(e){
    const li = e.target.closest('li[data-path]');
    const destDir = li ? getDropTargetDirPath(li) : '';
    if (!__dragSource) return;
    if (!isValidMove(__dragSource, __dragSourceType, destDir)) return;
    e.preventDefault();
    if (li) li.classList.add('drag-over');
});

menu.addEventListener('dragleave', function(e){
    const li = e.target.closest('li[data-path]');
    if (!li) return;
    li.classList.remove('drag-over');
});

menu.addEventListener('drop', async function(e){
    const li = e.target.closest('li[data-path]');
    e.preventDefault();
    const destDir = li ? getDropTargetDirPath(li) : '';
    const src = __dragSource;
    const srcType = __dragSourceType;
    __dragSource = null; __dragSourceType = null;
    // Clear highlights
    try { document.querySelectorAll('#menu li.drag-over').forEach(el=>el.classList.remove('drag-over')); } catch {}
    try { const rz = document.getElementById('root-dropzone'); if (rz) rz.classList.remove('drag-over'); } catch {}
    if (!src) return;
    if (!isValidMove(src, srcType, destDir)) return;
    try {
        const resp = await fetch(`/api/pages/move?source=${encodeURIComponent(src)}&destination=${encodeURIComponent(destDir)}`, { method: 'POST' });
        if (!resp.ok) { throw new Error(`HTTP ${resp.status}`); }
        const data = await resp.json().catch(()=>({}));
        const newPath = data && data.path ? data.path : null;
        const movedType = data && data.type ? data.type : srcType;
        if (movedType === 'file'){
            // Update currentPath if moved file was open
            if (currentPath === src) {
                currentPath = newPath || (destDir ? (destDir + '/' + (src.split('/').pop()||'')) : (src.split('/').pop()||''));
            }
            // Update tagsIndex key
            try {
                if (tagsIndex && tagsIndex[src]) { tagsIndex[currentPath] = tagsIndex[src]; delete tagsIndex[src]; }
            } catch {}
            try { expandAncestorsForPath(currentPath, false); } catch {}
        } else {
            // Folder moved: update selections
            if (selectedFolderPath && (selectedFolderPath === src || selectedFolderPath.startsWith(src + '/'))){
                if (newPath) selectedFolderPath = newPath; else selectedFolderPath = destDir ? (destDir + '/' + (src.split('/').pop()||'')) : (src.split('/').pop()||'');
                currentFolderPath = selectedFolderPath;
            }
            if (currentPath && currentPath.startsWith(src + '/')){
                const suffix = currentPath.substring(src.length);
                currentPath = (newPath || (destDir + '/' + (src.split('/').pop()||''))) + suffix;
            }
            try { expandAncestorsForPath(selectedFolderPath, true); } catch {}
        }
        await loadMenu();
        updateToolbar();
        showToast('Moved successfully', 'success');
    } catch (err) {
        console.error('Move failed', err);
        showToast(`Move failed: ${err.message}`, 'error');
    }
});

menu.addEventListener('click', function(e) {
    // Toggle folder expand/collapse via caret or clicking folder row (except clicking actions/links)
    const caret = e.target.closest('.caret');
    if (caret && !caret.classList.contains('placeholder')) {
        const li = caret.closest('li');
        const path = caret.getAttribute('data-toggle');
        const expanded = li.classList.contains('expanded');
        const state = getExpandedState();
        state[path] = !expanded;
        setExpandedState(state);
        // Re-render to apply state across tree
        loadMenu();
        return;
    }

    // While in edit mode, disable selecting/loading nodes in the tree (but still allow caret toggling above)
    if (isEditMode) {
        const clickable = e.target.closest('.file-link, .folder-item');
        if (clickable) {
            e.preventDefault();
            return;
        }
    }

    if (e.target.classList.contains('file-link') || e.target.closest('.file-link')) {
        e.preventDefault();
        const link = e.target.classList.contains('file-link') ? e.target : e.target.closest('.file-link');
        const li = link.closest('li');
        const path = li.dataset.path;
        loadFile(path);
        return;
    }

    if (e.target.classList.contains('folder-item') || e.target.closest('.folder-item')) {
        const folderItem = e.target.classList.contains('folder-item') ? e.target : e.target.closest('.folder-item');
        currentFolderPath = folderItem.dataset.folderPath;
        selectedFolderPath = currentFolderPath;
        // Re-render to show selected state on the clicked folder
        loadMenu();
        updateToolbar();
    }
});

function createEntity(createPath, type) {
    const displayEntity = type === 'file' ? 'Document' : 'Node';

    fetch(`/api/pages/create?path=${encodeURIComponent(createPath)}&type=${type}`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
        },
        body: JSON.stringify({ name: createPath.split('/').pop(), type: type })
    })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            // Ensure the parent folder chain is expanded so the new item is visible
            expandAncestorsForPath(createPath, type !== 'file');

            if (type === 'file') {
                showToast(`${displayEntity} created successfully`, 'success');
                // Initialize tags index for new file with empty tag list
                if (createPath) {
                    tagsIndex[createPath] = [];
                }
                // Loading the file will also refresh the menu (and keep it expanded)
                // Then automatically enter edit mode for the new page
                loadFile(createPath).then(() => {
                    // Trigger the same logic as clicking the Edit button
                    if (btnEdit) btnEdit.click();
                });
            } else {
                // For a new folder (node), expand and re-render the menu
                return loadMenu().then(() => {
                    showToast(`${displayEntity} created successfully`, 'success');
                });
            }
        })
        .catch(error => {
            console.error('Error creating entity:', error);
            showToast(`Failed to create ${displayEntity}: ${error.message}`, 'error');
        });
}

function deleteEntity(name, type, path) {
    fetch(`/api/pages/delete?path=${encodeURIComponent(path)}`, {
        method: 'DELETE',
    })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }

            if (path === currentPath) {
                currentPath = '';
                currentContent = DEFAULT_WELCOME;
                viewer.setMarkdown(currentContent);

                if (isEditMode) {
                    cancelEdit();
                }

                updateToolbar();
            }

            // remove from tags index
            try { if (path) { delete tagsIndex[path]; } } catch {}
            loadMenu();
            showToast('File deleted successfully', 'success');
        })
        .catch(error => {
            console.error('Error deleting entity:', error);
            showToast(`Failed to delete file: ${error.message}`, 'error');
        });
}

async function loadFile(path) {
    if (hasUnsavedChanges()) {
        const save = await showModal('Unsaved Changes', 'Do you want to save your changes?', false);
        if (save === true) {
            await saveFile();
        } else if (save === null) {
            return;
        }
        cancelEdit();
    } else if (isEditMode) {
        cancelEdit();
    }

    currentPath = path;
    viewer.setMarkdown('Loading...');

    try {
        const response = await fetch(`/api/pages/content?path=${encodeURIComponent(path)}&includeMeta=true`);
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }
        const contentType = (response.headers.get('content-type') || '').toLowerCase();
        let content = '';
        if (contentType.includes('application/json')) {
            const data = await response.json();
            content = data && typeof data.content === 'string' ? data.content : '';
            // Store server-provided breadcrumbs for renderBreadcrumbs()
            serverBreadcrumbs = Array.isArray(data && data.breadcrumbs) ? data.breadcrumbs : null;
        } else {
            content = await response.text();
            serverBreadcrumbs = null;
        }
        currentContent = content;
        viewer.setMarkdown(content);

        // Set base folder to the parent directory of the selected file
        const lastSlash = currentPath.lastIndexOf('/');
        currentFolderPath = lastSlash >= 0 ? currentPath.substring(0, lastSlash) : '';
        // Clear explicit folder selection so the file remains highlighted
        selectedFolderPath = '';

        updateToolbar();
        loadMenu();
    } catch (error) {
        viewer.setMarkdown('Error loading file');
        console.error('Error loading file:', error);
        showToast(`Failed to load file: ${error.message}`, 'error');
    }
}

async function saveFile() {
    if (!currentPath) {
        showToast('No file selected', 'warning');
        return;
    }

    if (!isEditMode || !editor) {
        showToast('Not in edit mode', 'warning');
        return;
    }

    try {
        let content = editor.getMarkdown();
        // Collect tags from input but let the server merge them into the markdown
        let tagsParam = '';
        let tagsArray = [];
        if (tagsInput) {
            tagsArray = tagsInput.value.split(',').map(t => t.trim()).filter(Boolean);
            tagsParam = `&tags=${encodeURIComponent(tagsArray.join(', '))}`;
        }

        const response = await fetch(`/api/pages/save?path=${encodeURIComponent(currentPath)}${tagsParam}`, {
            method: 'POST',
            headers: { 'Content-Type': 'text/plain' },
            body: content
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        const savedContent = await response.text();
        showToast('File saved successfully!', 'success');

        // Update tags index for this file using the tags we just sent (or refetch from meta)
        try {
            let updatedOrigTags = tagsArray;
            if (!updatedOrigTags || updatedOrigTags.length === 0) {
                updatedOrigTags = await fetchMetaTags(currentPath);
            }
            const updatedNormTags = (updatedOrigTags || []).map(normalizeTag);
            tagsIndex[currentPath] = updatedNormTags;
            // Update known tags map with original-cased labels (add new ones)
            for (let i = 0; i < updatedNormTags.length; i++) {
                const norm = updatedNormTags[i];
                const display = updatedOrigTags[i];
                if (norm && display && !allKnownTags.has(norm)) {
                    allKnownTags.set(norm, display);
                }
            }
            // Prune tags that are no longer used by any file
            const present = new Set();
            for (const p in tagsIndex) {
                const list = tagsIndex[p] || [];
                for (const t of list) present.add(t);
            }
            // Remove non-present tags from the active filter selection
            tagFilter = (tagFilter || []).filter(t => present.has(t));
            // Drop unused tags from the known tags map so filter chips update
            for (const key of Array.from(allKnownTags.keys())) {
                if (!present.has(key)) allKnownTags.delete(key);
            }
        } catch {}

        currentContent = savedContent;
        originalContent = savedContent;

        editor.destroy();
        editor = null;
        const editorEdit = document.getElementById('editor-edit');
        if (editorEdit) editorEdit.remove();

        document.querySelector('#editor').style.display = 'block';
        viewer.setMarkdown(savedContent);

        isEditMode = false;
        updateToolbar();

        // Re-render the menu filter bar so new/changed tags appear immediately
        try {
            if (window.__lastStructure) {
                const data = window.__lastStructure;
                const tree = (tagFilter.length && Object.keys(tagsIndex).length)
                    ? filterTreeByTags(data, tagFilter)
                    : data;
                // Rely on server-side sorting; no client-side sorting applied
                menu.innerHTML = `<div id=\"menu-tree\" class=\"menu-tree\">${renderTree(tree)}</div>` + renderTagFilterBar();
                attachTagFilterEvents();
            } else {
                // Fallback: reload menu if cached structure is missing
                loadMenu();
            }
        } catch {}

    } catch (error) {
        console.error('Error saving file:', error);
        showToast(`Failed to save file: ${error.message}`, 'error');
    }
}

window.addEventListener('beforeunload', function(e) {
    if (hasUnsavedChanges()) {
        e.preventDefault();
        e.returnValue = '';
        return '';
    }
});

initDefaultPage();
updateToolbar();
loadMenu();
// Search UI and logic
(function(){
    const searchInput = document.getElementById('search-input');
    const searchButton = document.getElementById('search-button');
    const searchResults = document.getElementById('search-results');
    if (!searchInput || !searchButton) return;

    let outsideClickHandler = null;
    let escapeHandler = null;

    function showAnchoredResults(q, items){
        if (!searchResults) return;
        const html = ['<ul>',
            ...items.map(it => {
                const title = (it.title || it.path || '').toString();
                const path = (it.path || '').toString();
                const snippet = (it.snippet || '').toString();
                return `<li data-path="${path}">`+
                       `<div class="result-title">${escapeHtml(title)}</div>`+
                       `<div class="result-path">${escapeHtml(path)}</div>`+
                       (snippet ? `<div class="result-snippet">${escapeHtml(snippet)}</div>` : '')+
                       `</li>`;
            }),
            '</ul>'].join('');
        searchResults.innerHTML = html;
        searchResults.hidden = false;
        searchResults.classList.remove('hidden');

        // Attach handlers
        searchResults.addEventListener('click', onResultClick);
        outsideClickHandler = (ev)=>{
            const within = ev.target.closest('#search-results') || ev.target.closest('.title-search');
            if (!within) closeAnchoredResults();
        };
        escapeHandler = (ev)=>{ if (ev.key === 'Escape') closeAnchoredResults(); };
        window.addEventListener('click', outsideClickHandler, { capture: true });
        window.addEventListener('keydown', escapeHandler);
    }

    function closeAnchoredResults(){
        if (!searchResults) return;
        searchResults.hidden = true;
        searchResults.classList.add('hidden');
        searchResults.innerHTML = '';
        searchResults.removeEventListener('click', onResultClick);
        if (outsideClickHandler) window.removeEventListener('click', outsideClickHandler, { capture: true });
        if (escapeHandler) window.removeEventListener('keydown', escapeHandler);
        outsideClickHandler = null;
        escapeHandler = null;
    }

    async function performSearch() {
        const q = (searchInput.value || '').trim();
        if (q.length < 2) {
            showToast('Please enter at least 2 characters to search.', 'info');
            return;
        }
        try {
            const res = await fetch(`/api/pages/search?query=${encodeURIComponent(q)}`);
            if (!res.ok) {
                showToast(`Search failed (${res.status})`, 'error');
                return;
            }
            const items = await res.json();
            if (!Array.isArray(items) || items.length === 0) {
                // Show an empty state anchored panel
                showAnchoredResults(q, [{ title: 'No results', path: '', snippet: `No matches for "${q}".` }]);
                return;
            }
            showAnchoredResults(q, items);
        } catch (e) {
            console.error('Search error', e);
            showToast('Search error. See console for details.', 'error');
        }
    }

    function onResultClick(e) {
        const li = e.target.closest('li[data-path]');
        if (!li) return;
        const path = li.getAttribute('data-path');
        if (!path) { return; }
        // Expand ancestors in the tree and load the file
        try { expandAncestorsForPath(path, false); } catch {}
        loadMenu();
        closeAnchoredResults();
        loadFile(path);
    }

    function escapeHtml(str){
        return str.replace(/[&<>"']/g, s => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[s]));
    }

    searchButton.addEventListener('click', performSearch);
    searchInput.addEventListener('keydown', (e)=>{ if(e.key==='Enter'){ performSearch(); }});
})();


// Settings UI logic
(function(){
    const settingsBtn = document.getElementById('settings-btn');
    const settingsOverlay = document.getElementById('settings-overlay');
    const settingsCloseBtn = document.getElementById('settings-close-btn');
    const settingsCancelBtn = document.getElementById('settings-cancel-btn');
    const settingsSaveBtn = document.getElementById('settings-save-btn');

    const inputAllowedHosts = document.getElementById('settings-allowed-hosts');
    const inputPagesDir = document.getElementById('settings-pages-dir');
    const inputBackupDir = document.getElementById('settings-backup-dir');

    let originalSettings = null;

    function openSettings() {
        settingsOverlay && settingsOverlay.classList.add('active');
    }
    function closeSettings() {
        settingsOverlay && settingsOverlay.classList.remove('active');
    }

    async function loadSettings() {
        try {
            const res = await fetch('/api/settings');
            if (!res.ok) throw new Error('Failed to load settings');
            const data = await res.json();
            originalSettings = data;
            if (inputAllowedHosts) inputAllowedHosts.value = data.allowedHosts ?? data.AllowedHosts ?? '';
            if (inputPagesDir) inputPagesDir.value = data.pagesDirectory ?? data.PagesDirectory ?? '';
            if (inputBackupDir) inputBackupDir.value = data.backupDirectory ?? data.BackupDirectory ?? '';
        } catch (err) {
            showToast && showToast('Failed to load settings', 'error');
        }
    }

    async function saveSettings() {
        const payload = {
            allowedHosts: inputAllowedHosts ? inputAllowedHosts.value : undefined,
            pagesDirectory: inputPagesDir ? inputPagesDir.value : undefined,
            backupDirectory: inputBackupDir ? inputBackupDir.value : undefined,
        };
        try {
            const res = await fetch('/api/settings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            if (!res.ok) throw new Error('Save failed');
            const data = await res.json();
            showToast && showToast('Settings saved', 'success');
            // Warn about PagesDirectory change
            const oldPages = originalSettings?.pagesDirectory ?? originalSettings?.PagesDirectory ?? '';
            const newPages = data.pagesDirectory ?? data.PagesDirectory ?? '';
            if (oldPages !== newPages) {
                showToast && showToast('PagesDirectory changed. Restart the app to update static file serving.', 'warning');
            }
            closeSettings();
        } catch (err) {
            showToast && showToast('Failed to save settings', 'error');
        }
    }

    if (settingsBtn) {
        settingsBtn.addEventListener('click', async () => {
            await loadSettings();
            openSettings();
        });
    }
    if (settingsCloseBtn) settingsCloseBtn.addEventListener('click', closeSettings);
    if (settingsCancelBtn) settingsCancelBtn.addEventListener('click', closeSettings);
    if (settingsOverlay) settingsOverlay.addEventListener('click', (e) => {
        if (e.target === settingsOverlay) closeSettings();
    });
    if (settingsSaveBtn) settingsSaveBtn.addEventListener('click', saveSettings);
})();
