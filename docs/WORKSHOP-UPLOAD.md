# Uploading the Workshop item from GitHub Actions

`.github/workflows/workshop-upload.yml` uploads the item a `workshop.yml` run built to the Steam
Workshop. It logs in as a separate **upload account**, never your main Steam account, and only
after you approve the run in GitHub. A compromised workflow can reach the upload account and the
item, nothing else of yours.

## What a run does

Run it by hand from `master` or `u60` with the number of a successful `workshop.yml` run:

```sh
gh workflow run workshop-upload.yml -r master -f run=RUN_ID                 # dry run: no Steam login
gh workflow run workshop-upload.yml -r master -f run=RUN_ID -f mode=upload  # waits for your approval
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
5. **Upload only:** in the `steam-workshop` environment, after your approval, makes a Steam
   Guard code from the account's `shared_secret` (masked in the log) and runs
   `workshop_build_item`. steamcmd's home, which holds its cached login, is deleted at the end
   of the job, even when it fails.

The item is the `STEAM_WORKSHOP_ITEM` repository variable (3837112063 when unset), or `new`.

## Can a contributor upload?

Probably not. Valve documents nothing that lets a contributor update an item's content; every
report found says contributors can't. Steam also refuses an update from an account that doesn't
own the game. Sources:

- [ISteamUGC `SubmitItemUpdateResult_t`](https://partner.steamgames.com/doc/api/ISteamUGC#SubmitItemUpdateResult_t):
  `k_EResultAccessDenied` means "The user doesn't own a license for the provided app ID".
- [Tabletop Simulator's Workshop guide](https://kb.tabletopsimulator.com/custom-content/steam-workshop/):
  "contributors cannot update your mod for you … an ability that Valve needs to add".
- [Steam's suggestion thread on contributor permissions](https://steamcommunity.com/discussions/forum/10/618456760263925033/)
  (still asked for in 2024), and an [Unreal forum thread](https://forums.unrealengine.com/t/is-it-possible-to-have-multiple-contributors-to-a-steam-workshop-item/56555)
  where a contributor's steamcmd upload failed and Family Sharing wasn't enough.

So the setup below makes the upload account the item's **owner**: it creates a new private item,
and your main account becomes a contributor on it so the item shows on your profile. Option B
keeps item 3837112063 instead, if you want to try the contributor route first.

## One-time setup

Do these yourself. Never paste a password, code or secret into a chat; each one goes straight
into Steam, steamguard-cli or `gh`.

### 1. Create the upload account

1. Sign up at <https://store.steampowered.com/join> with a separate email address.
2. Use a long random password made of **letters and digits only**. The workflow refuses spaces,
   quotes, `;`, `\` and `//`, because steamcmd's login script would split or cut the line there.
   It allows `. _ ~ @ # % ^ * + = : , ! ? -` besides.
3. **Give it Oxygen Not Included.** Buy it on the account or gift it from your main account;
   Family Sharing doesn't count. Steam refuses updates from an account without the game, and
   owning a game also lifts the new account's limits. No DLC is needed.
