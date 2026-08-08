using System;
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
        ServerAddress address = ServerAddress.Parse(["*", "4723"]);

        address.Host.ShouldBe("*");
        address.Port.ShouldBe(4723);
    }

    [Test]
    public void ToListenUrl_ComposesSchemeHostAndPort_WithoutBasePath()
    {
        // The base path is applied by UsePathBase, not by the listen URL — Kestrel
        // binds an origin. Encoding the path here would bind a URL Kestrel rejects.
        ServerAddress address = ServerAddress.Parse(["10.0.0.10", "4723/wd/hub"]);

        address.ToListenUrl().ShouldBe("http://10.0.0.10:4723");
    }

    [TestCase("0")]
    [TestCase("65536")]
    [TestCase("-1")]
    [TestCase("notaport")]
    [TestCase("4723/")]
    public void Parse_InvalidPort_Throws(string portArgument)
    {
        Should.Throw<FormatException>(() => ServerAddress.Parse([portArgument]));
    }

    [Test]
    public void Parse_TooManyArguments_Throws()
    {
        Should.Throw<FormatException>(() => ServerAddress.Parse(["10.0.0.10", "4723", "extra"]));
    }
}
