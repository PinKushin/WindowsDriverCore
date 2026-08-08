using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// WinAppDriver does not use one response envelope, it uses five, and the
/// differences are not cosmetic. A void command omits <c>value</c> entirely
/// rather than sending null; <c>/sessions</c> and <c>DELETE /session</c> omit
/// <c>sessionId</c>; <c>/status</c> has no envelope at all.
///
/// Each test is anchored to a recording, so the measurement is "produces the
/// same JSON real WinAppDriver produced" rather than "produces something that
/// looks reasonable".
/// </summary>
[TestFixture]
public sealed class JsonWireResponseTests
{
    private static JsonElement RecordedBody(string recordingName)
    {
        RecordedResponse recorded = RecordedResponses.Named(recordingName);
        return JsonDocument.Parse(recorded.ResponseBody!).RootElement.Clone();
    }

    private static JsonElement Serialize<T>(T response)
    {
        // Default options deliberately. Every wire name is pinned with
        // JsonPropertyName on the record, and those attributes override any
        // naming policy, so a shared options instance would defend nothing.
        // An earlier version had one; applying a camelCase policy to it changed
        // no test, which is what proved it inert.
        string json = JsonSerializer.Serialize(response);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Test]
    public void SessionCommand_HasSessionIdStatusAndValue()
    {
        JsonElement recorded = RecordedBody("element.find.byAccessibilityId");

        JsonElement produced = Serialize(
            JsonWireResponse.ForSession("abc", new ElementReference("42.19466560.4.73")));

        produced.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.EnumerateObject().Select(p => p.Name));
        produced.GetProperty("status").GetInt32().ShouldBe(0);
        produced.GetProperty("value").GetProperty("ELEMENT").GetString()
            .ShouldBe("42.19466560.4.73");
    }

    [Test]
    public void ElementKey_StaysUpperCase_UnderTheConfiguredSerializer()
    {
        // The protocol mixes conventions: sessionId is camel case, ELEMENT is
        // upper case. The manipulation this detects is removal of the
        // JsonPropertyName attribute, which would let the property serialize as
        // "Element" and break every Selenium 3 client.
        //
        // Note what it does NOT detect, verified rather than assumed: adding a
        // camelCase naming policy changes nothing, because JsonPropertyName
        // overrides the policy. A test written against that manipulation would
        // have been unfalsifiable.
        JsonElement recorded = RecordedBody("element.find.byAccessibilityId");
        recorded.GetProperty("value").TryGetProperty("ELEMENT", out _).ShouldBeTrue(
            "guards the premise: the recording really uses the upper-case key");

        JsonElement produced = Serialize(new ElementReference("42.1.2.3"));

        produced.TryGetProperty("ELEMENT", out _).ShouldBeTrue();
        produced.TryGetProperty("element", out _).ShouldBeFalse();
    }

    [Test]
    public void VoidSessionCommand_OmitsValueEntirely_RatherThanSendingNull()
    {
        // The distinction that matters: `{"sessionId":…,"status":0}` and
        // `{"sessionId":…,"status":0,"value":null}` are different bytes, and a
        // client that checks for the presence of the key can tell them apart.
        JsonElement recorded = RecordedBody("element.click");
        recorded.TryGetProperty("value", out _).ShouldBeFalse(
            "guards the premise: the recording really has no value key");

        JsonElement produced = Serialize(JsonWireResponse.ForSessionVoid("abc"));

        produced.TryGetProperty("value", out _).ShouldBeFalse();
        produced.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.EnumerateObject().Select(p => p.Name));
    }

    [Test]
    public void ServerCommand_OmitsSessionId()
    {
        // GET /sessions is not scoped to a session, so it carries no sessionId.
        JsonElement recorded = RecordedBody("sessions.list");
        recorded.TryGetProperty("sessionId", out _).ShouldBeFalse();

        JsonElement produced = Serialize(JsonWireResponse.ForServer(Array.Empty<string>()));

        produced.TryGetProperty("sessionId", out _).ShouldBeFalse();
        produced.GetProperty("status").GetInt32().ShouldBe(0);
        produced.TryGetProperty("value", out _).ShouldBeTrue();
    }

    [Test]
    public void VoidServerCommand_HasStatusOnly()
    {
        // DELETE /session drops both sessionId and value.
        JsonElement recorded = RecordedBody("session.delete");

        JsonElement produced = Serialize(JsonWireResponse.ForServerVoid());

        produced.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.EnumerateObject().Select(p => p.Name));
        produced.GetProperty("status").GetInt32().ShouldBe(0);
    }

    [Test]
    public void Fault_HasStatusAndValue_ButNoSessionId()
    {
        JsonElement recorded = RecordedBody("error.noSuchElement.accessibilityId");

        JsonElement produced = Serialize(JsonWireResponse.ForFault(
            WebDriverFault.NoSuchElement,
            "An element could not be located on the page using the given search parameters."));

        produced.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.EnumerateObject().Select(p => p.Name));
        produced.GetProperty("status").GetInt32()
            .ShouldBe(recorded.GetProperty("status").GetInt32());
        produced.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe(recorded.GetProperty("value").GetProperty("error").GetString());
        produced.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe(recorded.GetProperty("value").GetProperty("message").GetString());
    }

    [Test]
    public void Fault_CarriesTheCallSiteMessage_NotOneDerivedFromTheFault()
    {
        // Status 23 appears with two different messages depending on cause — a
        // closed window versus a handle that was never valid. The message is
        // therefore a property of the call site, not of the fault, and a design
        // that derived it from the fault could not produce both.
        JsonElement closed = RecordedBody("error.element.afterWindowClosed.text");
        JsonElement badHandle = RecordedBody("error.window.switchBadHandle");

        closed.GetProperty("status").GetInt32()
            .ShouldBe(badHandle.GetProperty("status").GetInt32());
        closed.GetProperty("value").GetProperty("message").GetString()
            .ShouldNotBe(badHandle.GetProperty("value").GetProperty("message").GetString());

        string closedMessage = closed.GetProperty("value").GetProperty("message").GetString()!;
        JsonElement produced = Serialize(
            JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, closedMessage));

        produced.GetProperty("value").GetProperty("message").GetString().ShouldBe(closedMessage);
    }

    [Test]
    public void ServerStatus_HasNoEnvelopeAtAll()
    {
        // /status is the one route with no status field and no value wrapper.
        JsonElement recorded = RecordedBody("status");

        JsonElement produced = Serialize(new ServerStatus(
            new BuildInfo("1.2.2009", "2003", "Wed Aug 26 07:56:06 2020"),
            new OperatingSystemInfo("amd64", "windows", "10.0.26200")));

        produced.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.EnumerateObject().Select(p => p.Name));
        produced.TryGetProperty("status", out _).ShouldBeFalse();
        produced.TryGetProperty("value", out _).ShouldBeFalse();
        produced.GetProperty("os").GetProperty("name").GetString().ShouldBe("windows");
    }

    [Test]
    public void EveryEnvelope_UsesStatusZeroForSuccess()
    {
        // Control across the shapes: a success envelope that reported a non-zero
        // status would still satisfy every key-name assertion above.
        IEnumerable<string> successRecordings = RecordedResponses.All
            .Where(r => r.HttpStatus == 200
                        && r.Name != "status"
                        && !string.IsNullOrEmpty(r.ResponseBody))
            .Select(r => r.ResponseBody!);

        foreach (string body in successRecordings)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        }
    }
}
