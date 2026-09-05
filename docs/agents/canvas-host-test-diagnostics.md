# CanvasHost test diagnostics

`Glasswork.CanvasHost.Tests` is a sequential black-box suite that starts the
real host and calls it over loopback HTTP. Its harness owns bounded failure
evidence and test-created process/file cleanup.

## Failure codes

| Code | Meaning |
|---|---|
| `GWCH_HTTP_EMPTY_BODY` | A declared JSON response was empty or whitespace-only. |
| `GWCH_HTTP_CONTENT_TYPE` | A declared JSON response used a non-JSON content type. |
| `GWCH_HTTP_MALFORMED_JSON` | A non-empty JSON response could not be parsed. |
| `GWCH_REQUEST_TIMEOUT` | A request, body read, or overall poll exceeded its bound. |
| `GWCH_HOST_EXITED` | The host exited before readiness or during failed request observation. |
| `GWCH_STARTUP_TIMEOUT` | The host did not become ready within the startup bound. |
| `GWCH_TEARDOWN_TIMEOUT` | An owned host did not exit within the teardown bound. |
| `GWCH_TEMP_CLEANUP_FAILED` | Test-created temporary state could not be removed. |
| `GWCH_TEST_FAILURE_CONTEXT` | A normal assertion failed; the harness attached host context without replacing it. |

`GWCH_HTTP_EMPTY_BODY` recurred in CI runs
[33913338385](https://github.com/tjegbejimba/Glasswork/actions/runs/33913338385)
and
[33911578411](https://github.com/tjegbejimba/Glasswork/actions/runs/33911578411).
That evidence establishes the symptom, not connection pooling, teardown,
Kestrel, runner contention, or another cause.

## Output contract

Run the suite with:

```powershell
--logger "trx;LogFileName=canvas-host.trx" --results-directory TestResults\canvas-host
```

GitHub Actions uploads must use:

```text
TestResults\canvas-host\canvas-host.trx
TestResults\canvas-host\diagnostics\${{ github.run_id }}-${{ github.run_attempt }}\*.json
```

The producer writes:

- `TestResults\canvas-host\canvas-host.trx`
- `TestResults\canvas-host\diagnostics\<run-id>-<run-attempt>\*.json`
  on completed GitHub Actions test failures

A completed passing run may have no diagnostics. Build/discovery failure or a
crashed/external-terminated test runner can leave either output absent, so
artifact upload must use `if: always()` and tolerate missing files without
masking the test step's result.

Each GitHub Actions attempt writes to the generation derived from
`GITHUB_RUN_ID-GITHUB_RUN_ATTEMPT`; local runs use
`local-<test-process-id>-<invocation-guid>`. Hosted identity components must
both be present, numeric, and bounded (32 characters for the run ID and 8 for
the attempt). An incomplete or malformed hosted identity fails closed rather
than falling back to a local path. The assembly log prints the selected
diagnostics directory.
Uploads must select only the current GitHub generation. Assembly initialization
removes that generation before any test executes. If it cannot be removed,
initialization fails closed with `GWCH_TEMP_CLEANUP_FAILED`; it does not
continue and risk treating evidence from an earlier invocation in the same
generation as current. That pre-test failure may leave no new TRX or diagnostic
JSON, so upload still warns on missing files without masking the authoritative
test-step failure.

Diagnostics never include raw response bodies, authorization headers, command
arguments, environment dictionaries, tokens, or unredacted temporary Vault/UI
State paths. Host streams are sanitized and bounded by bytes, lines, and line
length.

After a readiness record, a failed health probe allows up to one bounded second
for a spontaneous process exit to become observable before teardown can kill
the host. An observed exit is classified as `GWCH_HOST_EXITED`, including when
the inner request first reported a timeout.

## Flake policy

There are no automatic retries. A known code changes routing and evidence
collection only; it never changes pass/fail semantics.

Any future retry requires a linked issue, named owner, expiry, and exact
signature allowlist. It may rerun an exact failed test once for diagnostics,
but the first failure remains authoritative and the job stays red.
