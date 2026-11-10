# Uploading the Workshop item from GitHub Actions

`.github/workflows/workshop-upload.yml` uploads the item a `workshop.yml` run built to the Steam
Workshop. Each upload needs two approvals from you: the run in GitHub, then the Steam sign-in on
your phone. The workflow stores the account's name and password, never a Steam Guard secret.

## What a run does

Run it by hand from `master` or `u60` with the number of a successful `workshop.yml` run:

```sh
gh workflow run workshop-upload.yml -r master -f run=RUN_ID                 # dry run: no Steam sign-in
gh workflow run workshop-upload.yml -r master -f run=RUN_ID -f mode=upload  # waits for your approvals
```

1. Checks that the run is a successful `workshop.yml` run on master or u60 and downloads its
   `workshop-item` artifact.
2. Checks the item with `ci/workshop/verify.sh` from the checkout: each version's release
   manifest by its provenance from `release.yml`, the item's `SHA256SUMS` by its provenance from
   `workshop.yml` on master or u60, every file against it, and nothing extra. It also checks
   that the attestation was signed by that same run.
3. Writes the VDF (`scripts/workshop-upload.py`):
   - **description:** master's `ci/workshop/description.bbcode`, as written;
   - **change note:** the notes of the releases whose tags are in the item. A tag without a
     published release is refused, so an item built from a dry run is never uploaded;
   - **preview:** master's `ci/workshop/preview.gif`, only with `-f preview=true`;
   - **visibility:** left out by default, so Steam keeps the item's current visibility.
     `-f visibility=private|friends|unlisted|public` sets it.
4. Installs steamcmd from Valve's bootstrap archive, checked against a pinned SHA-256
   (`scripts/workshop-upload.sh`). steamcmd then updates itself from Valve, which can't be pinned.
   The dry run stops here.
5. **Upload only:** the job waits in the `steam-workshop` environment until you approve it in
   GitHub, then signs in to Steam:
   - Steam asks your phone to confirm the sign-in. The run shows "Approve the sign-in on your
     phone" with a deadline 5 minutes ahead.
   - steamcmd gives up on one confirmation after about a minute, so the job signs in again, up
     to 6 times, until those 5 minutes are over.
   - Then it runs `workshop_build_item`.

   steamcmd's home, which holds its cached login, is deleted at the end of the job, even when the
   job fails. The next run asks your phone again.

The item is the `STEAM_WORKSHOP_ITEM` repository variable: 3837112063 when unset, or `new` to
create one.

## Phone confirmation in steamcmd

Current steamcmd (version 1792617903, the one CI installs) supports it. When an account has the
Steam Mobile authenticator and gets no code, steamcmd prints:

```
This account is protected by a Steam Guard mobile authenticator.
Please confirm the login in the Steam Mobile app on your phone.
Waiting for confirmation...OK
```

It then carries on without a code. If no one confirms within about a minute, it prints
`Timed out waiting for confirmation` and `ERROR (Timeout)`. It needs no terminal:

