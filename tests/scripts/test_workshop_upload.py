"""scripts/workshop-upload.py: the VDF steamcmd reads raw (values as written, backslashes kept,
a double quote refused, visibility left out to keep it), the change note from the releases'
notes, Steam Guard's code from a shared_secret, and the login script."""

import json, os, stat, subprocess, sys, tempfile, unittest
from unittest import mock

import helpers

upload = helpers.script('workshop-upload.py')
SCRIPT = os.path.join(helpers.SCRIPTS, 'workshop-upload.py')
DESCRIPTION = 'Make game go fast\n\n[b]Uninstall:[/b] delete [i]%LOCALAPPDATA%\\oni-moono[/i].\n'

RELEASE_U60 = {
    'tagName': 'v2026.11.10.06',
    'name': '2026.11.10.06 (U60 Beta)',
    'url': 'https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.10.06',
    'isDraft': False,
    'body': (
        '**Only for the U60 public test.** On the normal game, use [the latest](https://example.com/latest).\n\n'
        'An update to the U60 beta.\n\n'
        '**Fixes**\n'
        '- A crash in FMOD ("0xC0000374 (heap corruption)") with `steam_api64.dll`. Reported in #72 by @someone.\n'
        '- **Sound memory guard**: on by default.\n\n'
        'Built by GitHub Actions from commit abc ([build](https://example.com/run)) and attested.\n\n'
        'Install or update: download the archive.\n'
    ),
}
RELEASE_MASTER = dict(
    RELEASE_U60,
    tagName='v2026.11.10.05',
    name='2026.11.10.05',
    url='https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.10.05',
    body='**Fixes**\n- One.\n',
)


class VdfTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def test_values_are_raw(self):
        text = upload.vdf('3837112063', DESCRIPTION.rstrip('\n'), content=self.dir, changenote='[b]U60[/b]')
        self.assertEqual(
            text,
            '"workshopitem"\n{\n'
            '\t"appid"\t\t"457140"\n'
            '\t"publishedfileid"\t\t"3837112063"\n'
            f'\t"contentfolder"\t\t"{self.dir}"\n'
            '\t"description"\t\t"Make game go fast\n\n[b]Uninstall:[/b] delete [i]%LOCALAPPDATA%\\oni-moono[/i]."\n'
            '\t"changenote"\t\t"[b]U60[/b]"\n'
            '}\n',
        )
        # One backslash stays one: steamcmd does not unescape.
        self.assertIn('%LOCALAPPDATA%\\oni-moono', text)
        self.assertNotIn('\\\\', text)

    def test_keep_visibility_leaves_the_key_out(self):
        self.assertNotIn('visibility', upload.vdf('1', 'd'))
        for name, value in (('public', 0), ('friends', 1), ('private', 2), ('unlisted', 3)):
            self.assertIn(f'\t"visibility"\t\t"{value}"\n', upload.vdf('1', 'd', visibility=name))
        with self.assertRaises(SystemExit):
            upload.vdf('1', 'd', visibility='hidden')

    def test_double_quotes_and_control_characters_are_refused(self):
        for bad in ('say "hi"', 'bell\x07'):
            with self.assertRaises(SystemExit):
                upload.vdf('1', bad)
            with self.assertRaises(SystemExit):
                upload.vdf('1', 'd', changenote=bad)

    def test_item_id(self):
        self.assertIn('"publishedfileid"\t\t"42"', upload.vdf('42', 'item ITEM_ID'))
        self.assertIn('item 42"', upload.vdf('42', 'item ITEM_ID'))
        for bad in ('0', '12a', '', '-1'):
            with self.assertRaises(SystemExit):
                upload.vdf(bad, 'd')

    def test_new_item_is_private_with_a_title(self):
        text = upload.vdf('new', 'd', title='Moono')
        self.assertIn('"publishedfileid"\t\t"0"', text)
        self.assertIn('"visibility"\t\t"2"', text)
        self.assertIn('"title"\t\t"Moono"', text)
        self.assertIn('"visibility"\t\t"3"', upload.vdf('new', 'd', title='Moono', visibility='unlisted'))
        with self.assertRaises(SystemExit):
            upload.vdf('new', 'd')
        with self.assertRaises(SystemExit):
            upload.vdf('new', 'item ITEM_ID', title='Moono')

    def test_lengths(self):
        upload.vdf('1', 'é' * 4000)
        with self.assertRaises(SystemExit):
            upload.vdf('1', 'é' * 4001)
        with self.assertRaises(SystemExit):
            upload.vdf('1', 'd', changenote='x' * 8001)

    def test_preview(self):
        gif = os.path.join(self.dir, 'p.gif')
        with open(gif, 'wb') as f:
            f.write(b'GIF89a' + b'\0' * 100)
        self.assertIn(f'"previewfile"\t\t"{gif}"', upload.vdf('1', 'd', preview=gif))
        with open(gif, 'wb') as f:
            f.write(b'GIF89a' + b'\0' * (1024 * 1024))
        with self.assertRaises(SystemExit):
            upload.vdf('1', 'd', preview=gif)
        with open(gif, 'wb') as f:
            f.write(b'<html>')
        with self.assertRaises(SystemExit):
            upload.vdf('1', 'd', preview=gif)
        with self.assertRaises(SystemExit):
            upload.vdf('1', 'd', preview='p.gif')

    def test_cli(self):
        desc = os.path.join(self.dir, 'description.bbcode')
        with open(desc, 'w') as f:
            f.write(DESCRIPTION)
        out = os.path.join(self.dir, 'item.vdf')
        p = subprocess.run(
            [sys.executable, '-I', SCRIPT, 'vdf', '--item', '7', '--description', desc, '--content', self.dir, out], capture_output=True, text=True
        )
        self.assertEqual(p.returncode, 0, p.stderr)
        with open(out) as f:
            self.assertIn('oni-moono[/i]."\n}', f.read())


