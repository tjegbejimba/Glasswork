---
name: tdd
description: Test-driven development with red-green-refactor loop. Use when building features or fixing bugs in Glasswork.
---

# Test-Driven Development

> Copied from the local Copilot CLI skill of the same name. The cloud coding
> agent does not have access to the CLI's skill registry, so this is committed
> into the repo. Glasswork-specific addenda are at the bottom.

## Philosophy

**Core principle**: Tests should verify behavior through public interfaces, not implementation details. Code can change entirely; tests shouldn't.

**Good tests** are integration-style: they exercise real code paths through public APIs. They describe _what_ the system does, not _how_ it does it. A good test reads like a specification - "user can checkout with valid cart" tells you exactly what capability exists. These tests survive refactors because they don't care about internal structure.

**Bad tests** are coupled to implementation. They mock internal collaborators, test private methods, or verify through external means (like querying a database directly instead of using the interface). The warning sign: your test breaks when you refactor, but behavior hasn't changed. If you rename an internal function and tests fail, those tests were testing implementation, not behavior.

## Anti-Pattern: Horizontal Slices

**DO NOT write all tests first, then all implementation.** This is "horizontal slicing" - treating RED as "write all tests" and GREEN as "write all code."

This produces **crap tests**:

- Tests written in bulk test _imagined_ behavior, not _actual_ behavior
- You end up testing the _shape_ of things (data structures, function signatures) rather than user-facing behavior
- Tests become insensitive to real changes - they pass when behavior breaks, fail when behavior is fine
- You outrun your headlights, committing to test structure before understanding the implementation

**Correct approach**: Vertical slices via tracer bullets. One test → one implementation → repeat. Each test responds to what you learned from the previous cycle. Because you just wrote the code, you know exactly what behavior matters and how to verify it.

```
WRONG (horizontal):
  RED:   test1, test2, test3, test4, test5
  GREEN: impl1, impl2, impl3, impl4, impl5

RIGHT (vertical):
  RED→GREEN: test1→impl1
  RED→GREEN: test2→impl2
  RED→GREEN: test3→impl3
  ...
```

## Workflow

### 1. Planning

Before writing any code:

- [ ] Identify the behaviors to test (not implementation steps)
- [ ] Prioritize: which behaviors are critical vs. nice-to-have
- [ ] If the issue is ambiguous about behavior, post a clarifying comment instead of guessing

**You can't test everything.** Focus testing effort on critical paths and complex logic, not every possible edge case.

### 2. Tracer Bullet

Write ONE test that confirms ONE thing about the system:

```
RED:   Write test for first behavior → test fails
GREEN: Write minimal code to pass → test passes
```

This is your tracer bullet — proves the path works end-to-end.

### 3. Incremental Loop

For each remaining behavior:

```
RED:   Write next test → fails
GREEN: Minimal code to pass → passes
```

Rules:

- One test at a time
- Only enough code to pass current test
- Don't anticipate future tests
- Keep tests focused on observable behavior

### 4. Refactor

After all tests pass, look for refactor candidates:

- [ ] Extract duplication
- [ ] Apply SOLID principles where natural
- [ ] Consider what new code reveals about existing code
- [ ] Run tests after each refactor step

**Never refactor while RED.** Get to GREEN first.

## Checklist Per Cycle

```
[ ] Test describes behavior, not implementation
[ ] Test uses public interface only
[ ] Test would survive internal refactor
[ ] Code is minimal for this test
[ ] No speculative features added
```

---

## Glasswork specifics

- **Framework**: MSTest. Do not introduce xUnit/NUnit/FluentAssertions.
- **Location**: `tests/Glasswork.Tests/`. One test file per production class,
  named `<ClassName>Tests.cs`. Follow existing patterns (see
  `ArtifactRowTests.cs`, `FileSystemArtifactStoreTests.cs`).
- **Run tests**: `dotnet test tests/Glasswork.Tests/Glasswork.Tests.csproj`
  on the Ubuntu cloud runner. The .NET 10 SDK is preinstalled by
  `.github/workflows/copilot-setup-steps.yml`.
- **Scope of TDD on this repo**: applies to `Glasswork.Core` (cross-platform,
  pure C#). `Glasswork.App` (WinUI 3) cannot be unit-tested in the cloud
  agent's Linux environment — for app-layer changes, write Core tests for
  any extracted logic and clearly mark UI-only paths in your PR description.
- **Pre-existing flakes**: three debounce tests
  (`DebouncesBurstsIntoOneEvent`, `Trigger_FiresAgainAfterQuietPeriodElapses`,
  and one related) are timing-sensitive and may fail intermittently on slow
  runners. Don't "fix" them as part of an unrelated change.
