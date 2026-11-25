/**
 * Hard patch-only release type for this repo.
 *
 * @semantic-release/commit-analyzer merges custom releaseRules with the
 * angular preset and then picks the *highest* matching type. That made
 * feat→patch in .releaserc lose to the preset's feat→minor, which cut
 * 1.1.0 / 1.2.0 / 1.3.0. This plugin never returns minor or major.
 */
const RELEASE_TYPES = new Set(["feat", "fix", "perf", "revert"]);

function commitType(commit) {
  if (commit && typeof commit.type === "string" && commit.type.length > 0) {
    return commit.type.toLowerCase();
  }
  const message = (commit && commit.message) || "";
  const match = /^(?:.*!\s*)?([a-zA-Z]+)(?:\([^)]*\))?[!()]?:/.exec(message);
  return match ? match[1].toLowerCase() : "";
}

function isReleasable(commit) {
  const type = commitType(commit);
  if (RELEASE_TYPES.has(type)) return true;
  const notes = (commit && commit.notes) || [];
  return notes.some(
    (note) =>
      note &&
      typeof note.title === "string" &&
      note.title.toUpperCase().includes("BREAKING")
  );
}

async function analyzeCommits(_pluginConfig, context) {
  const commits = (context && context.commits) || [];
  return commits.some(isReleasable) ? "patch" : null;
}

module.exports = { analyzeCommits };
