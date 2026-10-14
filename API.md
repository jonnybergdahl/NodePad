# NodePad API

The web interface talks to NodePad through this REST API. Paths of notes and folders are relative to the Pages folder and use forward slashes; notes end in .md.

Errors are returned as 400 Bad Request (invalid input), 404 Not Found, 409 Conflict, 403 (access denied on disk) or 500, with a short message in the body.

## Authentication

All endpoints require a signed-in session (cookie "NodePad.Auth"), except the ones under /api/auth marked as anonymous below. Requests without a valid session get 401 Unauthorized.

- GET /api/auth/status (anonymous)
  - Returns { configured: bool, authenticated: bool, username: string|null }. configured is false until the first account has been created.

- POST /api/auth/setup (anonymous, only while no account exists)
  - Body: { username, password }. Password must be at least 6 characters.
  - Creates the account in nodepad.auth.json and signs in. Returns 409 if an account already exists.

- POST /api/auth/login (anonymous)
  - Body: { username, password, rememberMe }. rememberMe=true keeps the session across browser restarts (30 days, sliding).
  - Returns 200 and sets the session cookie, 400 on wrong credentials, 409 if no account exists yet.

- POST /api/auth/logout (anonymous)
  - Clears the session cookie.

- POST /api/auth/change-password
  - Body: { currentPassword, newPassword }. Returns 400 if the current password is wrong.
  - Sessions in other browsers are invalidated.

- GET /api/auth/api-key
  - Returns { exists: bool }: whether an API key for the MCP endpoint has been generated.

- POST /api/auth/api-key
  - Generates a new API key for the MCP endpoint, replacing any existing one. Returns { apiKey }. The key is only returned here; only its hash is stored.

- DELETE /api/auth/api-key
  - Revokes the API key, which disables the MCP endpoint.

login, setup and change-password are rate limited to 5 requests per minute per client address (429 Too Many Requests).

## MCP endpoint

- /mcp (MCP Streamable HTTP)
  - Requires the API key, sent as "Authorization: Bearer <key>" or "X-Api-Key: <key>". Returns 401 without a valid key, and always while no key exists. The browser session cookie is not accepted here, and the API key is not accepted on /api.
  - Tools (paths are relative to the Pages folder and use forward slashes; notes end in .md):
    - search_notes(query, tags?, limit?): same as GET /api/pages/search
    - list_notes(tags?): the sorted folder tree with note titles
    - read_note(path): { path, title, tags, lastModifiedUtc, content }
    - recent_notes(limit?, tags?): same as GET /api/pages/recent
    - list_tags(): same as GET /api/pages/tags
    - create_note(path, content, tags?): creates a note; fails if it already exists
    - update_note(path, content, tags?): replaces the content of an existing note; tags are kept when omitted
    - set_note_tags(path, tags): replaces a note's tags
    - create_folder(path)
    - move(source, destination): same as POST /api/pages/move
    - rename(path, newName): same as POST /api/pages/rename
  - There is no delete tool.

## Pages

The frontend uses server-side endpoints for tags and filtered/sorted structure to reduce client work and avoid N+1 requests.

- GET /api/pages/structure
  - Query parameters:
    - sorted=true|false: when true, the tree is returned sorted alphabetically (case-insensitive).
    - dirsFirst=true|false: when sorting, controls whether directories are listed before files.
    - tags=a,b: comma-separated list of tags. Returns only files that have all specified tags, and the directories that contain them.
    - includeCounts=true|false: when true, directory nodes include fileCount (direct .md files) and totalFileCount (recursive .md files in subtree).
    - includeTitles=true|false: when true, file nodes include an extracted title field based on the first H1 or the filename fallback.
  - Examples:
    - /api/pages/structure?sorted=true&dirsFirst=true
    - /api/pages/structure?tags=work,project-x&sorted=true
    - /api/pages/structure?sorted=true&dirsFirst=true&includeCounts=true
    - /api/pages/structure?sorted=true&includeTitles=true
  - Response enrichment when includeCounts=true:
    - Directory nodes contain additional fields:
      - fileCount: number of Markdown files directly in that directory
      - totalFileCount: number of Markdown files in the directory and all its subdirectories
  - Response enrichment when includeTitles=true:
    - File nodes contain an additional field:
      - title: extracted from the first level-1 heading ("# ") in the file; falls back to the file name if no H1 is found

- GET /api/pages/tags-index
  - Returns a map of relative markdown file paths to their normalized tags.
  - Example response: { "Folder/file.md": ["work", "project-x"] }

- GET /api/pages/tags
  - Returns aggregated tag counts: [{ tag, count }], with tags normalized.

- GET /api/pages/tags/suggest?prefix=de
  - Returns up to 50 normalized tags that start with the given prefix (case-insensitive).

- GET /api/pages/meta?path=...&includeNormalized=true|false
  - Default (includeNormalized=false): returns an array of tags as stored for the note.
  - When includeNormalized=true: returns an object { display: string[], normalized: string[] }.

- GET /api/pages/content?path=...&includeMeta=true|false
  - Default (includeMeta=false): returns raw markdown as text/plain (backward compatible).
  - When includeMeta=true: returns JSON { content: string, breadcrumbs: [{ name, path }] }.

- GET /api/pages/breadcrumbs?path=...
  - Returns only the computed breadcrumbs for the given markdown file path: [{ name, path }].

