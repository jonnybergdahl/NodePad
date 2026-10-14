# Tips and FAQ

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

- Found an issue or have an idea? Open an issue or pull request on [GitHub](https://github.com/jonnybergdahl/NodePad).
