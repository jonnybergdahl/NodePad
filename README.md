# NodePad

A fast, lightweight notes web app that uses plain Markdown files. Create, organize, and edit your notes in the browser — your content stays as simple .md files on your disk.

<p align="center">
  <img src="src/Bergdahl.NodePad.WebApp/wwwroot/assets/nodepad_logo.png" alt="NodePad logo" width="240" />
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/en-us/download/dotnet/8.0"><img alt=".NET" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" /></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-green.svg" /></a>
</p>

---

## What you can do with NodePad

- Browse your notes in a simple, clean interface
- Write notes in Markdown format with preview
- Organize notes in folders, with drag&drop support
- Add tags to notes and quickly filter by tag
- Add images to your notes
- Search notes by title or content
- Switch between a light and a dark theme
- Enjoy automatic, lightweight backups of your notes folder
- Keep named to do lists: checked items get the date they were done and disappear two weeks later
- Let AI assistants such as Claude read and edit your notes through the built-in MCP server

## Quick start

1) Start NodePad
- From the repository root, open a terminal and run:
  - cd src/Bergdahl.NodePad.WebApp
  - dotnet run
- Open the shown address in your browser (for example http://localhost:5132)

2) Explore the sample content
- When run from the source code, NodePad uses the Pages folder in the project, which contains this documentation, a Markdown showcase and a sample to do list.
- A published build starts with an empty Pages folder.

3) Create your first note
- Click “New Document”, give it a name, and start writing.

## Signing in

NodePad is protected by a single account. Everything — the app, the API and your notes — requires you to be signed in.

- First start: open NodePad in your browser and you will be asked to create your account (username and password, at least 6 characters). Do this right after installing, since whoever opens the app first gets to create the account.
- The account is stored in nodepad.auth.json next to nodepad.json (it is git-ignored). The password is stored as a salted PBKDF2 hash, never in plain text.
- Change your password on the "Password" tab in Settings (the gear button in the top bar). Other browsers that are signed in will be signed out.
- Sign out with the sign-out button in the top-right corner.
- Forgot your password? Stop NodePad, delete nodepad.auth.json, start it again and create a new account.
- Too many failed sign-in attempts from the same address are blocked for a minute.

Hosting in IIS
- The app pool identity needs write access to the application folder: NodePad writes nodepad.json (settings), nodepad.auth.json (account), the Pages and Backups folders, and a Keys folder.
- The Keys folder holds the keys used to protect sign-in cookies, so you stay signed in across app pool recycles. Its location can be changed with "DataProtectionKeysDirectory" in nodepad.json. Keep it private.
- nodepad.json and nodepad.auth.json are not included when you publish NodePad, so deploying a new version keeps the server's settings and account.
- Use HTTPS if NodePad is reachable from other machines, so passwords and cookies are not sent in clear text.

## Using NodePad from Claude and other AI assistants (MCP)

NodePad has a built-in MCP server, so AI assistants such as Claude Code can search, read, create and edit your notes, and read and update your to do lists. Notes and to do lists cannot be deleted through it.

1) In NodePad, open Settings (the gear button), go to the "MCP Server" tab and click "Generate API key". Copy the key right away — it is only shown once. Generating a new key replaces the old one, and "Revoke key" turns MCP access off.
2) Add the server to your assistant using the MCP server URL shown in Settings (for example `http://<your-nodepad-host>/mcp`) and send the key as a bearer token. For Claude Code:

   ```bash
   claude mcp add --scope user --transport http nodepad http://<your-nodepad-host>/mcp --header "Authorization: Bearer <your-key>"
   ```

   `--scope user` makes NodePad available in every Claude Code session, not just the current folder.

   Other clients that support Streamable HTTP work the same way; the key can also be sent as an "X-Api-Key" header.

Tools for notes: search_notes, list_notes, read_note, recent_notes, list_tags, create_note, update_note, set_note_tags, create_folder, move and rename. Tools for to do lists: list_todos, create_todo_list, add_todo and set_todo_done. Every save also triggers the normal zip backup.

