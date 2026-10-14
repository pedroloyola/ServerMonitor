# Release security setup — the human steps (G1)

The release workflows enforce what code can enforce: tag/version coherence, the version floor, the
provenance of the run that is submitted (G4), the package gates, the publishing hold (G6), pinned
actions (G8), and a GitHub Release only on an explicit dispatch (G2). See
[`release-automation.md`](../release-automation.md).

What code cannot do is protect the code itself. Every one of those checks lives on `main`, and the
Store environments trust `main`. Whoever can push to `main` can rewrite the checks. The settings
below close that gap. **They are repository settings, changed by the owner, never by an agent or a
workflow.** Every step has a read-only verification command.

Labels: **VERIFIED** = read from the live repository with `gh api` (read-only), 2026-10-14 ·
**DOC** = GitHub or Microsoft documentation · **HYPOTHESIS** = not verified; says how to check.

## 0. Current state (VERIFIED 2026-10-14)

| Item | State | Command |
| --- | --- | --- |
| Branch protection on `main` | none (`404 Branch not protected`) | `gh api repos/pedroloyola/ServerMonitor/branches/main/protection` |
| Rulesets | none (`0`) | `gh api repos/pedroloyola/ServerMonitor/rulesets -q length` |
| `microsoft-store` environment | required reviewer `pedroloyola`, `prevent_self_review: false`; deployments only from `main` | `gh api repos/pedroloyola/ServerMonitor/environments/microsoft-store` |
| `microsoft-store-status` environment | **no reviewer**; deployments only from `main` | `gh api repos/pedroloyola/ServerMonitor/environments/microsoft-store-status` |
| Actions policy | `allowed_actions: all`, `sha_pinning_required: false` | `gh api repos/pedroloyola/ServerMonitor/actions/permissions` |
| Default workflow token | `read`; cannot approve pull requests | `gh api repos/pedroloyola/ServerMonitor/actions/permissions/workflow` |
| CI check names (GitHub Actions app id `15368`) | `Debug build + tests`, `Release build + vulnerability scan`, `Packaged build + distribution privacy` | `gh api repos/pedroloyola/ServerMonitor/commits/main/check-runs -q '.check_runs[] \| "\(.name) \(.app.id)"'` |
| CODEOWNERS | added by the hardening change (`.github/CODEOWNERS`); no effect until step A requires it | `gh api repos/pedroloyola/ServerMonitor/codeowners/errors` (after merge) |

## A. Protect `main` with a ruleset

### A.1 Decide the review model first (this is G7)

GitHub never lets the author of a pull request approve it (DOC). With one human maintainer:

* **Variant 1, solo (recommended now).** Pull request required, **0** approvals, code-owner review
  **off**, required checks on, no force push, no deletion. Direct pushes to `main` stop, including
  from tools and agents that use the owner's credential, and nothing red can merge. The owner can
  still merge their own pull request. CODEOWNERS only routes notifications.
* **Variant 2, two identities.** A second GitHub account (or a GitHub App) does the merging or the
  approving, and code-owner review is **on** with 1 approval. Only this variant turns CODEOWNERS into
  a control. It needs a second identity that agents cannot use.

