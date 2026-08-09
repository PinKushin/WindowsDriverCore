using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Unit.Sessions;

/// <summary>
/// The session store is shared by every request the server handles, so its
/// concurrency behaviour is a correctness property rather than a performance
/// one. A client running tests in parallel against one server hits it from
/// several threads at once.
/// </summary>
[TestFixture]
public sealed class SessionStoreTests
{
    private static DriverSession Session(string id) =>
        new(id, new Dictionary<string, string> { ["app"] = "Calculator" }, ProcessId: 1, WindowHandle: 2);

    [Test]
    public void Find_ReturnsTheSessionThatWasAdded()
    {
        SessionStore store = new();
        DriverSession added = Session("abc");

        store.Add(added);

        store.Find("abc").ShouldBe(added);
    }

    [Test]
    public void Find_UnknownId_ReturnsNull()
    {
        SessionStore store = new();
        store.Add(Session("abc"));

        // The control: a store that returned the first session regardless of id
        // would pass the test above. This is the input where correct and broken
        // differ.
        store.Find("not-a-session").ShouldBeNull();
    }

    [Test]
    public void Find_IsCaseSensitive()
    {
        // Session ids are opaque GUIDs the server generated. Matching loosely
        // would let a client reach a session it did not create by guessing case.
        SessionStore store = new();
        store.Add(Session("abc"));

        store.Find("ABC").ShouldBeNull();
    }

    [Test]
    public void Remove_ReturnsTheSession_AndItIsGone()
    {
        SessionStore store = new();
        DriverSession added = Session("abc");
        store.Add(added);

        store.Remove("abc").ShouldBe(added);
        store.Find("abc").ShouldBeNull();
    }

    [Test]
    public void Remove_UnknownId_ReturnsNullAndLeavesOthersAlone()
    {
        // The bystander: removing something that is not there must not disturb a
        // session that is. Without a second session in the store, "removed
        // nothing" and "removed everything" look the same.
        SessionStore store = new();
        DriverSession survivor = Session("keep-me");
        store.Add(survivor);

        store.Remove("not-a-session").ShouldBeNull();

        store.Find("keep-me").ShouldBe(survivor);
        store.All().Count.ShouldBe(1);
    }

    [Test]
    public void All_PreservesCreationOrder()
    {
        // GET /sessions lists them, and a client that created sessions in order
        // has no other way to tell them apart than the order and the ids.
        SessionStore store = new();
        store.Add(Session("first"));
        store.Add(Session("second"));
        store.Add(Session("third"));

        store.All().Select(s => s.Id).ShouldBe(["first", "second", "third"]);
    }

    [Test]
    public void Add_IsSafeUnderConcurrentUse()
    {
        // Many threads adding at once. The condition has to be large enough for
        // the race to appear — a handful of sequential adds would pass against a
        // plain Dictionary too.
        //
        // Detection here is genuinely probabilistic, which is unusual in this
        // suite and worth stating: swapping in a plain Dictionary did NOT fail
        // this test on the run that verified it, only the interleaved one below.
        // A race that does not manifest is not evidence of safety, so this test
        // is the weaker of the pair and should not be trusted alone.
        SessionStore store = new();
        const int SessionCount = 500;

        Parallel.For(0, SessionCount, index => store.Add(Session($"session-{index}")));

        store.All().Count.ShouldBe(SessionCount);
        store.All().Select(s => s.Id).Distinct().Count().ShouldBe(SessionCount);
    }

    [Test]
    public void AddAndRemove_InterleavedAcrossThreads_LeaveTheStoreConsistent()
    {
        SessionStore store = new();
        const int SessionCount = 500;
        for (int index = 0; index < SessionCount; index++)
        {
            store.Add(Session($"session-{index}"));
        }

        Parallel.For(0, SessionCount, index =>
        {
            if (index % 2 == 0)
            {
                store.Remove($"session-{index}");
            }
        });

        store.All().Count.ShouldBe(SessionCount / 2);

        // Exactly the odd-numbered sessions survive. Asserting only the count
        // would pass if the store removed 250 arbitrary sessions.
        store.All()
            .Select(s => int.Parse(s.Id["session-".Length..], CultureInfo.InvariantCulture))
            .ShouldAllBe(index => index % 2 == 1);
    }
}