class ChangeNoteTests(unittest.TestCase):
    def test_release_bbcode(self):
        self.assertEqual(
            upload.release_bbcode(RELEASE_U60['body']),
            '[b]Fixes[/b]\n[list]\n'
            '[*]A crash in FMOD (“0xC0000374 (heap corruption)”) with steam_api64.dll. Reported in #72 by @someone.\n'
            '[*][b]Sound memory guard[/b]: on by default.\n[/list]',
        )

    def test_change_note_and_its_short_form(self):
        note = upload.change_note([('U60 build', RELEASE_U60), ('U59 build', RELEASE_MASTER)])
        self.assertTrue(
            note.startswith(
                '[b]U60 build: v2026.11.10.06[/b] ([url=https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.10.06]release[/url])\n[b]Fixes[/b]'
            )
        )
        self.assertIn('\n\n[b]U59 build: v2026.11.10.05[/b]', note)
        self.assertNotIn('"', note)
        upload.vdf('1', 'd', changenote=note)
        # The same notes (as paired releases often have) come once, under both headings.
        same = upload.change_note([('U60 build', RELEASE_U60), ('U59 build', dict(RELEASE_U60, tagName='v2026.11.10.05'))])
        self.assertEqual(same.count('[b]Fixes[/b]'), 1)
        self.assertIn('release[/url])\n[b]U59 build: v2026.11.10.05[/b]', same)
        long = dict(RELEASE_U60, body='**Fixes**\n' + '- x\n' * 5000)
        short = upload.change_note([('U60 build', long), ('U59 build', RELEASE_MASTER)])
        self.assertEqual(short.count('\n'), 1)
        self.assertNotIn('[list]', short)

    def test_item_releases(self):
        with tempfile.TemporaryDirectory() as d:
            item, notes = os.path.join(d, 'item'), os.path.join(d, 'notes')
            os.makedirs(os.path.join(item, 'archived_versions', 'u59'))
            os.makedirs(notes)
            for folder, tag, branch in (('', 'v2026.11.10.06', 'u60'), ('archived_versions/u59', 'v2026.11.10.05', 'master')):
                with open(os.path.join(item, folder, 'moono.json'), 'w') as f:
                    json.dump({'tag': tag, 'branch': branch}, f)
            with open(os.path.join(notes, 'v2026.11.10.06.json'), 'w') as f:
                json.dump(RELEASE_U60, f)
            # A tag without a published release (a dry run's item) is refused.
            with self.assertRaises(SystemExit):
                upload.item_releases(item, notes)
            with open(os.path.join(notes, 'v2026.11.10.05.json'), 'w') as f:
                json.dump(dict(RELEASE_MASTER, isDraft=True), f)
            with self.assertRaises(SystemExit):
                upload.item_releases(item, notes)
            with open(os.path.join(notes, 'v2026.11.10.05.json'), 'w') as f:
                json.dump(RELEASE_MASTER, f)
            self.assertEqual(
                [(l, r['tagName']) for l, r in upload.item_releases(item, notes)], [('U60 build', 'v2026.11.10.06'), ('U59 build', 'v2026.11.10.05')]
            )