- [buildalon/upload-steam](https://github.com/buildalon/upload-steam) is a GitHub Action built
  on this flow: "You will be prompted to accept the login from the Steam Mobile app on your phone".
- Logs of both outcomes: [confirmed](https://discourse.cubecoders.com/t/startup-issue-with-left-4-dead-2-attempting-to-start-a-server-for-the-first-time/29857)
  (2025) and [timed out](https://discourse.cubecoders.com/t/how-to-provide-steam-2fa-information-from-steams-phone-app-solved/42461)
  (2026).
- [UnityBuildUploader's login tests](https://github.com/JamesVeug/UnityBuildUploader/blob/HEAD/UnitTest/Core/SteamLoginOutputTests.cs)
  treat this output as a successful login.

## One-time setup

Do these yourself. Never paste a password, code or secret into a chat; each one goes straight
into Steam or `gh`.

### 1. The Steam account

The account must own Oxygen Not Included, because Steam refuses updates from an account
without it. It must also own the item or be able to update it. `qigit` owns both, and its
Steam Mobile app must be its Steam Guard.

The workflow refuses spaces, quotes, `;`, `\` and `//` in the password, because steamcmd's login
script would split or cut the line there. Besides letters and digits it allows
`. _ ~ @ # % ^ * + = : , ! ? -`. With any other character the job fails before signing in.

### 2. Create the GitHub Environment

The `steam-workshop` environment requires your approval for each run, accepts runs from
master and u60 only, and doesn't let admins skip the approval:

```sh
gh api -X PUT repos/tigin-backwards/oni-moono/environments/steam-workshop --input - <<'EOF'
{"reviewers": [{"type": "User", "id": 275280754}], "prevent_self_review": false, "can_admins_bypass": false,
 "deployment_branch_policy": {"protected_branches": false, "custom_branch_policies": true}}
EOF
for b in master u60; do
  gh api -X POST repos/tigin-backwards/oni-moono/environments/steam-workshop/deployment-branch-policies -f name=$b -f type=branch
done
```

`275280754` is the tigin-backwards account. `prevent_self_review` is false so that you can approve
the runs you start. You can also do this in Settings → Environments.

### 3. Set the environment's secrets

Each command asks for the value; type or paste it there:

```sh
gh secret set STEAM_USERNAME --env steam-workshop -R tigin-backwards/oni-moono   # the account name, not the email
gh secret set STEAM_PASSWORD --env steam-workshop -R tigin-backwards/oni-moono
```

### 4. Upload

Start a dry run, then an upload (see the commands above). Approve the run in GitHub, keep your
phone at hand, and approve the sign-in in the Steam Mobile app when the run asks. Steam shows
where the sign-in comes from: a GitHub runner, usually a US data centre. Reject any sign-in you
didn't just start.

## Unattended alternative: a code from the shared_secret

If `STEAM_SHARED_SECRET` is set in the environment, the job doesn't ask your phone. It makes
the current Steam Guard code itself (masked in the log) and passes it with
`set_steam_guard_code`, as [GameCI's steam-deploy](https://github.com/game-ci/steam-deploy#totp)
does. The code uses steamguard-cli's algorithm and is tested against steamguard-cli's test vector.

Anyone holding that secret and the password can sign in to the account, so use this only with a
separate upload account:

1. The account needs Oxygen Not Included of its own.
2. Its authenticator comes from [steamguard-cli](https://github.com/dyc3/steamguard-cli) (`steamguard setup`).
3. Set the secret straight from the maFile, so it never shows:

   ```sh
   jq -r .shared_secret ~/.config/steamguard-cli/maFiles/ACCOUNT.maFile \
     | gh secret set STEAM_SHARED_SECRET --env steam-workshop -R tigin-backwards/oni-moono
   ```

Being a contributor on an item probably doesn't let that account update the item's content.
Valve documents no such right, and the reports found say contributors can't:

- [Tabletop Simulator's guide](https://kb.tabletopsimulator.com/custom-content/steam-workshop/);
- [Steam's suggestion thread](https://steamcommunity.com/discussions/forum/10/618456760263925033/).

So that account would own its own item: set `STEAM_WORKSHOP_ITEM` to `new` for one run.

A cached steamcmd `config.vdf` (GameCI's `configVdf`) isn't supported. It is a full session that
works without the password, and Steam drops it after
[about 200 days](https://dev.doctormckay.com/topic/4607-access-tokens-and-refreshtoken-lifetime)
or sooner.

## Steam's rules

- [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) §1.C:
  don't "reveal, share or otherwise allow others to use your password or Account". Only your own
  workflow uses it, and you confirm each sign-in.
- §4.C forbids "scripts, bots, macros, or other non-human-controlled systems … to interact with
  Content and Services on Steam". Its examples are account creation, faked gameplay statistics,
  unearned rewards and automated reports. Uploading with Valve's own steamcmd is what its
  [Workshop docs](https://partner.steamgames.com/doc/features/workshop/implementation) describe
  (there, for testing). Each upload here is started and approved by you. This reading isn't
  legal advice.
- §6 (User Generated Content) applies to the item as it does to any upload.

## Rotating and revoking

- **Password:** change it on Steam, then `gh secret set STEAM_PASSWORD --env steam-workshop`.
- **Stop CI uploads:** `gh secret delete STEAM_PASSWORD --env steam-workshop` (or delete the
  environment).
- **If the password may have leaked:** change it. A sign-in still needs your phone, so reject any
  confirmation you didn't start. Check the account's sessions at
  <https://store.steampowered.com/account/authorizeddevices> and remove any you don't know.
  Anyone with your GitHub token can approve runs through the API, but not the phone
  confirmation.
- **Bootstrap digest:** if Valve replaces `steamcmd_linux.tar.gz` (unchanged since 2018), the
  install step fails. Check the new archive, then update `STEAMCMD_SHA256` in
  `scripts/workshop-upload.sh`.
