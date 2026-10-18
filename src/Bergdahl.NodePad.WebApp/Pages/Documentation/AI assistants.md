# AI assistants (MCP)

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