**Automation shares the owner's credential (G7).** Local tooling that acts through the owner's `gh`
login is, to GitHub, the owner. No rule can tell them apart. Variant 1 still forces such changes through
a pull request with green checks, and the project's working rules reserve merging for the owner. Making
"automation cannot merge" a technical control needs a separate, restricted identity for that automation
(owner's decision).

### A.2 The ruleset (variant 1)

Save as `main-ruleset.json` outside the repository:

```json
{
  "name": "main",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "bypass_actors": [],
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": true,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": true,
        "required_status_checks": [
          { "context": "Debug build + tests", "integration_id": 15368 },
          { "context": "Release build + vulnerability scan", "integration_id": 15368 },
          { "context": "Packaged build + distribution privacy", "integration_id": 15368 }
        ]
      }
    }
  ]
}
```

For variant 2, set `"required_approving_review_count": 1` and `"require_code_owner_review": true`.

Apply it, as the owner (Settings → Rules → Rulesets → New branch ruleset, or):

```powershell
gh api -X POST repos/pedroloyola/ServerMonitor/rulesets --input main-ruleset.json
```

* `bypass_actors` is empty, so the rules also bind repository admins (DOC: rulesets apply to everyone
  not on the bypass list). Adding the owner to the bypass list gives back the direct push this step
  removes. Don't add the owner.
* `strict_required_status_checks_policy` means a pull request must be up to date with `main` before it
  merges, so the checks ran on what actually lands.
* The check names are job names from `ci.yml`. If a job is renamed, update the ruleset in the same
  change, or every pull request stays blocked waiting for a check that no longer exists.

### A.3 Protect release tags (recommended)

`store-submit` compares the tag's commit **at submission time** with the commit the run built, so
a moved tag fails closed. A tag ruleset prevents the move in the first place:

```json
{
  "name": "release tags",
  "target": "tag",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/tags/v*"], "exclude": [] } },
  "bypass_actors": [],
  "rules": [ { "type": "update" }, { "type": "deletion" } ]
}
```

Creating `vX.Y.Z` stays allowed. Re-pointing or deleting one is blocked, including for the owner. A
broken tag is then fixed by a new, higher version, which is what the Store requires anyway.

### A.4 Verify (read-only)

```powershell
gh api repos/pedroloyola/ServerMonitor/rulesets -q '.[] | "\(.id) \(.name) \(.target) \(.enforcement)"'
gh api repos/pedroloyola/ServerMonitor/rules/branches/main -q '.[].type'
#   expected: deletion, non_fast_forward, pull_request, required_status_checks
gh api repos/pedroloyola/ServerMonitor/rules/branches/main -q '.[] | select(.type=="required_status_checks") | .parameters.required_status_checks[].context'
gh api repos/pedroloyola/ServerMonitor/codeowners/errors -q '.errors | length'   # expected 0
```

Behavioural check, harmless: `git push origin HEAD:main` from a throwaway local commit must be
**rejected**. Do this only with a commit you were going to discard anyway, and never with `--force`.

## B. `microsoft-store-status` — reviewer or least-privilege credential

Both Store environments carry the same Entra application, which has the **Manager** role in
Partner Center (VERIFIED in `release-automation.md`). The status workflow only runs read commands,
but its credential can write. Pick one:

* **B1 (recommended, verifiable today):** add a required reviewer to `microsoft-store-status`.
  Settings → Environments → `microsoft-store-status` → Required reviewers → `pedroloyola`. Every
  status run then waits for an approval, which is the cost. With one human, "prevent self-review"
  must stay off, or nobody can approve (G7).
* **B2 (least privilege, not verified):** a second Entra application, added to Partner Center with
  a role that cannot submit, used only by `microsoft-store-status`.
  * Partner Center lets you assign Entra applications the roles Manager, Developer, Business
    contributor, Finance contributor and Marketer (DOC, Partner Center "Add Microsoft Entra
    applications").
  * Microsoft's submission-API documentation asks for **Manager** (DOC).
  * Whether a lesser role can read submission status through the API is **HYPOTHESIS**. Check it with
    the second application in a `store-status` run, before relying on it.
  * Until that is proven, B1 is the control.

Verify (read-only):

```powershell
gh api repos/pedroloyola/ServerMonitor/environments/microsoft-store-status -q '[.protection_rules[].type]'
#   B1 expected: ["required_reviewers", "branch_policy"]
gh api repos/pedroloyola/ServerMonitor/environments/microsoft-store-status/secrets -q '.secrets[].name'   # names only
```

Keep `microsoft-store` as it is: required reviewer, deployments only from `main`.

## C. Actions policy (optional, after one more change)

The three release workflows pin every action to a full SHA. `ci.yml` still uses major tags, which
lets Dependabot propose plain version bumps there. The repository setting "Require actions to be
pinned to a full-length commit SHA" (`sha_pinning_required`) would turn the release-workflow rule
into a repository-wide one. Before turning it on, pin `ci.yml` too. Otherwise CI fails.
HYPOTHESIS: the setting applies to every workflow in the repository; check it on a pull request
after enabling. Verify with
`gh api repos/pedroloyola/ServerMonitor/actions/permissions -q .sha_pinning_required`.

## D. When G1 is closed

All of the following, verified with the commands above, recorded with the date:

1. The `main` ruleset is active with deletion, non-fast-forward, pull request and the three required
   checks. `bypass_actors` is empty.
2. `microsoft-store-status` has a required reviewer (B1), or a separate credential whose read-only
   role was proven (B2).
3. `microsoft-store` keeps its reviewer and the `main`-only deployment policy.
4. `codeowners/errors` returns no errors.
5. The release-tag ruleset is active (recommended, not required for closing G1).
6. G7 and G9 below are recorded as accepted risks by the owner, with a date.

## E. Accepted risks (owner's decision, recorded here)

| ID | Risk | Why it is accepted | Revisit when |
| --- | --- | --- | --- |
| **G7** | One human identity. The reviewer of `microsoft-store` is also the one who dispatches (`prevent_self_review: false`). Agents act with the owner's credential, so rules cannot separate "owner" from "agent". Code-owner review can't be enforced without a second identity. | Solo maintainer. The controls that still hold: pull request + green checks on `main`, no force push, the provenance job, the Store reviewer prompt, `SUBMIT`, and the publishing hold (a person presses "Publish now" in Partner Center). | A second maintainer, or a separate restricted identity for agents. |
| **G9** | The Store credential is a long-lived Entra **client secret** (expires **2027-03-04**, see *Rotating the client secret*), not a federated/OIDC credential. | `msstore reconfigure` is documented with a client secret. Whether the Store CLI and the Partner Center flow accept a GitHub OIDC federated credential is **HYPOTHESIS** and needs an approved Entra identity change (R1). The secret lives only in the two environments, is passed to one step through the environment, and is never printed. | Before 2027-03-04 (rotation), or when a federated credential is approved. |

| Accepted by | Date | Note |
| --- | --- | --- |
| | | |

## Sources

* GitHub — [About rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets) · [Available rules for rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets) · [REST: repository rulesets](https://docs.github.com/en/rest/repos/rules)
* GitHub — [About code owners](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners) · [About pull request reviews (authors cannot approve their own)](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/about-pull-request-reviews)
* GitHub — [Managing environments for deployment](https://docs.github.com/en/actions/managing-workflow-runs-and-deployments/managing-deployments/managing-environments-for-deployment) · [REST: GitHub Actions permissions](https://docs.github.com/en/rest/actions/permissions)
* Microsoft — [Add Microsoft Entra applications to your Partner Center account](https://learn.microsoft.com/en-us/partner-center/account-settings/add-azure-ad-applications) · [Create and manage submissions using Microsoft Store services](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services)
