# Setting up code signing with SignPath Foundation

Everything in the repository is ready: the release workflow signs automatically as soon as the
GitHub settings in step 4 exist. The steps below need your identity and accounts, so only you can
do them. Exact screen names in SignPath may differ slightly from this guide.

## 1. Turn on two-factor authentication (required)

SignPath requires MFA for everyone on the team, on both GitHub and SignPath.

- GitHub: **Settings → Password and authentication → Two-factor authentication**.

## 2. Apply to SignPath Foundation

Apply at **https://signpath.org/** (look for "Apply" / "Get started" for open-source projects).

Their requirements and how this project meets them:

| Requirement | Where it's met |
|---|---|
| OSI-approved licence, no proprietary components | MIT [`LICENSE`](../LICENSE); no third-party packages ship in the app |
| Actively maintained, already released | [Releases](https://github.com/ZuluBax/Windows-Repair-Assistant/releases) (v1.0.0-beta.1) |
| Functionality described on the download page | [`README.md`](../README.md) and each release's notes |
| "Code signing policy" on the home page, attribution, team roles, privacy statement | [`CODE_SIGNING_POLICY.md`](../CODE_SIGNING_POLICY.md), linked from the README |
| Built from source automatically on GitHub-hosted runners | [`.github/workflows/release.yml`](../.github/workflows/release.yml) |
| Product name/version metadata enforced | [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml) + "Verify exe metadata" step |
| Every release manually approved | SignPath signing policy approver (you) |
| No system changes without warning | Start screen explains the repair; restart only after a confirmation dialog |

Review can take a while. Wait for their approval before continuing.

## 3. Configure the SignPath project (after approval)

SignPath Foundation may create some of these for you. If they give you names/slugs, use theirs.

1. **Install the SignPath GitHub App** and give it access to `ZuluBax/Windows-Repair-Assistant`.
2. **Trusted build system:** add the predefined **GitHub.com** trusted build system to your
   organisation and link it to the project.
3. **Project:** e.g. slug `windows-repair-assistant`, linked to the GitHub repository.
4. **Artifact configuration:** paste the contents of
   [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml) and make it the
   project's default.
5. **Signing policy:** e.g. slug `release-signing`, using the SignPath Foundation certificate.
   - **Approvers:** you. Every release waits for your approval.
   - **Submitters:** the user whose API token GitHub will use (next step).
6. **API token:** create one for a user with *submitter* permission on that signing policy
   (SignPath recommends a dedicated CI user). Copy it; it is shown once.
7. Note your **Organization ID** (shown in SignPath's organisation settings).

## 4. Add the settings to GitHub

Repository → **Settings → Secrets and variables → Actions**:

| Type | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | the API token from step 3.6 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | your SignPath organisation ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | e.g. `windows-repair-assistant` |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | e.g. `release-signing` |

Signing switches on as soon as `SIGNPATH_ORGANIZATION_ID` is set.

## 5. Test with a pre-release

```cmd
git tag v1.0.0-beta.2
git push origin v1.0.0-beta.2
```

1. The workflow builds the exe and submits it to SignPath.
2. You get an email from SignPath. **Approve the request within one hour**, or the workflow times out.
3. The workflow checks the signature and publishes the signed exe.

Check: right-click the downloaded exe → **Properties → Digital Signatures** should show
SignPath Foundation, and the UAC prompt should no longer say "Unknown publisher".

## Troubleshooting

- **"Stable release ... must be code-signed"**: you tagged `vX.Y.Z` before step 4. Use a pre-release
  tag, or finish the setup.
- **SignPath rejects the artifact (metadata mismatch)**: the exe's ProductName/ProductVersion must
  match `.signpath/artifact-configuration.xml`. The workflow's "Verify exe metadata" step prints both.
- **Workflow times out waiting**: the request wasn't approved within an hour. Delete the release tag
  (`git push --delete origin <tag>` and `git tag -d <tag>`), then push it again.
- **New signatures still get SmartScreen warnings at first**: reputation builds up with downloads
  of signed releases; it improves over days to weeks.
