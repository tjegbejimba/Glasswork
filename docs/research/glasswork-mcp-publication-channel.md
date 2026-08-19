# Publication channel for the `glasswork-mcp` global tool

**Access date:** 2026-08-19
**Method:** primary NuGet, .NET, and GitHub documentation only.

## Question

Which durable channel can publish independently versioned `glasswork-mcp`
packages without creating an MCP-only GitHub Release that becomes the desktop
app's `/releases/latest` update signal?

## Findings

### NuGet.org is immutable and anonymous to read

NuGet.org package owners cannot permanently delete a published package version;
they can only unlist it, and an exact-version client can still download an
unlisted package. Permanent deletion is reserved for exceptional NuGet.org
policy cases. This preserves the identity of an already published semantic
version.

Source: [Deleting packages from nuget.org](https://learn.microsoft.com/nuget/nuget-org/policies/deleting-packages)

NuGet's V3 Package Base Address lists both listed and unlisted versions and
provides predictable exact-version package and manifest URLs. A missing package
ID or version returns `404`.

Source: [Package Content, NuGet API](https://learn.microsoft.com/nuget/api/package-base-address-resource)

Public NuGet.org packages install through the default .NET source with an exact
`--version`; no consumer token or custom feed configuration is required.

Sources:

- [`dotnet tool install`](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-install)
- [`dotnet tool update`](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-update)

### GitHub Packages requires authenticated reads

GitHub's NuGet registry requires a classic personal access token to install
public as well as private/internal packages. GitHub Packages also permits an
administrator to delete public package versions below its documented download
threshold and restore them for 30 days. That is useful for private distribution
but weaker than the anonymous, non-deletable public contract required here.

Sources:

- [Working with the NuGet registry](https://docs.github.com/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [Deleting and restoring a package](https://docs.github.com/packages/learn-github-packages/deleting-and-restoring-a-package)

### Trusted publishing removes long-lived publisher secrets

NuGet.org trusted publishing exchanges a GitHub Actions OIDC token for a
single-use API key valid for one hour. The configured policy binds the
repository owner, repository, workflow filename, and optionally a GitHub
environment. NuGet.org notes that the feature is still rolling out, so account
availability must be confirmed before the first publication.

Source: [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)

### Same-version local packages are inherently stale-prone

NuGet checks its global packages folder before package sources when the exact
ID/version is already present. `--add-source` also searches feeds in parallel;
it does not prioritize the newly added local feed. Repacking changed bits under
an unchanged version therefore violates NuGet's immutable identity model and
can preserve an older cached package.

Sources:

- [Managing global packages and cache folders](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders)
- [`dotnet tool update --add-source`](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-update)

## Decision support

Use NuGet.org with trusted publishing. Publish through a dedicated workflow
that creates an annotated `mcp-vX.Y.Z` integrity tag but no GitHub Release.
Installers should select an exact version, verify the published package against
the tag's checksum and source commit, stage it in an isolated cache, and verify
the executable build identity before replacing an installed tool.

This leaves ADR 0012's app Release workflow and `/releases/latest` signal
unchanged. Ephemeral Actions artifacts may retain publication evidence, but are
not the distribution channel.

## Setup blocker

The first real publication requires a nuget.org owner with a trusted-publishing
policy for `tjegbejimba/Glasswork` and workflow `publish-mcp.yml`, plus the
repository variable `NUGET_USER`. Package publication remains blocked until
that external account configuration exists.