class LoginTests(unittest.TestCase):
    def test_totp(self):
        # steamguard-cli's test vector (steamguard/src/token.rs, test_generate_code).
        self.assertEqual(upload.totp('zvIayp3JPvtvX/QGHqsqKBk/44s=', 1616374841), '2F9J5')
        self.assertEqual(upload.totp('zvIayp3JPvtvX/QGHqsqKBk/44s=', 1616374841 - 1616374841 % 30), '2F9J5')
        with self.assertRaises(ValueError):
            upload.totp('not base64!')

    def test_login_script(self):
        self.assertEqual(
            upload.login_script('moono_ci', 'Abc123xyz', '2F9J5', '/tmp/x/item.vdf'),
            '@ShutdownOnFailedCommand 1\n@NoPromptForPassword 1\nset_steam_guard_code 2F9J5\n'
            'login moono_ci Abc123xyz\nworkshop_build_item /tmp/x/item.vdf\nquit\n',
        )
        # Without a code, no Steam Guard line: the phone confirms the sign-in.
        self.assertEqual(
            upload.login_script('qigit', 'Abc123xyz', None, '/tmp/x/item.vdf'),
            '@ShutdownOnFailedCommand 1\nlogin qigit Abc123xyz\nworkshop_build_item /tmp/x/item.vdf\nquit\n',
        )
        for user, password, path in (
            ('a b', 'Abc123xyz', '/v'),
            ('moono', 'has space1', '/v'),
            ('moono', 'semi;colon1', '/v'),
            ('moono', 'cut//here1', '/v'),
            ('moono', 'quote"d123', '/v'),
            ('moono', 'Abc123xyz', 'rel.vdf'),
            ('moono', 'Abc123xyz', '/a b.vdf'),
        ):
            with self.assertRaises(SystemExit):
                upload.login_script(user, password, '2F9J5', path)

    def test_login_cli_writes_a_private_script_and_masks_the_code(self):
        with tempfile.TemporaryDirectory() as d:
            out = os.path.join(d, 'login.txt')
            env = {
                'PATH': os.environ['PATH'],
                'STEAM_USERNAME': 'moono_ci',
                'STEAM_PASSWORD': 'Abc123xyz',
                'STEAM_SHARED_SECRET': 'zvIayp3JPvtvX/QGHqsqKBk/44s=',
            }
            with (
                mock.patch('time.time', return_value=1616374841.0),
                mock.patch.dict(os.environ, env, clear=True),
                mock.patch('sys.argv', ['workshop-upload.py', 'login', os.path.join(d, 'item.vdf'), out]),
                mock.patch('builtins.print') as printed,
            ):
                upload.main()
            self.assertEqual(printed.call_args_list[0].args, ('::add-mask::2F9J5',))
            self.assertEqual(stat.S_IMODE(os.stat(out).st_mode), 0o600)
            with open(out) as f:
                self.assertIn('set_steam_guard_code 2F9J5\nlogin moono_ci Abc123xyz\n', f.read())
            # No shared_secret: no code, nothing to mask.
            confirm = os.path.join(d, 'confirm.txt')
            env.pop('STEAM_SHARED_SECRET')
            with (
                mock.patch.dict(os.environ, env, clear=True),
                mock.patch('sys.argv', ['workshop-upload.py', 'login', os.path.join(d, 'item.vdf'), confirm]),
                mock.patch('builtins.print') as printed,
            ):
                upload.main()
            self.assertFalse([c for c in printed.call_args_list if 'add-mask' in str(c)])
            with open(confirm) as f:
                self.assertNotIn('set_steam_guard_code', f.read())
            with (
                mock.patch.dict(os.environ, {'STEAM_USERNAME': 'x'}, clear=True),
                mock.patch('sys.argv', ['workshop-upload.py', 'login', 'v', os.path.join(d, 'other.txt')]),
            ):
                with self.assertRaises(SystemExit):
                    upload.main()


if __name__ == '__main__':
    unittest.main()