4. Signed in as the upload account, accept the
   [Workshop legal agreement](https://steamcommunity.com/sharedfiles/workshoplegalagreement).
   Until then Steam hides its items from everyone but itself.

### 2. Set up Steam Guard with steamguard-cli

1. Install [steamguard-cli](https://github.com/dyc3/steamguard-cli/releases) (v0.18.4: the
   `.deb`, or `steamguard-linux-x86_64`).
2. Run `steamguard setup` and sign in as the upload account. Follow its prompts; Steam confirms the change by email or text message.
3. **Write down the revocation code** it shows (`R…`) and back up `~/.config/steamguard-cli/maFiles/`.
   Without one of them, only Steam Support can remove the authenticator.
4. From now on, `steamguard code` gives the account's sign-in code, and
   `steamguard confirm` handles its confirmations.

If you chose an encryption passkey during setup, run `steamguard decrypt` before step 4 and
`steamguard encrypt` after it.

### 3. Create the GitHub Environment

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

### 4. Set the environment's secrets

Each command asks for the value; type or paste it there:

```sh
gh secret set STEAM_USERNAME --env steam-workshop -R tigin-backwards/oni-moono   # the account name, not the email
gh secret set STEAM_PASSWORD --env steam-workshop -R tigin-backwards/oni-moono
jq -r .shared_secret ~/.config/steamguard-cli/maFiles/ACCOUNT.maFile \
  | gh secret set STEAM_SHARED_SECRET --env steam-workshop -R tigin-backwards/oni-moono
```

The last command pipes the secret from the maFile straight into `gh`, so it never shows on the
screen. `ACCOUNT` is the upload account's name.

### 5. Make the item

**A. A new item owned by the upload account (recommended):**

1. `gh variable set STEAM_WORKSHOP_ITEM --body new -R tigin-backwards/oni-moono`
2. Start a dry run, then an upload, of the latest `workshop.yml` run (see the commands above),
   and approve it. The new item is created **private**, titled `Moono`, or the
   `STEAM_WORKSHOP_TITLE` variable if set. The run's summary prints its ID.
3. `gh variable set STEAM_WORKSHOP_ITEM --body NEW_ID -R tigin-backwards/oni-moono`
4. Make the upload account and your main account (`qigit`) Steam friends. Then, signed in as the
   upload account, open the item's page, choose **Add/Remove Contributors** and add `qigit`.
5. Item 3837112063 is then unused: keep it private or delete it from its page.

**B. Keep item 3837112063:** make the two accounts friends. Signed in as `qigit`, choose
**Add/Remove Contributors** on the item's page and add the upload account. Leave
`STEAM_WORKSHOP_ITEM` unset. If the first upload fails with an access error, switch to A.

## Authentication: why TOTP

- **TOTP from `shared_secret` (used here).** Each run makes the current code itself
  (steamguard-cli's algorithm, tested against its test vector), and passes it to steamcmd as
  [GameCI's steam-deploy](https://github.com/game-ci/steam-deploy#totp) does, with
  `set_steam_guard_code` before `login`. Nothing expires, and no login is cached between runs.
- **A cached `config.vdf`** (GameCI's `configVdf`) holds a logged-in session. It lasts until
  Steam drops the session: GameCI lists "License expired" and new Steam Guard prompts as
  things to fix by hand, and Steam's refresh tokens live
  [about 200 days](https://dev.doctormckay.com/topic/4607-access-tokens-and-refreshtoken-lifetime).
  The secret is also a full session that works without the password, and it needs manual
  renewal. Not used.

## Steam's rules

- [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) §1.C:
  don't "reveal, share or otherwise allow others to use your password or Account". The upload
  account is yours and only your own workflow uses it. Nothing in the agreement forbids a second
  account (only re-registering while one is delinquent, §3.B).
- §4.C forbids "scripts, bots, macros, or other non-human-controlled systems … to interact with
  Content and Services on Steam". Its examples are account creation, faked gameplay statistics,
  unearned rewards and automated reports. Uploading with Valve's own steamcmd is what its
  [Workshop docs](https://partner.steamgames.com/doc/features/workshop/implementation)
  describe (there, for testing). Each upload here is started and approved by you. This reading
  isn't legal advice.
- §6 (User Generated Content) applies to the item as it does to any upload: you grant Valve the
  usual licence and confirm you have the rights.

## Rotating and revoking

- **Password:** change it on Steam, then `gh secret set STEAM_PASSWORD --env steam-workshop`.
- **Authenticator:** `steamguard remove`, `steamguard setup` again, then set
  `STEAM_SHARED_SECRET` again as in step 4. If the maFile is lost, remove the authenticator
  with the revocation code at <https://store.steampowered.com/twofactor/manage>.
- **Stop CI uploads:** `gh secret delete STEAM_PASSWORD --env steam-workshop` (or delete the
  environment). For option B, also remove the upload account from the item's contributors.
- **If the account may be compromised:** change its password, choose
  **Deauthorize all other devices** at <https://store.steampowered.com/twofactor/manage>, rotate
  the authenticator, check the item's change notes, and reject any upload run you didn't start.
  Anyone with your GitHub token can also approve runs through the API, so keep that token as
  safe as the secrets.
- **Bootstrap digest:** if Valve replaces `steamcmd_linux.tar.gz` (unchanged since 2018), the
  install step fails. Check the new archive, then update `STEAMCMD_SHA256` in
  `scripts/workshop-upload.sh`.
