const menu = document.getElementById('menu');

// Toolbar buttons
const btnCreateFile = document.getElementById('btn-create-file');
const btnCreateFolder = document.getElementById('btn-create-folder');
const btnEdit = document.getElementById('btn-edit');
const btnSave = document.getElementById('btn-save');
const btnCancel = document.getElementById('btn-cancel');
const btnDelete = document.getElementById('btn-delete');

// Breadcrumbs
const breadcrumbsEl = document.getElementById('breadcrumbs');

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
    const saved = localStorage.getItem('theme') || 'system';
    applyTheme(saved);
    if (themeSelect) themeSelect.value = saved;
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
        editor = new toastui.Editor({
            el: editorContainer,
            initialEditType: 'markdown',
            previewStyle: 'vertical',
            height,
            initialValue: md,
            theme: mode === 'dark' ? 'dark' : 'default',
            plugins: [[toastui.Editor.plugin.codeSyntaxHighlight, { highlighter: Prism }]]
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

// Update if system preference changes while in system mode
prefersDarkQuery.addEventListener('change', () => {
    const current = localStorage.getItem('theme') || 'system';
    if (current === 'system') {
        applyTheme('system');
        applyToastUITheme();
    }
});

initTheme();

// Modal elements
const modalOverlay = document.getElementById('modal-overlay');
const modalTitle = document.getElementById('modal-title');
const modalMessage = document.getElementById('modal-message');
const modalInput = document.getElementById('modal-input');
const modalConfirmBtn = document.getElementById('modal-confirm-btn');
const modalCancelBtn = document.getElementById('modal-cancel-btn');
const modalCloseBtn = document.getElementById('modal-close-btn');

// Toast UI Viewer/Editor helpers with theme support
const DEFAULT_WELCOME = 'Welcome to NodePad!\n\nSelect a file from the menu or create a new one.';
let viewer = null;
function createViewer(initialMarkdown) {
    const mode = getEffectiveTheme();
    const el = document.querySelector('#editor');
    viewer = new toastui.Editor.factory({
        el,
        viewer: true,
        initialValue: typeof initialMarkdown === 'string' ? initialMarkdown : DEFAULT_WELCOME,
        theme: mode === 'dark' ? 'dark' : 'default',
        plugins: [[toastui.Editor.plugin.codeSyntaxHighlight, { highlighter: Prism }]]
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
    }
    // Keep breadcrumbs in sync with current selection
    renderBreadcrumbs();
}

btnCreateFile.addEventListener('click', async function() {
    if (hasUnsavedChanges()) {
        const save = await showModal('Unsaved Changes', 'Do you want to save your changes before creating a new file?', false);
        if (save === true) {
            await saveFile();
        }
    }

    const name = await showModal('Create New File', 'Enter the name of the file:', true);
    if (!name) return;

    const finalName = name.endsWith('.md') ? name : `${name}.md`;
    const createPath = currentFolderPath ? `${currentFolderPath}/${finalName}` : finalName;

    createEntity(createPath, 'file');
});

btnCreateFolder.addEventListener('click', async function() {
    const name = await showModal('Create New Folder', 'Enter the name of the folder:', true);
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
    const toolbar = document.querySelector('.toolbar');
    toolbar.parentNode.insertBefore(editorContainer, toolbar.nextSibling);

    // Skapa editor med syntax highlighting
    editor = new toastui.Editor({
        el: editorContainer,
        initialEditType: 'markdown',
        previewStyle: 'vertical',
        height: 'calc(100vh - 250px)',
        initialValue: currentContent,
        theme: getEffectiveTheme() === 'dark' ? 'dark' : 'default',
        plugins: [[toastui.Editor.plugin.codeSyntaxHighlight, { highlighter: Prism }]]
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
                    currentContent = 'Welcome to NodePad!\n\nSelect a file from the menu or create a new one.';
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
                        currentContent = 'Welcome to NodePad!\n\nSelect a file from the menu or create a new one.';
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
                        currentContent = 'Welcome to NodePad!\n\nSelect a file from the menu or create a new one.';
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

function loadMenu() {
    return fetch('/api/pages/structure')
        .then(res => res.json())
        .then(data => {
            menu.innerHTML = renderTree(data);
            return data;
        })
        .catch(error => {
            console.error('Error loading menu:', error);
            showToast('Failed to load menu', 'error');
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
        const caret = isFolder ? `<span class="caret" data-toggle="${node.path}" aria-label="Toggle" role="button">${expanded ? '▼' : '▶'}</span>` : '<span class="caret placeholder"></span>';
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
        <li class="${liClasses}" data-path="${node.path}" data-type="${node.type}">
            ${item}
            ${children}
        </li>`;
    }).join('') + '</ul>';
}

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
    const entityName = type === 'file' ? 'file' : 'folder';

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
                showToast(`${entityName.charAt(0).toUpperCase() + entityName.slice(1)} created successfully`, 'success');
                // Loading the file will also refresh the menu (and keep it expanded)
                loadFile(createPath);
            } else {
                // For a new folder, expand and re-render the menu
                return loadMenu().then(() => {
                    showToast(`${entityName.charAt(0).toUpperCase() + entityName.slice(1)} created successfully`, 'success');
                });
            }
        })
        .catch(error => {
            console.error('Error creating entity:', error);
            showToast(`Failed to create ${entityName}: ${error.message}`, 'error');
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
                currentContent = 'Welcome to NodePad!\n\nSelect a file from the menu or create a new one.';
                viewer.setMarkdown(currentContent);

                if (isEditMode) {
                    cancelEdit();
                }

                updateToolbar();
            }

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
        const response = await fetch(`/api/pages/content?path=${encodeURIComponent(path)}`);
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }
        const content = await response.text();
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
        const content = editor.getMarkdown();

        const response = await fetch(`/api/pages/save?path=${encodeURIComponent(currentPath)}`, {
            method: 'POST',
            headers: { 'Content-Type': 'text/plain' },
            body: content
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        showToast('File saved successfully!', 'success');

        currentContent = content;
        originalContent = content;

        editor.destroy();
        editor = null;
        const editorEdit = document.getElementById('editor-edit');
        if (editorEdit) editorEdit.remove();

        document.querySelector('#editor').style.display = 'block';
        viewer.setMarkdown(content);

        isEditMode = false;
        updateToolbar();

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