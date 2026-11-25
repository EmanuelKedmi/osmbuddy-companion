# Companion

This repository is the Windows companion. The website repository tracks it as the `osmbuddy-companion` submodule.

After you push `main` here, move that submodule in the website repository to this commit and commit the new gitlink.

## Versioning (Sagiv)

- Every new feature must progress the version. Do **not** stack multiple features into one version.
- Default bump is **patch only** (`z` in `x.y.z`).
- Bumping **minor** (`y`) or **major** (`x`) requires **explicit instruction from Sagiv**. Never do that on your own.
- A `feat` or `fix` commit on `main` publishes the player build as the next **patch**. semantic-release creates the `vMAJOR.MINOR.PATCH` tag, and the same workflow stamps that version onto `MarketNotes.Companion.exe`. A docs, chore, or ci commit publishes nothing. Leave `<Version>` in the project file unchanged (it stays a `1.0.3` placeholder; CI passes `-p:Version=…`, and local publishes stamp the latest `v*` tag so the UI does not show 1.0.3).
- Cursor agents also follow `.cursor/rules/versioning.mdc`.
