# Uploading the Workshop item from GitHub Actions

`.github/workflows/workshop-upload.yml` uploads the item a `workshop.yml` run built to the Steam
Workshop. It signs in with a steamcmd session you sign in to once and store as a secret, so an
upload needs no password, code or phone prompt. When the session runs out, the run fails at once
and says so; sign in again (below).

## What a run does

Run it by hand from `master` or `u60` with the number of a successful `workshop.yml` run. It
starts as soon as it is dispatched:

```sh
gh workflow run workshop-upload.yml -r master -f run=RUN_ID                 # dry run: no Steam sign-in
gh workflow run workshop-upload.yml -r master -f run=RUN_ID -f mode=upload
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
5. **Upload only:** in the `steam-workshop` environment (`scripts/workshop-upload.sh upload`):
   - puts `STEAM_CONFIG_VDF` in place as steamcmd's `config.vdf` (mode 600, never printed);
   - signs in with `login <name>`, no password, and no prompt (`@NoPromptForPassword 1`),
     within 3 minutes;
   - if that sign-in fails (an expired or revoked session, "Invalid Password", "Login Failure",
     "License expired", a password or Steam Guard prompt), stops with an error that links here.
     It never falls back to another way of signing in, and never waits;
   - turns steamcmd's "Login token expires at …" line, when it prints one, into a warning;
   - runs `workshop_build_item`.

   steamcmd's home, with the session, is deleted at the end of the job, even when the job
   fails. The renewed session steamcmd may write is never saved back.

The item is the `STEAM_WORKSHOP_ITEM` repository variable: 3837112063 when unset, or `new` to
create one.

## One-time setup

Do these yourself. Never paste a password, code or session into a chat; each one goes straight
into steamcmd or `gh`.

### 1. The Steam account

The account must own Oxygen Not Included, because Steam refuses updates from an account
without it. It must also own the item or be able to update it. `qigit` owns both.

### 2. Create the GitHub Environment

The `steam-workshop` environment keeps the secrets from every other job, and takes runs from
master and u60 only:

```sh
gh api -X PUT repos/tigin-backwards/oni-moono/environments/steam-workshop --input - <<'EOF'
{"deployment_branch_policy": {"protected_branches": false, "custom_branch_policies": true}}
EOF
for b in master u60; do
  gh api -X POST repos/tigin-backwards/oni-moono/environments/steam-workshop/deployment-branch-policies -f name=$b -f type=branch
done
gh secret set STEAM_USERNAME --env steam-workshop -R tigin-backwards/oni-moono   # the account name, not the email
```

You can also do this in Settings → Environments.

### 3. Priming the cached session

This follows Valve's
[Using SteamPipe in a CI/CD environment](https://partner.steamgames.com/doc/sdk/uploading):
sign in once with the password and Steam Guard, then keep `config/config.vdf`. Later runs use
only `+login <username>`. [GameCI's steam-deploy](https://github.com/game-ci/steam-deploy#configvdf)
uses the same `configVdf` approach.

Sign in once with steamcmd in a throwaway home, so your usual cached login is neither used nor
changed. With Steam's snap on Linux, and steamcmd in `out/local-tools/steamcmd`:

```sh
prime="$PWD/out/steam-prime"; mkdir -p "$prime"
snap run --shell steam -c "HOME='$prime' '$PWD/out/local-tools/steamcmd/steamcmd.sh' +login qigit +quit"
```

Type the password at steamcmd's prompt and approve the sign-in on your phone. Then store the
session and delete the throwaway home:

```sh
base64 -w0 "$prime/Steam/config/config.vdf" | gh secret set STEAM_CONFIG_VDF --env steam-workshop -R tigin-backwards/oni-moono
rm -rf "$prime"
```

**Prime it again** when a run fails with "The cached Steam session in STEAM_CONFIG_VDF has
expired or was revoked", or after its "expires" warning. Use the same commands; the new secret
replaces the old one.

How long a session lasts:
- Steam's refresh tokens last
  [about 200 days](https://dev.doctormckay.com/topic/4607-access-tokens-and-refreshtoken-lifetime).
- Valve can revoke one sooner, and so can changing the password or "Deauthorize all other
  devices".
- The token in `config.vdf` is encrypted, so the workflow can't read its expiry ahead of time. It
  only warns when steamcmd itself prints one at sign-in.

## Other ways to sign in

If `STEAM_CONFIG_VDF` isn't set, the job signs in with `STEAM_PASSWORD`:

- **On your phone:** with no `STEAM_SHARED_SECRET`, Steam asks the Steam Mobile app to confirm
  the sign-in. The run shows "Approve the sign-in on your phone" with a deadline. steamcmd gives
  up on one confirmation after about a minute, so the job asks again, up to 6 times in 5 minutes.
  Current steamcmd waits for this without a terminal:
  - [buildalon/upload-steam](https://github.com/buildalon/upload-steam) is a GitHub Action built on it;
  - logs of a sign-in being [confirmed](https://discourse.cubecoders.com/t/startup-issue-with-left-4-dead-2-attempting-to-start-a-server-for-the-first-time/29857)
    and of one [timing out](https://discourse.cubecoders.com/t/how-to-provide-steam-2fa-information-from-steams-phone-app-solved/42461).
- **A code from the shared_secret:** with `STEAM_SHARED_SECRET` (an authenticator's secret from
  [steamguard-cli](https://github.com/dyc3/steamguard-cli)), the job makes the current Steam
  Guard code itself (masked) and passes it with `set_steam_guard_code`, as GameCI does.
  - Anyone holding that secret and the password can sign in, so use it only with a separate
    upload account.
  - That account needs the game of its own, and an item of its own: Valve documents no way for
    a contributor to update content, and
    [the reports found](https://kb.tabletopsimulator.com/custom-content/steam-workshop/) say
    contributors can't.
  - Set `STEAM_WORKSHOP_ITEM` to `new` for one run to create the item.

The password must be letters and digits, plus `. _ ~ @ # % ^ * + = : , ! ? -`. Spaces, quotes,
`;`, `\` and `//` are refused, because steamcmd's login script would split or cut the line there.

## Steam's rules

- [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) §1.C:
  don't "reveal, share or otherwise allow others to use your password or Account". Only your own
  workflow uses the session.
- §4.C forbids "scripts, bots, macros, or other non-human-controlled systems … to interact with
  Content and Services on Steam". Its examples are account creation, faked gameplay statistics,
  unearned rewards and automated reports. Uploading with steamcmd from CI is what Valve's
  [SteamPipe CI/CD section](https://partner.steamgames.com/doc/sdk/uploading) describes, and you
  start each upload. This reading isn't legal advice.
- §6 (User Generated Content) applies to the item as it does to any upload.

## Rotating and revoking

- **Revoke the session:** "Deauthorize all other devices" at
  <https://store.steampowered.com/twofactor/manage> ends every cached login, this one included.
  Changing the password also does. Then `gh secret delete STEAM_CONFIG_VDF --env steam-workshop`
  or prime a new one.
- **Stop CI uploads:** delete the secret, or the environment.
- **If the secret may have leaked:** it signs in as `qigit` without a password or phone until it
  is revoked. Revoke it as above at once, check the account's devices at
  <https://store.steampowered.com/account/authorizeddevices>, and check the item's change notes.
  Anyone who can dispatch workflows in the repository can start an upload, so keep GitHub
  access as safe as the secret.
- **Bootstrap digest:** if Valve replaces `steamcmd_linux.tar.gz` (unchanged since 2018), the
  install step fails. Check the new archive, then update `STEAMCMD_SHA256` in
  `scripts/workshop-upload.sh`.
