# Settings

Open Settings with the gear button in the top bar.

- Settings tab: Allowed Hosts, the Pages directory and the Backup directory. These are saved to nodepad.json. Relative paths are relative to the folder NodePad runs from. A changed Pages directory takes effect after restarting NodePad.
- Password tab: change your password.
- MCP Server tab: create or revoke the API key for AI assistants (MCP).

Some options can only be set by editing nodepad.json (or appsettings.json):

- DataProtectionKeysDirectory: where the sign-in cookie keys are stored (default Keys).
- Uploads:MaxBytes: maximum size of an uploaded image in bytes (default 10 MB).
- Logging:File: the log file folder (default Logs) and file name, rolled daily.
