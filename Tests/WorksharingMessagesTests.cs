using System.Collections.Generic;
using TurboSuite.Shared.Services;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for <see cref="WorksharingMessages.BuildDenial"/> (Core/Shared/Services/WorksharingMessages.cs):
    /// the one message a refused worksharing lease shows. It names who to ask and what to do — the module-lock
    /// holder first, then element holders not already named for the lock, then a Reload Latest line for
    /// ownerless (stale) refusals.
    /// </summary>
    public class WorksharingMessagesTests
    {
        private static Dictionary<string, int> Items(params (string Owner, int Count)[] xs)
        {
            var d = new Dictionary<string, int>();
            foreach (var (o, c) in xs) d[o] = c;
            return d;
        }

        [Fact]
        public void SingleOwner_NamesThemAndCountsItems()
        {
            var msg = WorksharingMessages.BuildDenial("TurboSchedule", null, Items(("sam.k", 3)), 0, "lighting type");
            Assert.Equal("3 lighting types are being edited by sam.k — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void SingleItem_IsSingular()
        {
            var msg = WorksharingMessages.BuildDenial(null!, null, Items(("sam.k", 1)), 0, "circuit");
            Assert.Equal("1 circuit is being edited by sam.k — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void SeveralOwners_LargestHolderFirst()
        {
            var msg = WorksharingMessages.BuildDenial(null!, null, Items(("jo.p", 2), ("sam.k", 5)), 0, "circuit");
            Assert.Equal("5 circuits are being edited by sam.k, 2 by jo.p — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void StaleOnly_SaysReloadLatest()
        {
            var msg = WorksharingMessages.BuildDenial(null!, null, null, 4);
            Assert.Equal("The model has newer changes from central — Reload Latest, then try again.", msg);
        }

        [Fact]
        public void LockHolder_NamedForTheModule_AndNotRepeatedForItems()
        {
            var msg = WorksharingMessages.BuildDenial("TurboSchedule", new[] { "sam.k" },
                Items(("sam.k", 53)), 0, "lighting type");
            Assert.Equal("sam.k is working in TurboSchedule — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void LockHolderAndOtherItemHolder_BothNamed()
        {
            var msg = WorksharingMessages.BuildDenial("TurboSchedule", new[] { "sam.k" },
                Items(("sam.k", 10), ("jo.p", 2)), 0, "lighting type");
            Assert.Equal(
                "sam.k is working in TurboSchedule — ask them to sync, then try again.\n\n" +
                "2 lighting types are being edited by jo.p — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void SeveralLockHolders_JoinedAndPlural()
        {
            var msg = WorksharingMessages.BuildDenial("TurboZones", new[] { "sam.k", "jo.p" }, null, 0);
            Assert.Equal("jo.p and sam.k are working in TurboZones — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void OwnersPlusStale_AddsReloadOnceSyncedLine()
        {
            var msg = WorksharingMessages.BuildDenial(null!, null, Items(("sam.k", 1)), 2, "circuit");
            Assert.Equal(
                "1 circuit is being edited by sam.k — ask them to sync, then try again.\n\n" +
                "The model also has newer changes from central — Reload Latest once they've synced.", msg);
        }

        [Fact]
        public void BlankNoun_FallsBackToItem()
        {
            var msg = WorksharingMessages.BuildDenial(null!, null, Items(("sam.k", 2)), 0, "  ");
            Assert.Equal("2 items are being edited by sam.k — ask them to sync, then try again.", msg);
        }

        [Fact]
        public void NothingClassified_StillGivesAnActionableMessage()
        {
            var msg = WorksharingMessages.BuildDenial(null!, null, null, 0);
            Assert.Contains("Sync, then try again", msg);
        }
    }
}