The API key only grants access to /mcp, not to the rest of the app. Only a hash of the key is stored (in nodepad.auth.json).

## Everyday use

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

- Search
  - Type in the search field in the top bar and press Enter. Results show the matching text; click one to open the note.

- Images that “just work”
  - Paste, drag in, or insert images. NodePad saves them next to your note and updates the link for you.

- Themes
  - NodePad follows your system's light or dark setting. Use the sun/moon button in the top bar to switch; your choice is remembered in the browser.

- Backups
  - Every time you save a note, NodePad zips the whole Pages folder into the Backups folder in the background. The 10 most recent backups are kept.

## To do lists

Click "To do" at the top of the menu to open the To do page. It shows all your to do lists side by side.

- Lists are created, renamed and deleted on the "To do lists" tab in Settings.
- Type in "Add item" at the bottom of a list and press Enter to add an item.
- Tick an item's checkbox when it is done. NodePad notes the date, and done items are moved below the open ones, newest first.
- Click an item's text to change it (Enter saves, Esc cancels), and use the cross that appears when you point at an item to delete it.
- Done items older than two weeks are hidden, but stay in the list. Tick "Show older done items" to see them again.

Each list is a Markdown file in the _ToDo folder in Pages, for example _ToDo/Groceries.md:

```markdown
# Groceries

- [ ] Milk
- [x] Eggs ✅ 2026-10-18
```

You can edit these files in any editor: lines that are not items are left untouched, and done items without a date are always shown. The _ToDo folder is reserved for to do lists, so it is not shown in the note tree, search or tags, and notes cannot be created in it.

## Settings

Open Settings with the gear button in the top bar.

- Settings tab: Allowed Hosts, the Pages directory and the Backup directory. These are saved to nodepad.json, which is created the first time you save if it does not exist yet; until then the defaults are used (Pages and Backups next to the app). Leave a field empty to go back to its default. Relative paths are relative to the folder NodePad runs from. A changed Pages directory takes effect after restarting NodePad.
- Password tab: change your password.
- MCP Server tab: create or revoke the API key for AI assistants (MCP).
- To do lists tab: create, rename and delete to do lists. Deleting a list also deletes its items.

Some options can only be set by editing nodepad.json (or appsettings.json):

- DataProtectionKeysDirectory: where the sign-in cookie keys are stored (default Keys).
- Uploads:MaxBytes: maximum size of an uploaded image in bytes (default 10 MB).
- Logging:File: the log file folder (default Logs) and file name, rolled daily.

## Where your notes live

- Your notes are plain .md files stored in the Pages folder used by NodePad.
- A note's tags are stored in a .meta file with the same name next to it (for example ideas.md and ideas.meta).
- To do lists are stored in the _ToDo folder in Pages, one Markdown file per list.
- Each note can have images saved in the same folder as the note.
- This makes it easy to back up, sync, or move your notes to another computer.

## Tips

- A new note starts with a title heading taken from the file name — keep names short and meaningful. The first "# " heading is used as the note's title in search results.
- Use folders for structure and tags for cross-cutting topics.
- Prefer pasting images directly into your note to keep everything together.

## FAQ

- Can I open my notes in another editor?
  - Yes. Notes are plain Markdown files on disk.

- How do I move my notes?
  - Close NodePad, copy the Pages folder to the new location, then start NodePad again.

- How do I move a page to the root folder?
  - In the left-hand tree, drag the page and drop it on "Pages" at the top, or in the empty space below the list (not on a folder). This moves it to the root. You can also use the API: POST /api/pages/move?source=Folder/file.md&destination=

- Do images overwrite if names match?
  - Yes. If you upload a file with the same name, it replaces the existing one in that note’s folder.

- Does NodePad work offline?
  - Your notes stay on your own machine, but the browser loads the Markdown editor, fonts and icons from public CDNs, so it needs an internet connection.

## Need help or want to contribute?

- Found an issue or have an idea? Open an issue or pull request on GitHub.

## API

NodePad has a REST API, used by the web interface. See [API.md](API.md).

## License

MIT © 2025 Jonny Bergdahl — see [LICENSE](LICENSE)
