# To do lists

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

### Syncing with Home Assistant

NodePad can keep the to do lists in step with Home Assistant, so you can also check off items in the Home Assistant app, on a dashboard or by voice.

1) In Home Assistant, create a long-lived access token: open your profile, go to the Security tab and click "Create token". The user must be an administrator, since NodePad creates the lists in Home Assistant.
2) In NodePad, open Settings, go to the "Home Assistant" tab, enter the address of Home Assistant (for example http://homeassistant.local:8123) and the token, tick "Sync the to do lists" and click Save.

How it works:
- Each list gets a Local To-do list with the same name in Home Assistant, holding the open items and the done items of the last two weeks. If Home Assistant already has a Local To-do list with that name, it is used and the items of both are combined.
- Items added, checked off, unchecked, changed or removed on either side change on the other side right away. Items are matched by their text, so changing an item's text in NodePad replaces it in Home Assistant. An open To do page shows changes made in Home Assistant without being reloaded.
- If one side removes an item while the other side changes it, the item is removed.
- Lists are created, renamed and deleted in NodePad, and Home Assistant follows: a renamed list is created again under its new name. Lists created in Home Assistant are not added to NodePad, and a list deleted in Home Assistant is created there again with NodePad's items.
- NodePad stays connected to Home Assistant, and syncs all lists when it starts and whenever the connection comes back, so changes made on either side in the meantime are not lost. If Home Assistant cannot be reached, NodePad keeps trying; the "Home Assistant" tab shows the status.
- The address and token are stored in nodepad.auth.json, and the state of the last sync in _ToDo/.homeassistant.json.
