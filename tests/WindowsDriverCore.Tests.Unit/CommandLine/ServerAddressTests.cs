using System;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Host.CommandLine;

namespace WindowsDriverCore.Tests.Unit.CommandLine;

/// <summary>
/// The command line is a compatibility surface, not a design choice. Every form
/// here is documented by WinAppDriver and appears in scripts that already exist:
/// its README, Docs/RunningOnRemoteMachine.md, Docs/FAQ.md, Docs/SeleniumGrid.md
/// and Docs/UsingAppium.md. Getting the shape wrong means existing setups cannot
/// point at this server.
///
/// The unusual part is deliberate: the base path rides on the PORT argument
/// (<c>4723/wd/hub</c>), not as a third argument.
/// </summary>
[TestFixture]
public sealed class ServerAddressTests
{
    [Test]
    public void Parse_NoArguments_DefaultsToLoopbackOn4723()
    {
        ServerAddress address = ServerAddress.Parse([]);

        address.Host.ShouldBe("127.0.0.1");
        address.Port.ShouldBe(4723);
        address.BasePath.ShouldBeNull();
    }

    [Test]
    public void Parse_PortOnly_KeepsLoopbackHost()
    {
        // Documented in WinAppDriver's README: `WinAppDriver.exe 4727`
        ServerAddress address = ServerAddress.Parse(["4727"]);

        address.Host.ShouldBe("127.0.0.1");
        address.Port.ShouldBe(4727);
        address.BasePath.ShouldBeNull();
    }

    [Test]
    public void Parse_HostAndPort_UsesBoth()
    {
        ServerAddress address = ServerAddress.Parse(["10.0.0.10", "4725"]);

        address.Host.ShouldBe("10.0.0.10");
        address.Port.ShouldBe(4725);
        address.BasePath.ShouldBeNull();
    }

    [Test]
    public void Parse_BasePathRidesOnPortArgument_SplitsIntoPortAndPath()
    {
        // `WinAppDriver.exe 10.0.0.10 4723/wd/hub` — the base path is part of the
        // port argument. This is the form every Appium and Selenium Grid config
        // in the wild uses, and the previous implementation served none of it.
        ServerAddress address = ServerAddress.Parse(["10.0.0.10", "4723/wd/hub"]);

        address.Host.ShouldBe("10.0.0.10");
        address.Port.ShouldBe(4723);
        address.BasePath.ShouldBe("/wd/hub");
    }

    [Test]
    public void Parse_BasePathWithPortOnlyArgument_StillSplits()
    {
        ServerAddress address = ServerAddress.Parse(["4723/wd/hub"]);

        address.Host.ShouldBe("127.0.0.1");
        address.Port.ShouldBe(4723);
        address.BasePath.ShouldBe("/wd/hub");
    }

    [Test]
    public void Parse_AsteriskHost_BindsAllInterfaces()
    {
        // Docs/RunningOnRemoteMachine.md: "Setting `*` as the IP address command
        // line option will cause it to bind to all bound IP addresses".
        //
        // Port 4725 rather than 4723 deliberately: with the default port as the
        // condition, an implementation that ignored the port argument entirely
        // would produce the same observation as a correct one.
        ServerAddress address = ServerAddress.Parse(["*", "4725"]);

        address.Host.ShouldBe("*");
        address.Port.ShouldBe(4725);
        address.BasePath.ShouldBeNull();
    }

    [Test]
    public void ToListenUrl_UsesParsedHostAndPort_NotDefaults()
    {
        // Both the host and the port differ from the defaults, and the port
        // differs from 4723 specifically. An earlier version of this test used
        // 4723 and was insensitive to the manipulation it was written to
        // detect: ToListenUrl could have hardcoded the default port and passed.
        ServerAddress address = ServerAddress.Parse(["10.0.0.10", "4725/wd/hub"]);

        address.ToListenUrl().ShouldBe("http://10.0.0.10:4725");
    }

