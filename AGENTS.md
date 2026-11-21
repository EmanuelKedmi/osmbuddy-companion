# Companion

This repository is the Windows companion. The website repository tracks it as the `osmbuddy-companion` submodule.

After you push `main` here, move that submodule in the website repository to this commit and commit the new gitlink.

A `feat` or `fix` commit on `main` publishes the player build. semantic-release creates the `vMAJOR.MINOR.PATCH` tag, and the same workflow stamps that version onto `MarketNotes.Companion.exe`. A docs, chore, or ci commit publishes nothing. Leave `<Version>` in the project file unchanged.
