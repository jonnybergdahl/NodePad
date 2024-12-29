const menu = document.getElementById('menu');
const editButton = document.getElementById('edit');

const editor = new SimpleMDE({
    element: document.getElementById("editor"),
    autoDownloadFontAwesome: true,
});

document.querySelector('#edit').addEventListener('click', function() {
    let new_mode = !editor.codemirror.getOption('readOnly');
    editor.codemirror.setOption('readOnly', new_mode);
    editor.codemirror.setOption('toolbar', new_mode);
    editButton.disabled = !new_mode;
});

let currentPath = '';

function loadMenu() {
    fetch('/api/pages/structure')
        .then(res => res.json())
        .then(data => {
            menu.innerHTML = renderTree(data);
        });
}

function renderTree(nodes) {
    return '<ul>' + nodes.map(node => `
        <li class="${node.path === currentPath ? 'selected' : ''}">
        ${node.type === 'file' ?
        `<a href="#" onclick="loadFile('${node.path}')"><img src="/assets/file-outline.svg" class="icon" /> ${node.name}</a>` :
        `<img src="/assets/folder-outline.svg" class="icon" /> ${node.name}`}${node.children ? renderTree(node.children) : ''}
        </li>
    `).join('') + '</ul>';
}

function loadFile(path) {
    currentPath = path;
    fetch(`/api/pages/content?path=${encodeURIComponent(path)}`)
        .then(res => res.text())
        // .then(content => editor.value(content));
        .then(content => editor.value(content));
    editor.codemirror.setOption('readOnly', true);
    editButton.disabled = false;
    loadMenu();
}

function saveFile() {
    fetch(`/api/pages/save?path=${encodeURIComponent(currentPath)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain' },
        // body: editor.value()
        body: editor.value()
    })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            return response;
        })        
        .then(() => alert('File saved successfully!'))
        .catch(err => console.error('Error saving file:', err));
    editor.codemirror.setOption('readOnly', true);
    editor.codemirror.setOption('toolbar', false);
    editButton.disabled = false;
}

loadMenu();