# What WinAppDriver's users actually ask for

Read from `microsoft/WinAppDriver` on 2026-08-11: **1155 open issues, 49 open
pull requests**. The PRs are a dead end — almost all Dependabot bumps and
documentation edits, the newest substantive one from 2024. The issues are the
signal.

Sorted by reactions, the meta-issues dominate the top — *"Is WinAppDriver dead?"*
(57), *"State of WinAppDriver"* (51), *"Open Source WinAppDriver"* (47),
*"Update on WinAppDriver"* (46, 140 comments). That is the market this project
exists for and it needs no further comment.

Below those, the technical asks cluster into six things.

## 1. W3C WebDriver and Selenium 4 — the largest cluster

| issue | reactions | title |
|---|---|---|
| #1610 | 16 | WinAppDriver using deprecated JSON wire protocol |
| #1839 | 15 | Support for Selenium 4 |
| #1997 | 6 | Update WinAppDriver to support Selenium 4 (Python/C#) |
| #1543 | 5 | Invalid or unsupported capabilities (`app`, `appArguments`) |

**~42 reactions, and it is a real incompatibility rather than a preference.**
Selenium 4 dropped JSON Wire Protocol support, so a current Selenium cannot drive
WinAppDriver at all.

**This is in direct tension with this project's stated contract**, which is JWP
and says "where they disagree, JWP wins". That choice is right for passing the
compatibility suite and wrong for the largest thing users are asking for. Serving
BOTH — JWP for the existing suites, W3C for Selenium 4 — addresses the top demand
without giving up the floor. It is not a small piece of work and it should be a
deliberate decision rather than a drift.

## 2. SendKeys uses the wrong keyboard layout

| issue | reactions | title |
|---|---|---|
| #215 | 11 | unknown keyboard configuration instead of local (azerty vs qwerty) |
| #446 | 11 | sendKeys applies QWERTY-style on German keyboard setting |
| #507 | 9 | SendKeys not always sends correct keys (Swedish, English) |

**31 reactions across three issues, all the same defect.** Anyone outside a
US-QWERTY layout gets wrong characters. Squarely fixable: send scan codes or map
through the active layout rather than assuming one.

## 3. Waiting

| issue | reactions | title |
|---|---|---|
| #330 | 12 | Implement wait for the app to launch the main window, like winium |
| #117 | 9 | Auto wait until next screen gets loaded, like selenium |

**#330 is already done here** — `MainWindowWaiter`, and as of 2026-08-11 it no
longer returns a dead handle when it gives up. **#117 is implicit wait**, which
this driver accepts and ignores, and which the same day's transcript showed
costing 80 finds that answered "no such element".

## 4. Touch and pointer input

| issue | reactions | title |
|---|---|---|
| #1328 | 8 | Windows10 swipe: `touchAction.Perform()` with `MoveTo()` → UnsupportedCommand |

Directly the gap measured here: 16 Touch and 9 Pen tests that WinAppDriver passes
and this driver does not.

## 5. Developer Mode is a deployment blocker

| issue | reactions | title |
|---|---|---|
| #975 | 5 | DeveloperMode requirement is a roadblock for QA |
| #1150 | 5 | not working without Developer mode — any alternative? |

Worth knowing whether this driver needs it at all. It has never been tested
without.

## 6. Property access

| issue | reactions | title |
|---|---|---|
| #74 | 10 | unable to extract XAML properties via `GetAttribute()` |
| #998 | 6 | Support for Accessibility Patterns |
| #644 | 10 | `FindElementByWindowsUIAutomation()` for top-level windows from Root |

## What this changes about priorities

The measured gap against WinAppDriver on the guest is 98 tests, and pointer input
is 30 of them. But **the suite is not the market.** Nothing in the compatibility
suite measures Selenium 4 support, keyboard layouts, or Developer Mode — and those
are what people filed issues about.

Both are worth serving, and they are not the same list. A test score says whether
this driver can replace WinAppDriver; these issues say whether anyone would want
it to.
