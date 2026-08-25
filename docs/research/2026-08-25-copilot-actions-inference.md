# Copilot inference from GitHub Actions

## Question

Can Glasswork use GitHub Copilot for release-note rewriting in GitHub Actions
with the built-in `GITHUB_TOKEN` and `copilot-requests: write`, without relying
on the retired GitHub Models service or an external provider?

## Finding

Yes. GitHub officially documents direct Copilot CLI use in Actions with the
built-in `GITHUB_TOKEN`. The workflow must grant `copilot-requests: write`; for
an organization repository, the organization must allow Copilot CLI requests
billed to the organization. GitHub recommends Agentic Workflows for agentic
automation but explicitly supports direct CLI invocation for bounded tasks such
as one-shot note rewriting.

The original `actions/ai-inference@v1` reference was not this Copilot path. The
immutable v1 commit uses `https://models.github.ai/inference`, documents
`models: read`, and describes itself as GitHub Models integration. GitHub Models
was retired on July 30, 2026.

The current `actions/ai-inference` v3 tag resolves to commit
`2c43c91ae16266ca159d311430343c67a5ffa222`. At that commit the action is
Copilot-only, invokes an already-installed Copilot CLI with
`-p`, `-s`, and `--no-ask-user`, passes no tools unless explicitly configured,
and exposes the response through both `response` and `response-file`. Its source
reads `GITHUB_TOKEN` before the optional token input. The action does not install
the CLI, so the workflow must install it first.

`gpt-5.6-luna` is a supported Copilot CLI model. The GitHub model support table
lists GPT-5.6 Luna as available in the CLI, and GitHub's Agentic Workflows
firewall recognizes the exact `gpt-5.6-luna` slug.

## Glasswork decision

Glasswork will:

- install the current pinned stable CLI, `@github/copilot@1.0.80`;
- pin `actions/ai-inference` to the immutable v3 commit above;
- authenticate with the built-in `GITHUB_TOKEN` and
  `copilot-requests: write`;
- explicitly select the Copilot provider and grant no Copilot tools;
- keep CLI installation and inference non-blocking;
- validate the response's exact JSON shape, identifiers, categories, and prose;
  and
- always use deterministic categorized notes if any Copilot step or validation
  fails.

Copilot remains unable to affect release eligibility, stream classification,
version selection, PR contents outside note prose, or publication.

## Primary sources

- GitHub Docs, [Using Copilot CLI in GitHub Actions with
  GITHUB_TOKEN](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli-in-actions)
- GitHub Changelog, [GitHub Models is now
  retired](https://github.blog/changelog/2026-07-30-github-models-is-now-retired/)
- `actions/ai-inference` [v1 immutable
  action definition](https://github.com/actions/ai-inference/blob/b81b2afb8390ee6839b494a404766bef6493c7d9/action.yml)
  and [v1
  README](https://github.com/actions/ai-inference/blob/b81b2afb8390ee6839b494a404766bef6493c7d9/README.md)
- `actions/ai-inference` [v3 immutable
  action definition](https://github.com/actions/ai-inference/blob/2c43c91ae16266ca159d311430343c67a5ffa222/action.yml),
  [Copilot invocation
  source](https://github.com/actions/ai-inference/blob/2c43c91ae16266ca159d311430343c67a5ffa222/src/copilot.ts),
  and [output/token
  source](https://github.com/actions/ai-inference/blob/2c43c91ae16266ca159d311430343c67a5ffa222/src/main.ts)
- GitHub Docs source, [supported model clients
  table](https://github.com/github/docs/blob/main/data/tables/copilot/model-supported-clients.yml)
- GitHub Agentic Workflows firewall, [Copilot model
  validation](https://github.com/github/gh-aw-firewall/blob/main/src/copilot-model.ts)
