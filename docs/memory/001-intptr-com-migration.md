# 001 — Use IntPtr Everywhere for Low-Level COM

## Date: 2026-08-02

## Key Insight
If `hwnd` needs `IntPtr`, then ALL COM out parameters returning interface pointers need `IntPtr` too. Don't mix managed COM interfaces with raw pointers. The .NET COM marshaler fails with `InvalidCastException` when it tries to marshal a returned COM pointer into a managed interface it doesn't fully understand.

## What Broke
Defining `IUIAutomation` and `IUIAutomationElement` with `out IUIAutomationElement element` parameters caused "Specified cast is not valid" at runtime. The COM interop layer returned a pointer, the marshaler tried to wrap it in our custom interface definition, and failed.

## The Fix
- All `out` params returning COM interface pointers → `out IntPtr`
- Condition params → `IntPtr` (condition pointers are opaque handles, never call methods on them directly)
- Marshal with `Marshal.GetObjectForIUnknown(ptr)` + cast when needed
- `FindFirst`/`FindAll` take `IntPtr condition`, return `IntPtr element` / `IntPtr elementArray`

## Principle
Stay low-level. If it's a COM pointer, it's `IntPtr`. The managed wrapper (`RawAutomationElement`) owns the marshaling — callers never see raw pointers unless they want to. Use `unsafe` where performance matters (direct pointer arithmetic, fixed-size buffer access). Don't let the framework's "convenience" add hidden behavior or failure modes.

## Files Changed
- `Automation/Com/IUIAutomation.cs` — all out params → `IntPtr`
- `Automation/Com/IUIAutomationElement.cs` — all out params → `IntPtr`, condition params → `IntPtr`
- `Automation/Com/IUIAutomationCondition.cs` — `IUIAutomationElementArray.GetElement` → `IntPtr`
- `Automation/Raw/RawCondition.cs` — stores `IntPtr`, exposes `ConditionPtr`
- `Automation/Raw/ConditionFactory.cs` — marshals `IntPtr` → `RawCondition`
- `Automation/Raw/RawAutomationElement.cs` — stores `IntPtr`, marshals for method calls
