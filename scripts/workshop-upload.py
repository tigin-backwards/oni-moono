#!/usr/bin/env python3
"""The text side of uploading the Workshop item with steamcmd (.github/workflows/workshop-upload.yml,
docs/WORKSHOP-UPLOAD.md).

    workshop-upload.py notes ITEM NOTES-DIR OUT        the change note, from the releases' notes
    workshop-upload.py vdf --item ID|new --description FILE [--content DIR] [--preview FILE]
                           [--changenote FILE] [--visibility keep|...] [--title T] OUT
    workshop-upload.py login [--cached] [--check] VDF OUT
        steamcmd's login script, from the environment: --cached signs in with the session cached
        in steamcmd's config.vdf (no password); otherwise the password, with a Steam Guard code if
        STEAM_SHARED_SECRET is set, else the sign-in is confirmed on the phone. --check signs in
        and quits.

steamcmd reads the VDF raw: it does not unescape backslashes and has no escape for a double
quote, so every value is written exactly as given, and a value with a double quote is refused.
The login script holds the password; it is written readable by its owner alone, and the job
deletes it with steamcmd's home once steamcmd exits.
"""

import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import struct
import sys
import time

APP_ID = 457140
# ERemoteStoragePublishedFileVisibility. Leaving the key out keeps the item's visibility.
VISIBILITY = {"public": 0, "friends": 1, "private": 2, "unlisted": 3}
# isteamremotestorage.h: k_cchPublishedDocumentTitleMax, ...DescriptionMax, ...ChangeDescriptionMax.
TITLE_MAX, DESCRIPTION_MAX, CHANGENOTE_MAX = 128, 8000, 8000
PREVIEW_MAX = 1024 * 1024  # SubmitItemUpdateResult_t: the preview "must be less than 1 Megabyte"
PREVIEW_MAGIC = (b"GIF87a", b"GIF89a", b"\x89PNG\r\n\x1a\n", b"\xff\xd8\xff")
TAG = re.compile(r"v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}")
STEAM_CHARS = "23456789BCDFGHJKMNPQRTVWXY"
# steamcmd splits a script line at whitespace and ';', and drops what follows '//'.
LOGIN_NAME = re.compile(r"[A-Za-z0-9_]{3,64}")
PASSWORD = re.compile(r"[A-Za-z0-9._~@#%^*+=:,!?-]{8,128}")


def fail(message):
    sys.exit(f"workshop-upload: {message}")


def vdf_value(name, text):
    """text as steamcmd will read it between double quotes, or a failure saying why it can't be."""
    if '"' in text:
        fail(f"the {name} has a double quote, which steamcmd's VDF can't carry")
    if re.search(r"[\x00-\x08\x0b-\x1f\x7f]", text):
        fail(f"the {name} has a control character")
    return text


def utf8_len(text):
    return len(text.encode("utf-8"))


def vdf(item, description, content=None, preview=None, changenote=None, visibility="keep", title=None):
    """The workshop_build_item VDF. item is a published file ID, or "new" to create one (with a
    title, and private unless a visibility is given)."""
    if item == "new":
        if not title:
            fail("a new item needs a title")
        if "ITEM_ID" in description:
            fail("the description names ITEM_ID, unknown until the item exists: upload it again once it does")
        published = "0"
        if visibility == "keep":
            visibility = "private"
    else:
        if not re.fullmatch(r"[1-9][0-9]{0,19}", item):
            fail(f"the item must be its published file ID or new, not {item!r}")
        published = item
        description = description.replace("ITEM_ID", item)
    if visibility != "keep" and visibility not in VISIBILITY:
        fail(f"visibility must be keep or one of {', '.join(VISIBILITY)}, not {visibility!r}")
    if utf8_len(description) > DESCRIPTION_MAX:
        fail(f"the description is {utf8_len(description)} bytes; Steam takes {DESCRIPTION_MAX}")
    fields = [("appid", str(APP_ID)), ("publishedfileid", published)]
    for key, path in (("contentfolder", content), ("previewfile", preview)):
        if path is not None:
            if not os.path.isabs(path):
                fail(f"the {key} must be an absolute path, not {path!r}")
            fields.append((key, vdf_value(key, path)))
    if preview is not None:
        with open(preview, "rb") as f:
            head = f.read(8)
        if not head.startswith(PREVIEW_MAGIC):
            fail(f"{preview} is not a GIF, PNG or JPEG")
        if os.path.getsize(preview) >= PREVIEW_MAX:
            fail(f"{preview} is {os.path.getsize(preview)} bytes; Steam takes less than 1 MB")
    if content is not None and not os.path.isdir(content):
        fail(f"{content} is not a folder")
    if visibility != "keep":
        fields.append(("visibility", str(VISIBILITY[visibility])))
    if title is not None:
        if utf8_len(title) > TITLE_MAX or "\n" in title:
            fail("the title must be one line of at most 128 bytes")
        fields.append(("title", vdf_value("title", title)))
    fields.append(("description", vdf_value("description", description)))
    if changenote:
        if utf8_len(changenote) > CHANGENOTE_MAX:
            fail(f"the change note is {utf8_len(changenote)} bytes; Steam takes {CHANGENOTE_MAX}")
        fields.append(("changenote", vdf_value("change note", changenote)))
    return '"workshopitem"\n{\n' + "".join(f'\t"{k}"\t\t"{v}"\n' for k, v in fields) + "}\n"


