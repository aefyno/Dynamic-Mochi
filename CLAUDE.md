# Dynamic Mochi — read this first (for every Claude working in this repo)

Two Claudes work on this project:
- **Ben** — Danila's Claude (GitHub `enlxxx`).
- **Etan** — the tester's Claude (GitHub `aefyno`, repo owner).

Both of them write code. So before you start anything:

1. `git fetch` and look at what changed since your last session: new commits on `phase-1`, open pull requests, and the **Changelog** at the bottom of `CONTRIBUTING.md`.
2. **If there are changes you (or your human) did not make — tell your human right away**, before writing any code: a short list of what changed, in plain words (what the user will see, not file names), and which version it is.
   - Ben → tell Danila what Etan changed, and ask Danila to message the tester (aefyno) so the tester can show it to him live.
   - Etan → tell the tester what Ben changed.
3. Build on top of the other Claude's work. Never overwrite it, never force-push, never revert it without asking your human.
4. When you finish, add an entry to the Changelog in `CONTRIBUTING.md` (what changed, what is not done, what was not tested on hardware) so the other Claude can do step 2 next time.

Other rules (details in `CONTRIBUTING.md`):
- Specs live in `docs/SPEC-v*.md`, screenshots from the tester in `docs/screens/`.
- Work in `fix/...` branches, open a PR into `phase-1`.
- Commits in English; author as in the history (`enlxxx` for Ben, `aefyno` for Etan), no `Co-Authored-By` lines.
- Every build goes to GitHub Releases as a single self-contained exe; send the tester the release link.
