# Companion

This repository is the Windows companion. The website repository tracks it as the `osmbuddy-companion` submodule.

After you push `main` here, move that submodule in the website repository to this commit and commit the new gitlink.

A player build is a `<Version>` bump in `companion/MarketNotes.Companion/MarketNotes.Companion.csproj` plus a matching `vMAJOR.MINOR.PATCH` tag. GitHub Actions attaches `MarketNotes.Companion.exe` to that release.