    [Test]
    public void ToListenUrl_OmitsBasePath()
    {
        // The base path is applied by UsePathBase, not by the listen URL — Kestrel
        // binds an origin. Encoding the path here would bind a URL Kestrel rejects.
        //
        // Separate from the test above because it measures a different variable:
        // that one asks whether the parsed values are used, this one asks whether
        // the path is excluded. Combined, a single assertion could not say which
        // of the two failed.
        ServerAddress withPath = ServerAddress.Parse(["10.0.0.10", "4725/wd/hub"]);
        ServerAddress withoutPath = ServerAddress.Parse(["10.0.0.10", "4725"]);

        // The control: the base path must make no difference to the listen URL.
        withPath.ToListenUrl().ShouldBe(withoutPath.ToListenUrl());
        withPath.ToListenUrl().ShouldNotContain("wd");
    }

    // Asserting only the exception TYPE would be an unfaithful instrument here:
    // every rejection below throws FormatException, so a validator that rejected
    // everything — including valid input — would pass all five cases. The message
    // is what distinguishes which rule fired, so that is what is measured.
    [TestCase("0", "outside the valid range")]
    [TestCase("65536", "outside the valid range")]
    [TestCase("-1", "not a valid port number")]
    [TestCase("notaport", "not a valid port number")]
    [TestCase("4723/", "Base path cannot be empty")]
    public void Parse_InvalidPortSpecification_ThrowsNamingTheRuleThatFailed(
        string portArgument,
        string expectedReason)
    {
        FormatException exception = Should.Throw<FormatException>(
            () => ServerAddress.Parse([portArgument]));

        exception.Message.ShouldContain(expectedReason);
    }

    [Test]
    public void Parse_ValidPortSpecification_DoesNotThrow()
    {
        // The control for the rejection cases above. Without it, "rejects bad
        // input" and "rejects all input" are indistinguishable.
        Should.NotThrow(() => ServerAddress.Parse(["4723"]));
        Should.NotThrow(() => ServerAddress.Parse(["1"]));
        Should.NotThrow(() => ServerAddress.Parse(["65535"]));
        Should.NotThrow(() => ServerAddress.Parse(["4723/wd/hub"]));
    }

    [Test]
    public void Parse_TooManyArguments_Throws()
    {
        Should.Throw<FormatException>(() => ServerAddress.Parse(["10.0.0.10", "4723", "extra"]));
    }

    [Test]
    public void Parse_StopsAtTheFirstSwitch_LeavingHostBuilderArgumentsAlone()
    {
        // WinAppDriver's arguments are positional, but the same array also reaches
        // the ASP.NET host builder, which owns --key value switches. Treating a
        // switch as a positional argument makes "WindowsDriverCore.exe 4725
        // --environment Development" fail with "got 3", which is how this was
        // found: WebApplicationFactory boots the entry point with its own
        // switches appended.
        ServerAddress address = ServerAddress.Parse(["4725", "--environment", "Development"]);

        address.Host.ShouldBe("127.0.0.1");
        address.Port.ShouldBe(4725);
    }

    [Test]
    public void Parse_NegativeNumber_IsAPositionalArgument_NotASwitch()
    {
        // "-1" starts with a dash but is a (bad) port, not a host-builder switch.
        // Treating it as a switch would make the port argument vanish and bind
        // the default port silently, which is worse than rejecting it. The
        // rejection case above covers the message; this pins why it still reaches
        // the port parser at all.
        Should.Throw<FormatException>(() => ServerAddress.Parse(["-1"]))
            .Message.ShouldContain("not a valid port number");
    }

    [Test]
    public void Parse_OnlySwitches_UsesDefaults()
    {
        ServerAddress address = ServerAddress.Parse(["--environment", "Development"]);

        address.Host.ShouldBe(ServerAddress.DefaultHost);
        address.Port.ShouldBe(ServerAddress.DefaultPort);
        address.BasePath.ShouldBeNull();
    }

    [Test]
    public void Parse_SwitchBeforePositional_DoesNotConsumeThePositional()
    {
        // The control: stopping at the first switch must not silently swallow
        // arguments after it. A parser that filtered switches out rather than
        // stopping would read "4725" here and pass the test above for the wrong
        // reason.
        ServerAddress address = ServerAddress.Parse(["--environment", "Development", "4725"]);

        address.Port.ShouldBe(ServerAddress.DefaultPort);
    }