def curly_quotes(text):
    """Double quotes as typographic ones, opening and closing in turn on each line."""
    out = []
    for line in text.split("\n"):
        parts = line.split('"')
        out.append("".join(p + ("“" if i % 2 == 0 else "”") for i, p in enumerate(parts[:-1])) + parts[-1])
    return "\n".join(out)


def inline_bbcode(text):
    text = re.sub(r"\[([^\]]+)\]\((https://[^)\s\"]+)\)", r"[url=\2]\1[/url]", text)
    text = re.sub(r"\*\*(.+?)\*\*", r"[b]\1[/b]", text)
    text = re.sub(r"`([^`]+)`", r"\1", text)
    return text


def release_bbcode(body):
    """A release's notes as Steam BBCode: its sections (from the first bold heading line) up to
    the paragraph saying where it was built, without the install steps after it."""
    lines = body.replace("\r\n", "\n").split("\n")
    start = next((i for i, l in enumerate(lines) if re.fullmatch(r"\*\*[^*]+\*\*", l.strip())), 0)
    out, in_list = [], False
    for line in lines[start:]:
        if line.startswith("Built by GitHub Actions"):
            break
        item = re.match(r"\s*[-*] (.*)", line)
        if item:
            if not in_list:
                out.append("[list]")
                in_list = True
            out.append("[*]" + inline_bbcode(item.group(1)))
            continue
        if in_list:
            out.append("[/list]")
            in_list = False
        out.append(inline_bbcode(line))
    if in_list:
        out.append("[/list]")
    return curly_quotes("\n".join(out).strip())


def change_note(releases):
    """releases: (label, release JSON as gh release view --json tagName,name,url,body,isDraft
    gives it). Each release's notes under its label, releases with the same notes under one
    heading; if that is longer than Steam takes, only the labels and links."""
    groups, short = {}, []
    for label, r in releases:
        head = f"[b]{label}: {r['tagName']}[/b] ([url={r['url']}]release[/url])"
        short.append(head)
        groups.setdefault(release_bbcode(r["body"]), []).append(head)
    text = "\n\n".join("\n".join(heads) + "\n" + body for body, heads in groups.items())
    return text if utf8_len(text) <= CHANGENOTE_MAX else "\n".join(short)


def item_releases(item, notes_dir):
    """(label, release) for each version in the item (moono.json: its tag and branch), from
    NOTES-DIR/TAG.json. A tag without a published release is refused: the item would be a dry
    run's."""
    out = []
    versions = ["."] + sorted(os.path.join("archived_versions", d) for d in os.listdir(os.path.join(item, "archived_versions")))
    for version in versions:
        with open(os.path.join(item, version, "moono.json"), encoding="utf-8") as f:
            meta = json.load(f)
        tag, branch = meta["tag"], meta["branch"]
        if not TAG.fullmatch(tag) or branch not in ("u60", "master"):
            fail(f"{version}/moono.json: unexpected tag {tag!r} or branch {branch!r}")
        path = os.path.join(notes_dir, tag + ".json")
        if not os.path.exists(path):
            fail(f"{tag} has no published release; an item built from release.yml runs is never uploaded")
        with open(path, encoding="utf-8") as f:
            release = json.load(f)
        if release.get("tagName") != tag or release.get("isDraft"):
            fail(f"{tag}: not a published release")
        out.append(("U60 build" if branch == "u60" else "U59 build", release))
    return out


