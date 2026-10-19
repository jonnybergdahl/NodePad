// ToDo lists page, opened from the ToDo entry at the top of the menu.
// Loaded before app.js; uses its helpers (showModal, showToast, hasUnsavedChanges, saveFile, cancelEdit,
// loadMenu, setUrl, TODO_HASH) only when the user acts, after both scripts have loaded.

const todoNav = document.getElementById('todo-nav');
const todoView = document.getElementById('todo-view');
// The note UI that the ToDo page replaces while it is open
const TODO_REPLACES = ['.toolbar', '#breadcrumbs', '#tags-bar', '#editor'];

let todoViewActive = false;
let todoLists = [];
let todoShowOld = false;
try { todoShowOld = localStorage.getItem('todoShowOld') === 'true'; } catch { /* storage unavailable */ }

function todoEscape(str) {
    return String(str).replace(/[&<>"']/g, s => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\'': '&#39;' }[s]));
}

async function showTodoView() {
    // Same unsaved-changes handling as opening another note
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

    todoViewActive = true;
    if (location.hash !== TODO_HASH) setUrl(TODO_HASH);
    TODO_REPLACES.forEach(sel => document.querySelector(sel)?.classList.add('hidden'));
    todoView.hidden = false;
    todoNav.classList.add('selected');
    todoNav.setAttribute('aria-current', 'page');
    document.querySelectorAll('#menu .menu-item.selected').forEach(el => el.classList.remove('selected'));
    await loadTodos();
}

// Called by app.js when a note or folder is opened
function hideTodoView() {
    if (!todoViewActive) return;
    todoViewActive = false;
    TODO_REPLACES.forEach(sel => document.querySelector(sel)?.classList.remove('hidden'));
    todoView.hidden = true;
    todoNav.classList.remove('selected');
    todoNav.removeAttribute('aria-current');
}

// ---------------------------------------------------------------------
// Server calls
// ---------------------------------------------------------------------

async function todoRequest(method, url, body) {
    const res = await fetch(url, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    if (!res.ok) throw new Error(text || `HTTP ${res.status}`);
    return text ? JSON.parse(text) : null;
}

function todoOldParam(prefix) {
    return todoShowOld ? `${prefix}includeOldDone=true` : '';
}

async function loadTodos() {
    try {
        todoLists = await todoRequest('GET', `/api/todos${todoOldParam('?')}`);
    } catch (err) {
        showToast(`Could not load to do lists: ${err.message}`, 'error');
        todoLists = [];
    }
    renderTodos();
}

// Runs a change and puts the returned list in place of the old one
async function todoChange(method, url, body, { focusAdd } = {}) {
    try {
        const list = await todoRequest(method, url, body);
        todoLists = todoLists.filter(l => l.name.toLowerCase() !== String(list?.name).toLowerCase());
        if (list) todoLists.push(list);
        todoLists.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
        renderTodos(focusAdd ? list?.name : null);
    } catch (err) {
        showToast(err.message, 'error');
        await loadTodos();
    }
}

const listQuery = name => `list=${encodeURIComponent(name)}`;

// ---------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------

function todoDateLabel(isoDate) {
    if (!isoDate) return '';
    const date = new Date(`${isoDate}T00:00:00`);
    const options = { day: 'numeric', month: 'short' };
    if (date.getFullYear() !== new Date().getFullYear()) options.year = 'numeric';
    return `✓ ${date.toLocaleDateString(undefined, options)}`;
}

function renderTodoItem(item) {
    return `
        <li class="todo-item${item.done ? ' done' : ''}" data-index="${item.index}">
            <input type="checkbox" class="todo-check" ${item.done ? 'checked' : ''} aria-label="Done: ${todoEscape(item.text)}">
            <span class="todo-text" tabindex="0" title="Click to edit">${todoEscape(item.text)}</span>
            ${item.done && item.doneDate ? `<span class="todo-date" title="Done ${todoEscape(item.doneDate)}">${todoDateLabel(item.doneDate)}</span>` : ''}
            <button class="icon-button todo-delete" data-action="delete-item" title="Delete item" aria-label="Delete item">
                <span class="material-symbols-rounded">close</span>
            </button>
        </li>`;
}

function renderTodoCard(list) {
    const items = list.items.length
        ? `<ul class="todo-items">${list.items.map(renderTodoItem).join('')}</ul>`
        : '<p class="todo-empty">No items yet</p>';
    return `
        <section class="todo-card" data-list="${todoEscape(list.name)}">
            <header class="todo-card-header">
                <h2 class="todo-card-title">${todoEscape(list.name)}</h2>
            </header>
            ${items}
            <div class="todo-add">
                <span class="material-symbols-rounded" aria-hidden="true">add</span>
                <input type="text" class="todo-add-input" placeholder="Add item" aria-label="Add item to ${todoEscape(list.name)}">
            </div>
        </section>`;
}

function renderTodos(focusAddForList) {
    const hidden = todoLists.reduce((sum, l) => sum + (l.hiddenDoneCount || 0), 0);
    const hiddenNote = !todoShowOld && hidden > 0 ? ` <span class="todo-hidden-count">(${hidden} hidden)</span>` : '';
    const body = todoLists.length
        ? `<div class="todo-grid">${todoLists.map(renderTodoCard).join('')}</div>`
        : `<div class="todo-empty-state">
               <span class="material-symbols-rounded" aria-hidden="true">checklist</span>
               <p>No to do lists yet. Create one on the "To do lists" tab in Settings.</p>
           </div>`;

    todoView.innerHTML = `
        <div class="todo-header">
            <h1 class="todo-title">To do</h1>
            <label class="todo-toggle">
                <input type="checkbox" id="todo-show-old" ${todoShowOld ? 'checked' : ''}>
                Show older done items${hiddenNote}
            </label>
        </div>
        ${body}`;

    if (focusAddForList) {
        const card = [...todoView.querySelectorAll('.todo-card')].find(c => c.dataset.list === focusAddForList);
        card?.querySelector('.todo-add-input')?.focus();
    }
}

// ---------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------

// ---------------------------------------------------------------------
// List management (Settings, "To do lists" tab)
// ---------------------------------------------------------------------

const settingsTodoLists = document.getElementById('settings-todo-lists');
const settingsTodoName = document.getElementById('settings-todo-name');
const settingsTodoAddBtn = document.getElementById('settings-todo-add-btn');

// Called by app.js when the ToDo lists tab in Settings is shown
async function loadSettingsTodoLists() {
    let lists = [];
    try {
        lists = await todoRequest('GET', '/api/todos');
    } catch (err) {
        showToast(`Could not load to do lists: ${err.message}`, 'error');
    }
    settingsTodoLists.innerHTML = lists.length
        ? lists.map(list => {
            const open = list.items.filter(i => !i.done).length;
            return `
                <li class="settings-todo-list" data-list="${todoEscape(list.name)}">
                    <span class="material-symbols-rounded" aria-hidden="true">checklist</span>
                    <span class="settings-todo-name">${todoEscape(list.name)}</span>
                    <span class="settings-todo-count">${open} open</span>
                    <button class="icon-button" data-action="rename-list" title="Rename list" aria-label="Rename ${todoEscape(list.name)}">
                        <span class="material-symbols-rounded">edit</span>
                    </button>
                    <button class="icon-button danger" data-action="delete-list" title="Delete list" aria-label="Delete ${todoEscape(list.name)}">
                        <span class="material-symbols-rounded">delete</span>
                    </button>
                </li>`;
        }).join('')
        : '<li class="settings-todo-none">No to do lists yet.</li>';
}

// Keeps the ToDo page in step with changes made in Settings
async function afterTodoListChange() {
    await loadSettingsTodoLists();
    if (todoViewActive) await loadTodos();
}

async function addTodoList() {
    const name = settingsTodoName.value.trim();
    if (!name) {
        settingsTodoName.focus();
        return;
    }
    try {
        const list = await todoRequest('POST', '/api/todos/lists', { name });
        settingsTodoName.value = '';
        showToast(`List "${list.name}" created`, 'success');
        await afterTodoListChange();
    } catch (err) {
        showToast(err.message, 'error');
    }
    settingsTodoName.focus();
}

async function renameTodoList(name) {
    const newName = await showModal('Rename list', 'New name for the list:', true, name);
    if (!newName || !newName.trim() || newName.trim() === name) return;
    try {
        await todoRequest('POST', `/api/todos/lists/rename?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName.trim())}`);
        await afterTodoListChange();
    } catch (err) {
        showToast(err.message, 'error');
    }
}

async function deleteTodoList(name) {
    const confirmed = await showModal('Delete list', `Delete the list "${name}" and all its items?`, false);
    if (confirmed !== true) return;
    try {
        await todoRequest('DELETE', `/api/todos/lists?name=${encodeURIComponent(name)}`);
        showToast('List deleted', 'success');
        await afterTodoListChange();
    } catch (err) {
        showToast(err.message, 'error');
    }
}

settingsTodoAddBtn.addEventListener('click', addTodoList);
settingsTodoName.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
        e.preventDefault();
        addTodoList();
    }
});
settingsTodoLists.addEventListener('click', e => {
    const action = e.target.closest('[data-action]')?.dataset.action;
    const list = e.target.closest('.settings-todo-list')?.dataset.list;
    if (action === 'rename-list') renameTodoList(list);
    else if (action === 'delete-list') deleteTodoList(list);
});

