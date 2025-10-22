# API endpoints (tags, structure, and meta)

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

Notes
- Tag normalization is case-insensitive with whitespace trimmed and collapsed. Normalized tags are written when saving; existing files are unaffected until re-saved.
- These endpoints are opt-in. Calling /api/pages/structure without parameters preserves existing behavior.
