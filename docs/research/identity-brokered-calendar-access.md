# Research: Identity-Brokered Calendar Access Alternatives

**Status:** Research only. No production implementation.

**Question:** Which approved identity-brokered or MCP-based mechanisms can
provide Glasswork with today-only calendar context without requiring Glasswork
to own a corporate Entra app registration?

## Context

The existing
[Microsoft 365 calendar architecture](https://github.com/tjegbejimba/Glasswork/blob/c23a9c4f7768f240362f2ee4a04dba02689b8972/docs/research/microsoft-365-calendar-architecture.md)
assumes that Glasswork owns a public-client Entra registration. Provisioning
work later found that the target corporate tenant requires every registration
to carry a valid Service Tree reference, and that attaching Glasswork to an
unrelated service would misattribute ownership. This report investigates
whether a supported broker or alternate calendar source can avoid that
registration.

## Executive finding

No identity broker or Microsoft MCP surface eliminates the need for an Entra
application identity somewhere in the chain. WAM brokers sign-in but still
requires a client ID. Work IQ and Microsoft's Enterprise MCP Server require
registered, consented callers and are not embeddable calendar SDKs for a WinUI
product.

Two approaches avoid a Glasswork-owned corporate registration:

1. A published read-only ICS feed, when the tenant's Exchange sharing policy
   permits it.
2. A separately installed, user-operated local bridge that owns its identity
   and consent independently and exposes normalized calendar data to Glasswork.

The durable design is therefore a provider-neutral Calendar Context boundary
with Graph, ICS, local-bridge, and unavailable implementations. Planner already
requires calendar failure to degrade to Unknown capacity, so no provider must
be mandatory.

## Findings

### Work IQ MCP

Work IQ exposes Microsoft 365 operations to AI agents over MCP. Its
[permissions reference](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/permissions)
states that calling applications must authenticate through Entra OAuth. The
published `WorkIQAgent.Ask` delegated permission requires admin consent and is
broader than `Calendars.Read`.

Tenant enablement is also an administrator operation with a Copilot Studio
usage-based billing plan and Azure resources
([tenant enablement](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/enable-work-iq)).
Microsoft's [`work-iq`](https://github.com/microsoft/work-iq) repository
documents a CLI/plugin consumed by agent hosts such as GitHub Copilot CLI and
VS Code, not a supported runtime SDK for a separately compiled WinUI
application.

**Verdict:** Reject as a product dependency. It moves identity to a broader,
heavier agent platform and does not provide a supported registration bypass.

### Microsoft MCP Server for Enterprise

The
[Enterprise MCP Server](https://learn.microsoft.com/en-us/graph/mcp-server/overview)
is a preview, read-only Entra identity and directory surface. It has no calendar
tools and enforces the scopes granted to its registered MCP client.

**Verdict:** Reject. It is the wrong data domain and still requires caller
identity and consent.

### Copilot and agent connectors

[Copilot connectors](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/overview)
bring external data into Microsoft Search and Copilot. Teams
[agent connectors](https://learn.microsoft.com/en-us/microsoftteams/platform/m365-apps/agent-connectors)
expose an application's MCP server to Microsoft 365 agents. Both directions are
opposite to Glasswork reading Microsoft 365 calendar data.

**Verdict:** Not applicable.

### Windows Web Account Manager

WAM provides Windows SSO, brokered Conditional Access, and account integration.
Every documented MSAL flow still begins with
`PublicClientApplicationBuilder.Create(clientId)`
([WAM desktop flow](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)).

**Verdict:** Retain as the preferred sign-in experience if a Graph registration
later exists. It does not bypass registration.

### Outlook Object Model and COM

Classic Outlook exposes signed-in calendar data through
[`Microsoft.Office.Interop.Outlook`](https://learn.microsoft.com/en-us/visualstudio/vsto/outlook-object-model-overview)
without OAuth or an Entra registration. It provides broad access to the local
Outlook profile rather than a platform-enforced calendar-only permission.

Microsoft does not support VSTO or COM add-ins in new Outlook
([new Outlook guidance](https://learn.microsoft.com/en-us/office/dev/add-ins/outlook/one-outlook)).
That makes the mechanism dependent on a declining classic-Outlook surface.

**Verdict:** Do not use as a primary provider. It may be documented as a
legacy-only fallback for machines that retain classic Outlook.

### Exchange Web Services

Modern EWS access still requires OAuth application identity. Exchange Online
begins disabling EWS in October 2026 and permanently retires it on April 1,
2027
([EWS retirement](https://learn.microsoft.com/en-us/exchange/client-developer/exchange-web-services/deprecation-of-ews-in-exchange-online)).

**Verdict:** Reject.

### Office.js

Outlook Office.js APIs execute inside an Outlook-hosted add-in and expose the
current item rather than an arbitrary day's calendar range
([Outlook add-in APIs](https://learn.microsoft.com/en-us/office/dev/add-ins/outlook/apis)).

**Verdict:** Reject for a standalone WinUI Page.

### Local Outlook cache

No supported first-party API was found for directly reading Outlook OST cache
files. Supported local access routes through the declining Outlook Object
Model; supported cloud access routes through Microsoft Graph.

**Verdict:** Reject undocumented cache parsing.

### Published ICS

Outlook can publish a read-only calendar as an ICS feed without OAuth, an Entra
registration, or a Graph call. Availability is governed by the Exchange
sharing policy's anonymous-calendar setting
([publishing policy guidance](https://learn.microsoft.com/en-us/troubleshoot/exchange/outlook-on-the-web-issues/publish-calendar-option-missing-from-calendar-settings)).
The tenant administrator can disable or revoke this capability.

An ICS URL is a bearer secret. Glasswork would need protected local storage,
redacted diagnostics, explicit refresh/staleness behavior, and a user-facing
disconnect action. ICS may expose less event detail and freshness than Graph,
but Planner needs busy intervals rather than rich calendar editing.

**Verdict:** Viable registration-free provider when tenant policy permits
publishing.

### User-operated local bridge

MCP defines stdio as a standard subprocess transport and does not apply the
HTTP OAuth flow to stdio servers
([MCP transports](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports),
[MCP authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)).
This matches Glasswork's existing external-process precedent in
[ADR 0007](../adr/0007-mcp-server.md).

A separately installed bridge could own its Microsoft identity and consent,
then expose only normalized today-calendar data over stdio or another local
protocol. This keeps Microsoft identity code and credentials outside Glasswork,
but it does not make registration disappear. A bridge registered in the same
corporate tenant encounters the same Service Tree requirement. A third-party
multitenant bridge also remains subject to corporate external-app consent and
Conditional Access.

**Verdict:** Conditionally viable as an advanced, opt-in provider. Treat it as
an independently trusted component, not a bundled workaround.

## Provider comparison

| Mechanism | Avoids Glasswork registration | Native product fit | Decision |
| --- | --- | --- | --- |
| Work IQ MCP | No | Agent/IDE tooling, broad consent | Reject |
| Enterprise MCP Server | No | No calendar tools | Reject |
| Copilot/agent connectors | Not applicable | Wrong direction | Reject |
| WAM | No | Yes, after registration | Keep for future Graph |
| Outlook COM | Yes | Classic Outlook only | Legacy fallback |
| EWS | No | Retires April 2027 | Reject |
| Office.js | No | Outlook-hosted only | Reject |
| OST/cache parsing | Not applicable | Unsupported | Reject |
| Published ICS | Yes | Plain read-only feed | Viable |
| User-operated local bridge | Partially | External opt-in process | Conditionally viable |

## Recommendation

Keep Glasswork personal and do not attach it to an unrelated corporate Service
Tree service. Make Calendar Context provider-neutral:

- **ICS** is the first registration-free provider to validate against the
  target tenant.
- **None/unavailable** remains a first-class provider state so Planner triage
  still works with capacity marked Unknown.
- **Graph/MSAL/WAM** remains a future provider if a legitimate registration
  becomes available.
- **Local bridge** is an advanced extension point, not a bundled dependency.

This preserves the already-decided Planner behavior and normalization contract
without making its product viability depend on one identity mechanism.