// ---------------------------------------------------------------------
// Item editing on the ToDo page
// ---------------------------------------------------------------------

function startEditTodoText(textEl) {
    const card = textEl.closest('.todo-card');
    const item = textEl.closest('.todo-item');
    const original = textEl.textContent;
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'todo-edit';
    input.value = original;
    textEl.replaceWith(input);
    input.focus();
    input.select();

    let finished = false;
    const finish = async (save) => {
        if (finished) return;
        finished = true;
        const text = input.value.trim();
        if (save && text && text !== original) {
            await todoChange('PUT', `/api/todos/items?${listQuery(card.dataset.list)}&index=${item.dataset.index}${todoOldParam('&')}`, { text });
        } else {
            renderTodos();
        }
    };
    input.addEventListener('keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); finish(true); }
        else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
    });
    input.addEventListener('blur', () => finish(true));
}

todoNav.addEventListener('click', e => {
    e.preventDefault();
    showTodoView();
});

todoView.addEventListener('click', e => {
    const action = e.target.closest('[data-action]')?.dataset.action;
    const card = e.target.closest('.todo-card');
    const list = card?.dataset.list;

    if (action === 'delete-item') {
        const index = e.target.closest('.todo-item').dataset.index;
        return todoChange('DELETE', `/api/todos/items?${listQuery(list)}&index=${index}${todoOldParam('&')}`);
    }
    if (e.target.classList.contains('todo-text')) startEditTodoText(e.target);
});

todoView.addEventListener('change', e => {
    if (e.target.id === 'todo-show-old') {
        todoShowOld = e.target.checked;
        try { localStorage.setItem('todoShowOld', String(todoShowOld)); } catch { /* storage unavailable */ }
        loadTodos();
        return;
    }
    if (e.target.classList.contains('todo-check')) {
        const list = e.target.closest('.todo-card').dataset.list;
        const index = e.target.closest('.todo-item').dataset.index;
        todoChange('PUT', `/api/todos/items?${listQuery(list)}&index=${index}${todoOldParam('&')}`, { done: e.target.checked });
    }
});

todoView.addEventListener('keydown', e => {
    if (e.key !== 'Enter') return;
    if (e.target.classList.contains('todo-add-input')) {
        const text = e.target.value.trim();
        if (!text) return;
        const list = e.target.closest('.todo-card').dataset.list;
        todoChange('POST', `/api/todos/items?${listQuery(list)}${todoOldParam('&')}`, { text }, { focusAdd: true });
    } else if (e.target.classList.contains('todo-text')) {
        // Keyboard access to inline editing
        e.preventDefault();
        startEditTodoText(e.target);
    }
});
