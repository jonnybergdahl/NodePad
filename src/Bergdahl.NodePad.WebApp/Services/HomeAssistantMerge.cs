using Bergdahl.NodePad.WebApp.Models;
using static Bergdahl.NodePad.WebApp.Services.TodoService;

namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// Two-way merge of one ToDo list. Both sides are compared with the items as they were after the last
/// sync (the base), so it is clear which side changed what:
/// <list type="bullet">
/// <item>Items added on one side are added on the other; items added on both sides with the same text pair up.</item>
/// <item>A checked or unchecked item is changed on the other side.</item>
/// <item>Text changed in Home Assistant changes the NodePad item. NodePad items have no id, so text changed
/// in NodePad is a removed and an added item.</item>
/// <item>A removed item is removed on the other side, even if that side changed it.</item>
/// </list>
/// NodePad items are matched by their text, Home Assistant items by their uid.
/// </summary>
public static class HomeAssistantMerge
{
    /// <summary>An item as both sides had it after the last sync.</summary>
    public sealed record SyncedItem(string Uid, string Text, bool Done);

    public sealed record Plan(
        IReadOnlyList<TodoEdit> Local,
        IReadOnlyList<string> RemoteRemove,
        IReadOnlyList<(string Uid, bool Done)> RemoteSetDone,
        IReadOnlyList<(string Text, bool Done)> RemoteAdd)
    {
        public bool ChangesRemote => RemoteRemove.Count > 0 || RemoteSetDone.Count > 0 || RemoteAdd.Count > 0;
    }

    public static Plan Merge(IReadOnlyList<SyncedItem> baseItems, IReadOnlyList<TodoItem> local, IReadOnlyList<HomeAssistantClient.Item> remote)
    {
        var localEdits = new List<TodoEdit>();
        var remoteRemove = new List<string>();
        var remoteSetDone = new List<(string, bool)>();
        var remoteAdd = new List<(string, bool)>();

        var (basePairs, localAdded, _) = Match(local, l => l.Text, l => l.Done, baseItems, b => b.Text, b => b.Done);
        var localByBase = basePairs.ToDictionary(p => p.B, p => local[p.A]);
        var remoteByUid = ByUid(remote);

        for (var i = 0; i < baseItems.Count; i++)
        {
            var b = baseItems[i];
            localByBase.TryGetValue(i, out var l);
            remoteByUid.TryGetValue(b.Uid, out var r);
            if (l == null && r == null) continue;
            if (r == null)
            {
                // Removed in Home Assistant
                localEdits.Add(new TodoEdit.Remove(l!.Index));
                continue;
            }
            if (l == null)
            {
                // Removed in NodePad
                remoteRemove.Add(r.Uid);
                continue;
            }

            var text = r.Summary != b.Text ? r.Summary : null;
            bool? done = null;
            if (l.Done != b.Done)
            {
                if (r.Done != l.Done) remoteSetDone.Add((r.Uid, l.Done));
            }
            else if (r.Done != b.Done)
            {
                done = r.Done;
            }
            if (text != null || done != null) localEdits.Add(new TodoEdit.Update(l.Index, text, done));
        }

        var baseUids = baseItems.Select(b => b.Uid).ToHashSet(StringComparer.Ordinal);
        var remoteAdded = remote.Where(r => !baseUids.Contains(r.Uid)).ToList();
        var addedLocal = localAdded.Select(i => local[i]).ToList();
        var (addedPairs, onlyLocal, onlyRemote) = Match(addedLocal, l => l.Text, l => l.Done, remoteAdded, r => r.Summary, r => r.Done);

        // Added on both sides, for example the first sync of a list that already exists in Home Assistant; NodePad's state wins
        foreach (var (a, r) in addedPairs)
        {
            if (addedLocal[a].Done != remoteAdded[r].Done) remoteSetDone.Add((remoteAdded[r].Uid, addedLocal[a].Done));
        }
        remoteAdd.AddRange(onlyLocal.Select(i => (addedLocal[i].Text, addedLocal[i].Done)));
        localEdits.AddRange(onlyRemote.Select(i => (TodoEdit)new TodoEdit.Add(remoteAdded[i].Summary, remoteAdded[i].Done)));

        return new Plan(localEdits, remoteRemove, remoteSetDone, remoteAdd);
    }

    /// <summary>
    /// The base to store after a sync: each NodePad item paired with the Home Assistant item that has its text.
    /// Items that do not pair up are left out, so the next sync treats them as added.
    /// </summary>
    public static List<SyncedItem> Pair(IReadOnlyList<TodoItem> local, IReadOnlyList<HomeAssistantClient.Item> remote)
    {
        var (pairs, _, _) = Match(local, l => l.Text, l => l.Done, remote, r => r.Summary, r => r.Done);
        return pairs.Select(p => new SyncedItem(remote[p.B].Uid, local[p.A].Text, local[p.A].Done)).ToList();
    }

    /// <summary>
    /// The pairs of the Home Assistant items with a NodePad item whose done state differs.
    /// </summary>
    public static List<(string Uid, bool Done)> DoneMismatches(IReadOnlyList<TodoItem> local, IReadOnlyList<HomeAssistantClient.Item> remote)
    {
        var (pairs, _, _) = Match(local, l => l.Text, l => l.Done, remote, r => r.Summary, r => r.Done);
        return pairs.Where(p => local[p.A].Done != remote[p.B].Done).Select(p => (remote[p.B].Uid, local[p.A].Done)).ToList();
    }

    /// <summary>
    /// True if Home Assistant's items are exactly the base, so there is nothing to sync from that side.
    /// </summary>
    public static bool SameAsBase(IReadOnlyList<SyncedItem> baseItems, IReadOnlyList<HomeAssistantClient.Item> remote)
    {
        if (baseItems.Count != remote.Count) return false;
        var remoteByUid = ByUid(remote);
        return baseItems.All(b => remoteByUid.TryGetValue(b.Uid, out var r) && r.Summary == b.Text && r.Done == b.Done);
    }

    private static Dictionary<string, HomeAssistantClient.Item> ByUid(IReadOnlyList<HomeAssistantClient.Item> remote) =>
        remote.GroupBy(r => r.Uid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>
    /// Pairs items with the same text, first those that also have the same done state, so duplicates
    /// pair up sensibly. Returns the index pairs and the indexes left over on each side, in order.
    /// </summary>
    private static (List<(int A, int B)> Pairs, List<int> OnlyA, List<int> OnlyB) Match<TA, TB>(
        IReadOnlyList<TA> a, Func<TA, string> textA, Func<TA, bool> doneA,
        IReadOnlyList<TB> b, Func<TB, string> textB, Func<TB, bool> doneB)
    {
        var pairs = new List<(int, int)>();
        var usedA = new bool[a.Count];
        var usedB = new bool[b.Count];
        foreach (var sameDone in new[] { true, false })
        {
            for (var i = 0; i < a.Count; i++)
            {
                if (usedA[i]) continue;
                for (var j = 0; j < b.Count; j++)
                {
                    if (usedB[j] || textA(a[i]) != textB(b[j]) || (sameDone && doneA(a[i]) != doneB(b[j]))) continue;
                    pairs.Add((i, j));
                    usedA[i] = usedB[j] = true;
                    break;
                }
            }
        }
        var onlyA = Enumerable.Range(0, a.Count).Where(i => !usedA[i]).ToList();
        var onlyB = Enumerable.Range(0, b.Count).Where(j => !usedB[j]).ToList();
        return (pairs, onlyA, onlyB);
    }
}
