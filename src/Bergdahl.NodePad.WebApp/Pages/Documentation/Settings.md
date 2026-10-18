# Settings

Open Settings with the gear button in the top bar.

- Settings tab: Allowed Hosts, the Pages directory and the Backup directory. These are saved to nodepad.json, which is created the first time you save if it does not exist yet; until then the defaults are used (Pages and Backups next to the app). Leave a field empty to go back to its default. Relative paths are relative to the folder NodePad runs from. A changed Pages directory takes effect after restarting NodePad.
- Password tab: change your password.
- MCP Server tab: create or revoke the API key for AI assistants (MCP).
- To do lists tab: create, rename and delete to do lists. Deleting a list also deletes its items.

Some options can only be set by editing nodepad.json (or appsettings.json):

- DataProtectionKeysDirectory: where the sign-in cookie keys are stored (default Keys).
- Uploads:MaxBytes: maximum size of an uploaded image in bytes (default 10 MB).
- Logging:File: the log file folder (default Logs) and file name, rolled daily.
