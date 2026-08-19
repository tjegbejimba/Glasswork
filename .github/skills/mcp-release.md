---
name: mcp-release
description: Prepare and publish an independently versioned glasswork-mcp release through NuGet.org. Use for MCP tool release, package publication, exact MCP install, or mcp-v tags.
---

# glasswork-mcp release

MCP publication is separate from Glasswork app Release publication. Read ADR
0022 before changing this flow.

## Prepare

1. Compare `main` with the latest `mcp-vX.Y.Z` tag and confirm substantive MCP
   changes exist.
2. Choose a version while staying in `0.x`:
   - additive or breaking public MCP tool/CLI shape: minor bump;
   - compatible implementation, performance, or packaging fix: patch bump.
3. Prepare a reviewable MCP Release PR that commits the new
   `src\Glasswork.Mcp\Glasswork.Mcp.csproj` version and a dated matching heading
   in `src\Glasswork.Mcp\CHANGELOG.md`.
4. Run:

   ```powershell
   . .\scripts\Validate-McpReleasePublication.ps1
   Test-McpReleasePublicationInputs -RepoRoot $PWD -Version X.Y.Z
   Invoke-Pester -Path tests\scripts -Output Detailed -CI
   dotnet test tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj --configuration Release --nologo --verbosity minimal -- MSTest.Parallelize.Workers=1
   dotnet build src\Glasswork.Mcp\Glasswork.Mcp.csproj --configuration Release --nologo --verbosity minimal
   ```

The prepare step is complete only when the PR contains the intended version and
changelog entry and all available gates pass.

## Publish

After the MCP Release PR lands on `main`:

1. Confirm repository variable `NUGET_USER` and the nuget.org trusted-publishing
   policy for `publish-mcp.yml` exist.
2. Dispatch `Publish MCP` on `main` with input `version=X.Y.Z`.
3. Monitor the run to completion.
4. Verify the exact NuGet.org package and annotated `mcp-vX.Y.Z` tag exist.
5. Run `scripts\install-mcp.ps1 -Version X.Y.Z` and verify
   `glasswork-mcp --version` reports `X.Y.Z+<tag-commit>`.

Only the workflow publishes packages or creates MCP tags. A package or tag
collision, missing trusted-publisher configuration, failed gate, or partial
state blocks publication. MCP publication never creates a GitHub Release,
changes `vX.Y.Z` app tags, or edits app release notes.
