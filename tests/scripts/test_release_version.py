"""scripts/release-version.sh: a release's archives are named by its full tag (v2026.11.20.05 ->
2026.11.20.05), so two releases of one day differ (#103); a development build (no tag, or none)
by HEAD's commit date; any other MOONO_RELEASE is refused."""

import os, subprocess, unittest

import helpers

SCRIPT = os.path.join(helpers.ROOT, 'scripts', 'release-version.sh')


def run(tag):
    env = {k: v for k, v in os.environ.items() if k != 'MOONO_RELEASE'}
    if tag is not None:
        env['MOONO_RELEASE'] = tag
    return subprocess.run(['bash', SCRIPT], env=env, capture_output=True, text=True)


class ReleaseVersionTests(unittest.TestCase):
    def test_tag_names_the_version(self):
        p = run('v2026.11.20.05')
        self.assertEqual((p.returncode, p.stdout), (0, '2026.11.20.05\n'), p.stderr)

    def test_development_builds_use_the_commit_date(self):
        for tag in (None, '', 'none'):
            p = run(tag)
            self.assertEqual(p.returncode, 0, p.stderr)
            self.assertRegex(p.stdout, r'^[0-9]{4}\.[0-9]{2}\.[0-9]{2}\n$')

    def test_other_forms_are_refused(self):
        for tag in ('2026.11.20.05', 'v2026.11.20', 'v2026.11.20.5', 'v2026.11.20.05-u60', 'vtest-2026.11.20.05-1'):
            p = run(tag)
            self.assertNotEqual(p.returncode, 0, tag)
            self.assertIn('MOONO_RELEASE must be', p.stderr)


if __name__ == '__main__':
    unittest.main()
