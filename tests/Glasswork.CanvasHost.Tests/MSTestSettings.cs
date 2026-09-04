using Microsoft.VisualStudio.TestTools.UnitTesting;

// Unlike Glasswork.Tests/Glasswork.Mcp.Tests (pure in-memory unit tests that
// opt into method-level parallelism), every test in this project is a
// black-box boundary test that spawns a real Glasswork.CanvasHost.exe child
// process and talks to it over a real loopback HTTP port, often sharing a
// real UI State file path across a sequence of hosts within one test. Running
// these concurrently — whether via an explicit opt-in or an adapter default —
// pits multiple cold-starting .NET processes against each other for CPU on
// the same machine, which is exactly the kind of contention that produced
// intermittent truncated responses, stale reads, and 500s when this suite
// was first wired into CI (issue #563). DoNotParallelize makes the required
// sequential execution explicit instead of relying on an implicit default.
[assembly: DoNotParallelize]
