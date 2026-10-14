# Signing in

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
- Use HTTPS if NodePad is reachable from other machines, so passwords and cookies are not sent in clear text.
