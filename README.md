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
- Organize notes in folders, just like on your computer
- Add tags to notes and quickly filter by tag
- Add images to your notes
- Search notes by title or content
- Switch themes: Light, or Dark
- Enjoy automatic, lightweight backups of your notes folder

## Quick start

1) Start NodePad
- From the repository root, open a terminal and run:
  - cd src/Bergdahl.NodePad.WebApp
  - dotnet run
- Open the shown address in your browser (for example http://localhost:5132)

2) Explore the sample content
- On first run, NodePad creates a default Pages folder with a few example notes.

3) Create your first note
- Click “New Document”, give it a name, and start writing.

## Everyday use

- Create notes and folders
  - Use the toolbar to add new notes or create folders. Rename or delete from the menu.

- Edit and format
  - Click “Edit” to open the Markdown editor. Use the preview to see your formatting.
  - Code blocks are highlighted automatically.

- Tags and filtering
  - Add tags while editing using the tag field (comma separated).
  - In view mode, tags show as chips — click them to filter the list of notes.

- Images that “just work”
  - Paste, drag in, or insert images. NodePad saves them next to your note and updates the link for you.

- Themes
  - Choose System, Light, or Dark from the top-right menu. “System” follows your OS setting.

- Backups
  - NodePad creates simple zip backups of your notes folder in the background so you can roll back if needed.

## Where your notes live

- Your notes are plain .md files stored in the Pages folder used by NodePad.
- Each note can have images saved in the same folder as the note.
- This makes it easy to back up, sync, or move your notes to another computer.

## Tips

- The note title is pre-filled from the file name — keep names short and meaningful.
- Use folders for structure and tags for cross-cutting topics.
- Prefer pasting images directly into your note to keep everything together.

## FAQ

- Can I open my notes in another editor?
  - Yes. Notes are plain Markdown files on disk.

- How do I move my notes?
  - Close NodePad, copy the Pages folder to the new location, then start NodePad again.

- Do images overwrite if names match?
  - Yes. If you upload a file with the same name, it replaces the existing one in that note’s folder.

- Does NodePad work offline?
  - Yes, NodePad runs locally and your content stays on your machine.

## Need help or want to contribute?

- Found an issue or have an idea? Open an issue or pull request on GitHub.

## License

MIT © 2025 Jonny Bergdahl — see [LICENSE](LICENSE)

