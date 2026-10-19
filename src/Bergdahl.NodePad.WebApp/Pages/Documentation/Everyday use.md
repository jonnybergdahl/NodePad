# Everyday use

- Create notes and folders
  - Use "New Document" and "New Node" in the toolbar to add notes and folders. They are created in the selected folder, or in the folder of the open note.
  - Select a note or folder in the tree and use "Rename" or "Delete" in the toolbar. Deleting a folder that is not empty asks for confirmation first.
  - Drag notes and folders in the tree to move them. Drop on "Pages" at the top of the tree, or on the empty space below it, to move to the root folder.

- Edit and format
  - Click “Edit” to open the Markdown editor, with a live preview next to it. Click “Save” when you are done, or “Cancel” to discard your changes.
  - Code blocks are highlighted automatically.

- Tags and filtering
  - Add tags while editing using the tag field (comma separated). Tags are stored in lowercase.
  - In view mode, tags show as chips — click them to filter the list of notes.
  - "Filter by tag" at the bottom of the tree lists all tags. Select one or more to show only the notes that have all of them.

- Links between notes
  - Every note has its own address, for example `http://<your-nodepad-host>/#/Documentation/Settings`. Bookmark it, share it, or open it in a new tab; the browser's back and forward buttons move between the notes you have opened. The To do page is at `/#todo`.
  - To link to another note, use an ordinary Markdown link to its file, relative to the current note or starting with / for the Pages folder: `[Settings](Settings.md)`, `[Showcase](../Folder/folder.md)` or `[Home](/index.md)`. For names with spaces, use angle brackets or %20: `[Getting started](<Getting started.md>)`.
  - Or let NodePad write the link: while editing, click the link button in the editor toolbar, then pick a note in the tree (double-click inserts it straight away). You can still type any URL there instead.

- Search
  - Type in the search field in the top bar and press Enter. Results show the matching text; click one to open the note.

- Images that “just work”
  - Paste, drag in, or insert images. NodePad saves them next to your note and updates the link for you.

- Themes
  - NodePad follows your system's light or dark setting. Use the sun/moon button in the top bar to switch; your choice is remembered in the browser.

- Backups
  - Every time you save a note, NodePad zips the whole Pages folder into the Backups folder in the background. The 10 most recent backups are kept.
