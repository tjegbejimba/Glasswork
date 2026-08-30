# Copilot session launch guidance

PRD #505 records the accepted boundary for Parent Task orchestration: Glasswork
publishes copied command handoffs, while direct Copilot CLI or app launch is
deferred. Issue #514 applies that boundary to Parent Task lifecycle commands.

## Current implementation boundary

- Glasswork may format and copy stable Start work, Resume, Wrap up, and Refresh
  summary commands.
- A Parent Task agent may inspect the durable descendant tree and present a
  bounded plan containing ready work, blockers, proposed session count,
  concurrency limit, and intentionally unstarted work.
- Decomposition writes require their own confirmation. Fan-out requires a
  separate explicit approval.
- Approval produces copyable child Task handoff commands only. Neither the app
  nor the lifecycle skills launch Copilot, start a process, invoke a subagent,
  or claim that a session was created.
- Parent Resume can rely only on durable session references recorded in Task
  Links, Notes, or Artifacts. Process state and chat history are not Task domain
  state.

## Deferred research

Any future direct-launch design must separately define session identity,
durable Task-to-session linkage, cancellation semantics, concurrency
enforcement, partial-launch recovery, and user approval UX. It must not be
inferred from the copied-command implementation.

Source: [PRD #505](https://github.com/tjegbejimba/Glasswork/issues/505),
**Parent Agent Workflows** and **Non-goals**.
