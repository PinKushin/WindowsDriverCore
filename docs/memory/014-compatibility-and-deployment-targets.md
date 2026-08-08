# 014 — Compatibility Floor and Deployment Targets

## Date: 2026-08-08

Goal stated by the user: **run anywhere WinAppDriver runs**, including GitHub-hosted Windows
runners, so existing/older setups can point at this server instead. This entry records the measured
floor and the specific gaps.

## The OS floor is not the problem

Per Microsoft's [.NET on Windows support table](https://learn.microsoft.com/dotnet/core/install/windows#supported-versions),
.NET 10 (x64) is supported on:

- Windows 11 — all servicing versions
- Windows 10 — 21H2, 1809, 1607 (LTSC/Enterprise only)
- Windows Server — 2025, 23H2, 2022, 2019, 2016, 2012 R2, 2012
- Windows Server Core and Nano Server — 2025, 2022, 2019, 2016 (+2012 R2/2012 for Core)

WinAppDriver's own README scopes it to "Windows 10 PCs". **.NET 10 reaches further back than
WinAppDriver does**, so the target framework is not what limits us.

## What actually sets our floor

Every native API the server calls, with the OS that introduced it:

| API | Introduced | Used by | Guarded? |
|---|---|---|---|
| `IUIAutomation` (UIA 3.0) | Windows 7 | all element work | n/a |
| Toolhelp32 `Process32FirstW/NextW` | Windows 2000 | `Win32.GetChildProcessIds` | n/a |
| `IApplicationActivationManager` | Windows 8 | UWP launch | try/catch → Edge fallback |
| `GetDpiForWindow` | Windows 10 1607 | `Win32.GetDpiScale` | yes → falls back to scale 1.0 |
| `SetProcessDpiAwarenessContext` | Windows 10 1703 | `Program.cs:15` | yes → no-op |

**Effective floor: Windows 10 1607 / Windows Server 2016** for full behaviour, degrading (wrong DPI
scaling, no UWP) rather than crashing below that. That is the same floor WinAppDriver has. No code
change needed to hit the compatibility goal — the gaps are all in packaging and the HTTP surface.

## GitHub-hosted runner reality (verified 2026-08-08)

Available Windows images are `windows-2025` (= `windows-latest`, Server 2025), `windows-2022`
(Server 2022), and `windows-11-arm` (Windows 11 Arm64). **`windows-2019` is gone.** All of them
support .NET 10, so nothing about the runner fleet constrains us.

Two consequences:

- `windows-11-arm` means **`win-arm64` is a first-class RID**, not a nice-to-have. WinAppDriver only
  ever shipped x86/x64, so this is an area where we can beat it rather than match it.
- We need no MSI, no admin install, and **no Developer Mode** — WinAppDriver requires Developer Mode
  to be enabled. On a hosted runner that is one less setup step. Worth stating in the README as a
  selling point.

## Gap 1 — deployment prerequisite

WinAppDriver is a single MSI on .NET Framework, which is in-box on every supported Windows. It has
no prerequisite. We currently need the **.NET 10 Desktop Runtime** installed, because
`WindowsDriverCore.csproj` carries:

```xml
<FrameworkReference Include="Microsoft.WindowsDesktop.App" />
```

That reference exists only to get `System.Drawing` for screenshot capture. It drags in WPF and
WinForms for a headless HTTP server.

Two fixes, both worth doing:

1. Replace the framework reference with the `System.Drawing.Common` NuGet package. Drops the Desktop
   Runtime requirement entirely — ASP.NET Core runtime alone becomes sufficient, and the
   self-contained output gets substantially smaller.
2. Ship `dotnet publish -r <rid> --self-contained` output for `win-x64`, `win-x86`, `win-arm64`.
   Larger download, but zero prerequisites on the target machine — actual parity with the MSI.

**Native AOT is not available.** `Marshal.ReleaseComObject` and built-in COM interop are
unsupported under AOT (`BuiltInComInterop.IsSupported` is false). Getting there would mean
rewriting the whole COM layer on `ComWrappers`. Do not start down that path casually; note it and
move on.

## Gap 2 — no CLI arguments, no `/wd/hub`, loopback only

`Program.cs:18` hardcodes `builder.WebHost.UseUrls("http://127.0.0.1:4723")`. WinAppDriver's
documented command line is:

```
WinAppDriver.exe                          # defaults to 127.0.0.1:4723
WinAppDriver.exe <IP> <port>              # e.g. WinAppDriver.exe 10.0.0.10 4723
WinAppDriver.exe <IP> <port>/wd/hub       # port and base path in one argument
WinAppDriver.exe * 4723                   # '*' binds all interfaces
```

Confirmed against `WinAppDriver/README.md:15`, `Docs/RunningOnRemoteMachine.md:21`,
`Docs/FAQ.md:172`, `Docs/UsingAppium.md:34`, `Docs/SeleniumGrid.md:38`.

Three separate breaks for existing setups:

- **No `/wd/hub` base path.** Appium clients and Selenium Grid node configs routinely use
  `http://127.0.0.1:4723/wd/hub`. Every one of those points at us and 404s. This is the single
  highest-value compatibility fix and it is cheap — serve every route under both `/` and `/wd/hub`.
- **No port/IP override.** Cannot run two instances, cannot avoid a port conflict, cannot follow the
  Selenium Grid docs.
- **Loopback only.** `Docs/RunningOnRemoteMachine.md` describes the remote/Grid scenario that the
  `*` binding exists for. We cannot serve it at all.

Note the argument shape is odd on purpose — the base path rides on the *port* argument
(`4723/wd/hub`), not as a third argument. Parse it the way they did, or existing scripts break.

## Gap 3 — response envelope

Not verified yet, but flagged: WinAppDriver speaks JSON Wire Protocol, whose success envelope is
`{"sessionId": ..., "status": 0, "value": ...}`. `Messages/WebDriverResponse.cs` emits only
`{"value": ...}` (W3C shape). `/sessions` at `SessionRoutes.cs:128` hand-rolls a `status = 0` field,
which suggests someone hit this once and patched one endpoint. Worth an explicit test against an
old client before assuming the W3C shape is universally accepted.

## Recommended order for the compatibility work

1. `/wd/hub` dual-mount — unblocks every existing Appium/Grid config.
2. CLI argument parsing matching WinAppDriver's exact shape, including `*`.
3. Swap `Microsoft.WindowsDesktop.App` for `System.Drawing.Common`.
4. Self-contained publish for win-x64 / win-x86 / win-arm64, and a CI matrix over
   `windows-2022`, `windows-2025`, `windows-11-arm` that actually runs the suite on each.
5. Verify the JSON Wire success envelope against the old client.

Relates to [[006-w3c-vs-json-wire-protocol]], [[013-architecture-audit]].
