const menu = document.getElementById('menu');

const editor = new toastui.Editor({
    el: document.querySelector('#editor'),
    initialEditType: 'markdown',
    previewStyle: 'vertical',
    height: '80vh',
    viewer: true,
    hideModeSwitch: true
});

document.querySelector('#edit').addEventListener('click', function() {
    editor.viewer = !editor.viewer;
    let crap = editor.getEditorElements();
    let mdEditor = editor.mdEditor;
    mdEditor.el.style.display = editor.viewer ? 'none' : 'block';
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
        .then(content => editor.setMarkdown(content));
    loadMenu();
}

function saveFile() {
    fetch(`/api/pages/save?path=${encodeURIComponent(currentPath)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain' },
        // body: editor.value()
        body: editor.getMarkdown()
    })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            return response;
        })        
        .then(() => alert('File saved successfully!'))
        .catch(err => console.error('Error saving file:', err));
}

loadMenu();