    /// <summary>
    /// Environment overrides, which exist so this driver can run beside the
    /// thing it is measured against.
    /// </summary>
    /// <remarks>
    /// 4723 belongs to WinAppDriver <i>and</i> to Appium, so on a working
    /// machine it is often already bound. The default stays 4723 because an
    /// existing suite must point here unchanged; the variable moves the port
    /// without touching the arguments that suite passes.
    /// </remarks>
    [TestFixture]
    public sealed class EnvironmentOverrides
    {
        private static Func<string, string?> Environment(params (string Name, string Value)[] entries) =>
            name => entries.FirstOrDefault(entry => entry.Name == name).Value;

        [Test]
        public void NoArgumentsAndNoEnvironment_IsWinAppDriversAddress()
        {
            ServerAddress address = ServerAddress.Parse([], Environment());

            address.Host.ShouldBe(ServerAddress.DefaultHost);
            address.Port.ShouldBe(4723);
        }

        [Test]
        public void ThePortVariable_MovesTheServer()
        {
            ServerAddress address = ServerAddress.Parse(
                [], Environment((ServerAddress.PortEnvironmentVariable, "4899")));

            address.Port.ShouldBe(4899);
            address.Host.ShouldBe(ServerAddress.DefaultHost, "only the port was overridden");
        }

        [Test]
        public void TheHostVariable_MovesTheInterface()
        {
            ServerAddress address = ServerAddress.Parse(
                [], Environment((ServerAddress.HostEnvironmentVariable, "0.0.0.0")));

            address.Host.ShouldBe("0.0.0.0");
            address.Port.ShouldBe(4723, "only the host was overridden");
        }

        [Test]
        public void AnExplicitPortArgument_BeatsTheEnvironment()
        {
            // The precedence that matters. A stray variable in a shell must not
            // silently move a server somebody asked to be somewhere specific —
            // it would start, look healthy, and answer nothing the suite sent.
            ServerAddress address = ServerAddress.Parse(
                ["4727"], Environment((ServerAddress.PortEnvironmentVariable, "4899")));

            address.Port.ShouldBe(4727);
        }

        [Test]
        public void AnExplicitHostAndPort_BeatTheEnvironment()
        {
            ServerAddress address = ServerAddress.Parse(
                ["10.0.0.10", "4725"],
                Environment(
                    (ServerAddress.PortEnvironmentVariable, "4899"),
                    (ServerAddress.HostEnvironmentVariable, "0.0.0.0")));

            address.Host.ShouldBe("10.0.0.10");
            address.Port.ShouldBe(4725);
        }

        [TestCase("not-a-number")]
        [TestCase("0")]
        [TestCase("65536")]
        [TestCase("-1")]
        [TestCase("4723.5")]
        public void AnUnusablePortVariable_Throws_RatherThanFallingBack(string value)
        {
            // Falling back to 4723 would start a server on the port the caller
            // was explicitly avoiding, and the only symptom would be a collision
            // later — or worse, no collision and a suite talking to the wrong
            // server.
            FormatException failure = Should.Throw<FormatException>(
                () => ServerAddress.Parse([], Environment((ServerAddress.PortEnvironmentVariable, value))));

            failure.Message.ShouldContain(ServerAddress.PortEnvironmentVariable);
            failure.Message.ShouldContain(value);
        }

        [Test]
        public void AnEmptyVariable_IsTreatedAsUnset()
        {
            // Windows shells set variables to empty rather than removing them,
            // so empty must mean "not configured" rather than "invalid".
            ServerAddress address = ServerAddress.Parse(
                [], Environment(
                    (ServerAddress.PortEnvironmentVariable, ""),
                    (ServerAddress.HostEnvironmentVariable, "")));

            address.Port.ShouldBe(4723);
            address.Host.ShouldBe(ServerAddress.DefaultHost);
        }

        [Test]
        public void TheBasePathStillRidesOnThePortArgument()
        {
            // The override must not disturb the odd form WinAppDriver documents.
            ServerAddress address = ServerAddress.Parse(
                ["4723/wd/hub"], Environment((ServerAddress.PortEnvironmentVariable, "4899")));

            address.Port.ShouldBe(4723);
            address.BasePath.ShouldBe("/wd/hub");
        }
    }
}
