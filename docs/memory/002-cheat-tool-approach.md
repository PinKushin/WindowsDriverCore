# 002 — Cheat-Tool-Level Control, Not WinAppDriver's Approach

## Date: 2026-08-02

## User Direction
Don't copy WinAppDriver's approach. The user wants cheat-tool-level control:
- Raw COM, no managed wrappers hiding behavior
- Direct pointer manipulation where it makes sense
- `unsafe` code for performance-critical paths (element iteration, property reads)
- Transparent, predictable behavior — no hidden retries, no hidden caching, no hidden exception translation

## Why Not WinAppDriver's Approach
WinAppDriver wraps `IUIAutomation` behind managed `AutomationElement` objects. This:
- Adds a layer that can silently swallow errors or change behavior
- Makes debugging harder (exceptions thrown from the framework, not your code)
- Prevents direct control over COM lifetime, caching, and tree traversal
- The managed layer IS the source of bugs #857 and #1079

## What "Cheat Tool" Means Here
- You own the COM pointers. You decide when to release them.
- You own the tree traversal. No framework deciding "this element isn't relevant."
- You own the caching strategy. Not some `AutomationElement.FromHandle` cache you can't inspect.
- You can use `unsafe` to read element arrays directly without boxing/unboxing overhead.
- You can add tree-walking algorithms that don't exist in `System.Windows.Automation`.

## Boundaries
- Still use the standard COM interfaces (`IUIAutomation`, `IUIAutomationElement`) — those are the real API
- Don't re-invent the COM vtable layout — that's defined by Microsoft
- The "raw" part is HOW we interact with the COM objects, not WHAT COM objects we use
