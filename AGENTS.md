# Repository workflow

- Read CONTRIBUTING.md before changing this repository.
- Start from an up-to-date main and create a dedicated feature/, fix/, or chore/ branch. Never commit or push implementation changes directly to main.
- Preserve unrelated user changes. Do not force-push shared branches or rewrite published history.
- Keep changes scoped; run appropriate checks, commit clearly, push the branch, and open a pull request targeting main.
- Review the complete diff and confirm the PR checks job passes for the latest revision. CI success alone is not owner approval.
- Merge only after the owner explicitly approves the concrete pull request. Do not bypass branch protection. If approval is pending, report the PR URL and check results.
- After an approved merge, delete only the merged working branch, switch to main, and pull with --ff-only. Preserve archive branches.
- The desktop app and device firmware are maintained in separate repositories. Do not reintroduce cross-repository source/build dependencies.