def totp(shared_secret, now=None):
    """Steam Guard's mobile code: RFC 6238 over 30 s with HMAC-SHA1, as five of Steam's 26 letters
    and digits (as steamguard-cli's TwoFactorSecret::generate_code)."""
    key = base64.b64decode(shared_secret, validate=True)
    digest = hmac.new(key, struct.pack(">Q", int(time.time() if now is None else now) // 30), hashlib.sha1).digest()
    offset = digest[19] & 0x0F
    point = struct.unpack(">I", digest[offset : offset + 4])[0] & 0x7FFFFFFF
    code = ""
    for _ in range(5):
        code += STEAM_CHARS[point % len(STEAM_CHARS)]
        point //= len(STEAM_CHARS)
    return code


def login_script(user, password, code, vdf_path):
    """steamcmd's script: sign in, then upload the VDF (or, with vdf_path None, sign in alone).
    password None: the cached login in steamcmd's config.vdf, with no prompt of any kind."""
    if not LOGIN_NAME.fullmatch(user):
        fail("STEAM_USERNAME must be the account name: letters, digits and _")
    build = f"workshop_build_item {vdf_path}\n" if vdf_path is not None else ""
    if vdf_path is not None and (not os.path.isabs(vdf_path) or re.search(r"[\s;\"]|//", vdf_path)):
        fail(f"the VDF path must be absolute, without spaces, ';', '//' or quotes: {vdf_path!r}")
    if password is None:
        return f"@ShutdownOnFailedCommand 1\n@NoPromptForPassword 1\nlogin {user}\n{build}quit\n"
    if not PASSWORD.fullmatch(password) or "//" in password:
        fail("STEAM_PASSWORD has a character steamcmd's script would split or cut at (docs/WORKSHOP-UPLOAD.md)")
    # With a code, steamcmd must not prompt for anything. Without one, Steam asks the account's
    # phone to confirm the sign-in and steamcmd waits for that (its stdin is /dev/null).
    guard = f"@NoPromptForPassword 1\nset_steam_guard_code {code}\n" if code else ""
    return f"@ShutdownOnFailedCommand 1\n{guard}login {user} {password}\n{build}quit\n"


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text, mode=0o644):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("notes")
    p.add_argument("item")
    p.add_argument("notes_dir")
    p.add_argument("out")
    p = sub.add_parser("vdf")
    p.add_argument("--item", required=True)
    p.add_argument("--description", required=True)
    p.add_argument("--content")
    p.add_argument("--preview")
    p.add_argument("--changenote")
    p.add_argument("--visibility", default="keep")
    p.add_argument("--title")
    p.add_argument("out")
    p = sub.add_parser("login")
    p.add_argument("--cached", action="store_true", help="the cached login in steamcmd's config.vdf, no password")
    p.add_argument("--check", action="store_true", help="sign in alone; VDF is ignored")
    p.add_argument("vdf")
    p.add_argument("out")
    args = parser.parse_args()
    if args.command == "notes":
        write(args.out, change_note(item_releases(args.item, args.notes_dir)) + "\n")
    elif args.command == "vdf":
        text = vdf(
            args.item,
            read(args.description).rstrip("\n"),
            args.content,
            args.preview,
            read(args.changenote).rstrip("\n") if args.changenote else None,
            args.visibility,
            args.title,
        )
        write(args.out, text)
    else:
        env = os.environ
        vdf_path = None if args.check else os.path.abspath(args.vdf)
        if args.cached:
            if not env.get("STEAM_USERNAME"):
                fail("STEAM_USERNAME not set (the steam-workshop environment's secrets)")
            write(args.out, login_script(env["STEAM_USERNAME"].strip(), None, None, vdf_path), 0o600)
            print(f"{args.out}: steamcmd's script, with the cached login")
            return
        missing = [n for n in ("STEAM_USERNAME", "STEAM_PASSWORD") if not env.get(n)]
        if missing:
            fail(f"{', '.join(missing)} not set (the steam-workshop environment's secrets)")
        code = None
        # With the authenticator's shared_secret, a Steam Guard code; without it, the phone confirms.
        if env.get("STEAM_SHARED_SECRET", "").strip():
            # A code from the last seconds of its 30 s step may be stale by the time Steam checks it.
            if time.time() % 30 > 25:
                time.sleep(30 - time.time() % 30 + 1)
            try:
                code = totp(env["STEAM_SHARED_SECRET"].strip())
            except ValueError:
                fail("STEAM_SHARED_SECRET is not the base64 shared_secret")
            print(f"::add-mask::{code}", flush=True)
        write(args.out, login_script(env["STEAM_USERNAME"].strip(), env["STEAM_PASSWORD"], code, vdf_path), 0o600)
        print(f"{args.out}: steamcmd's login script")


if __name__ == "__main__":
    main()