- GET /api/pages/info?path=...
  - Returns unified page information with a normalized title and basic file stats.
  - Response: { title: string, tags: string[], lastModifiedUtc: string (ISO 8601), sizeBytes: number }
  - Title is extracted from the first H1 ("# ") in the markdown; falls back to the file name if none is found.

- GET /api/pages/validate-name?path=parent/dir&type=file|directory&name=ProposedName
  - Dry-run validation for create/rename operations.
  - Returns: { valid: boolean, message?: string, suggestedName?: string }
  - Behavior:
    - Verifies the parent path is within the Pages root and exists.
    - Sanitizes the proposed name (invalid chars removed, whitespace collapsed). Files are ensured to end with .md.
    - Rejects separators in the name ("/" or "\\").
    - If a conflict exists, suggestedName provides a unique alternative (e.g., "My-note-2.md").
    - When valid and the sanitized name differs, suggestedName contains the sanitized canonical name.

- POST /api/pages/save?path=...
  - Two content types are supported at the same route:
    - text/plain (legacy): body is raw markdown; optional query "tags=a,b". Tags are normalized server-side before storing.
    - application/json (structured): body { content: string, tags?: string[] }. Tags are normalized server-side before storing.
  - Tags are left unchanged when none are given. The note and its folders are created if they do not exist.
  - Returns the saved content as text/plain. Every save starts a zip backup of the Pages folder in the background.

- POST /api/pages/create?path=...&type=file|directory
  - Creates a note (type=file) or a folder (type=directory).
  - A new note starts with a "# " heading taken from the file name, and gets an empty .meta file.
  - Returns 400 if it already exists.

- DELETE /api/pages/delete?path=...&recursive=true|false
  - Deletes a note (with its .meta file) or a folder.
  - A folder that is not empty is only deleted with recursive=true; otherwise 409 Conflict is returned.

- GET /api/pages/search?query=...&tags=a,b&limit=100
  - Enhanced search with relevance scoring, optional tag filter, and highlight ranges for snippets.
  - Query parameters:
    - query (required): the search term (min 2 chars)
    - tags (optional): comma-separated list; only notes containing all specified tags are returned
    - limit (optional): max number of results (default 100, max 200)
  - Response (array of results):
    - path: relative markdown path (e.g., "Folder/file.md")
    - title: extracted from the first H1 or file name fallback
    - snippet: short preview around the first match; may be prefixed/suffixed with ellipsis characters (…)
    - score: relevance score (higher means more relevant)
    - highlights: array of { start, length } indicating match ranges relative to the snippet string, suitable for client-side highlighting

- GET /api/pages/recent?limit=20&tags=a,b
  - Returns the most recently modified notes, optionally filtered by tags.
  - Query parameters:
    - limit (optional): number of items to return (default 20, max 100)
    - tags (optional): comma-separated list; only notes containing all specified tags are returned
  - Response (array of results):
    - path: relative markdown path
    - title: extracted title (first H1 or filename fallback)
    - lastModifiedUtc: ISO 8601 timestamp of the last modification time (UTC)

- GET /api/pages/untagged
  - Returns all notes that currently have no tags.
  - Response (array of results):
    - path: relative markdown path
    - title: extracted title (first H1 or filename fallback)

- POST /api/pages/move?source=...&destination=...
  - Moves a file or a folder to the specified destination directory.
  - Parameters:
    - source (required): relative path of the file ("Folder/file.md") or folder ("Folder/Sub") to move.
    - destination (optional): relative path of the destination directory. Use an empty string to move to the root folder.
  - Returns JSON: { oldPath: string, path: string, type: "file"|"directory" }
  - Notes:
    - When moving a file, a matching .meta file (tags) is moved alongside it if present.
    - Safety checks prevent moving outside the Pages root or moving a folder into its own subtree.
  - Examples:
    - Move to another folder: POST /api/pages/move?source=docs/intro.md&destination=archive
    - Move to root: POST /api/pages/move?source=archive/intro.md&destination=

- POST /api/pages/rename?path=...&newName=...
  - Renames a note or a folder in place. For notes, .md is added to newName if missing, and the .meta file is renamed too.
  - Returns JSON: { oldPath: string, path: string, type: "file"|"directory" }. Returns 400 if the new name is taken.

## Images

- POST /api/uploads/images?pagePath=...
  - Multipart form upload with the file in the "image" field. PNG, JPEG, GIF, WebP and SVG are accepted.
  - With pagePath (a note path), the image is saved in the same folder as the note; otherwise in wwwroot/uploads/images.
  - An existing image with the same name is replaced.
  - Maximum size is set by "Uploads:MaxBytes" in the configuration (default 10 MB); larger files get 413.
  - Returns { url }, for example { "url": "/pages/Folder/diagram.png" }.

- GET /pages/...
  - Serves files from the Pages folder, such as images referenced by notes.

## Settings

- GET /api/settings
  - Returns { allowedHosts, pagesDirectory, backupDirectory } from nodepad.json.

- POST /api/settings
  - Body: { allowedHosts?, pagesDirectory?, backupDirectory? }. Only the given values are changed in nodepad.json, and missing directories are created.
  - Returns the updated settings. A changed Pages directory takes effect after a restart.

## Notes

- Tags are normalized to lowercase, with whitespace trimmed and collapsed. Normalized tags are written when saving; existing files are unaffected until re-saved.
- Tags are stored as a comma-separated list in a .meta file next to the note (ideas.md → ideas.meta).
- The query options of /api/pages/structure are opt-in. Calling it without parameters returns the unsorted tree.
