using System.Collections.Generic;
using System.Linq;
using Bergdahl.NodePad.WebApp.Models;
using Bergdahl.NodePad.WebApp.Services;
using Xunit;
using static Bergdahl.NodePad.WebApp.Services.HomeAssistantMerge;
using static Bergdahl.NodePad.WebApp.Services.TodoService;
using RemoteItem = Bergdahl.NodePad.WebApp.Services.HomeAssistantClient.Item;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// The two-way merge rules, without Home Assistant or files.
    /// </summary>
    public class HomeAssistantMergeTests
    {
        private static TodoItem Local(int index, string text, bool done = false) => new(index, text, done);

        private static RemoteItem Remote(string uid, string text, bool done = false) => new(uid, text, done);

        private static SyncedItem Base(string uid, string text, bool done = false) => new(uid, text, done);

        [Fact]
        public void First_Sync_Pairs_Items_By_Text_And_NodePad_Wins()
        {
            var plan = Merge(
                new List<SyncedItem>(),
                new[] { Local(0, "Milk"), Local(1, "Eggs", done: true) },
                new[] { Remote("a", "Eggs"), Remote("b", "Bread") });

            Assert.Equal(new TodoEdit[] { new TodoEdit.Add("Bread", false) }, plan.Local);
            Assert.Equal(new[] { ("a", true) }, plan.RemoteSetDone);
            Assert.Equal(new[] { ("Milk", false) }, plan.RemoteAdd);
            Assert.Empty(plan.RemoteRemove);
        }

        [Fact]
        public void Nothing_Changed_Gives_An_Empty_Plan()
        {
            var plan = Merge(
                new[] { Base("a", "Milk"), Base("b", "Eggs", done: true) },
                new[] { Local(0, "Milk"), Local(1, "Eggs", done: true) },
                new[] { Remote("a", "Milk"), Remote("b", "Eggs", done: true) });

            Assert.Empty(plan.Local);
            Assert.False(plan.ChangesRemote);
        }

        [Fact]
        public void Changes_Made_In_Home_Assistant_Reach_NodePad()
        {
            var plan = Merge(
                new[] { Base("a", "Milk"), Base("b", "Eggs"), Base("c", "Bread") },
                new[] { Local(0, "Milk"), Local(1, "Eggs"), Local(2, "Bread") },
                new[] { Remote("a", "Milk", done: true), Remote("b", "Brown eggs"), Remote("d", "Jam") });

            Assert.Equal(new TodoEdit[]
            {
                new TodoEdit.Update(0, null, true),
                new TodoEdit.Update(1, "Brown eggs", null),
                new TodoEdit.Remove(2),
                new TodoEdit.Add("Jam", false),
            }, plan.Local);
            Assert.False(plan.ChangesRemote);
        }

        [Fact]
        public void Changes_Made_In_NodePad_Reach_Home_Assistant()
        {
            var plan = Merge(
                new[] { Base("a", "Milk"), Base("b", "Eggs"), Base("c", "Bread", done: true) },
                // Eggs renamed, Bread unchecked, Jam added
                new[] { Local(0, "Milk"), Local(1, "Brown eggs"), Local(2, "Bread"), Local(3, "Jam", done: true) },
                new[] { Remote("a", "Milk"), Remote("b", "Eggs"), Remote("c", "Bread", done: true) });

            Assert.Empty(plan.Local);
            Assert.Equal(new[] { "b" }, plan.RemoteRemove);
            Assert.Equal(new[] { ("c", false) }, plan.RemoteSetDone);
            Assert.Equal(new[] { ("Brown eggs", false), ("Jam", true) }, plan.RemoteAdd);
        }

        [Fact]
        public void Both_Sides_Changing_Different_Items_Merge()
        {
            var plan = Merge(
                new[] { Base("a", "Milk"), Base("b", "Eggs") },
                new[] { Local(0, "Milk", done: true), Local(1, "Eggs") },
                new[] { Remote("a", "Milk"), Remote("b", "Eggs", done: true) });

            Assert.Equal(new TodoEdit[] { new TodoEdit.Update(1, null, true) }, plan.Local);
            Assert.Equal(new[] { ("a", true) }, plan.RemoteSetDone);
        }

        [Fact]
        public void Removing_An_Item_Wins_Over_Changing_It()
        {
            var plan = Merge(
                new[] { Base("a", "Milk"), Base("b", "Eggs") },
                // Milk removed in NodePad, Eggs checked in NodePad
                new[] { Local(0, "Eggs", done: true) },
                // Milk renamed in Home Assistant, Eggs removed in Home Assistant
                new[] { Remote("a", "Oat milk") });

            Assert.Equal(new TodoEdit[] { new TodoEdit.Remove(0) }, plan.Local);
            Assert.Equal(new[] { "a" }, plan.RemoteRemove);
            Assert.Empty(plan.RemoteSetDone);
            Assert.Empty(plan.RemoteAdd);
        }

        [Fact]
        public void Duplicate_Items_Pair_Up_By_Done_State()
        {
            var plan = Merge(
                new[] { Base("a", "Milk", done: true), Base("b", "Milk") },
                new[] { Local(0, "Milk"), Local(1, "Milk", done: true) },
                // The open one is checked in Home Assistant
                new[] { Remote("a", "Milk", done: true), Remote("b", "Milk", done: true) });

            Assert.Equal(new TodoEdit[] { new TodoEdit.Update(0, null, true) }, plan.Local);
            Assert.False(plan.ChangesRemote);
        }

        [Fact]
        public void Pair_Keeps_Only_Items_On_Both_Sides()
        {
            var paired = Pair(
                new[] { Local(0, "Milk"), Local(1, "Eggs", done: true) },
                new[] { Remote("a", "Eggs", done: true), Remote("b", "Bread") });

            Assert.Equal(new[] { Base("a", "Eggs", done: true) }, paired);
        }

        [Fact]
        public void SameAsBase_Compares_Uid_Text_And_State()
        {
            var baseItems = new[] { Base("a", "Milk"), Base("b", "Eggs", done: true) };

            Assert.True(SameAsBase(baseItems, new[] { Remote("b", "Eggs", done: true), Remote("a", "Milk") }));
            Assert.False(SameAsBase(baseItems, new[] { Remote("a", "Milk"), Remote("b", "Eggs") }));
            Assert.False(SameAsBase(baseItems, new[] { Remote("a", "Milk") }));
            Assert.False(SameAsBase(baseItems.ToList(), new[] { Remote("a", "Milk"), Remote("c", "Eggs", done: true) }));
        }
    }
}
