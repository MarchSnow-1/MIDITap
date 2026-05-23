// Update checker — queries GitHub Releases API for latest version
const https = require("https");

const GITHUB_API_URL = "https://api.github.com/repos/marchsnow-1/miditap/releases/latest";

// Normalize version string: strip leading 'v', trim whitespace
function normalizeVersion(ver) {
  if (!ver) return null;
  let v = String(ver).trim();
  if (v.length > 1 && (v[0] === "v" || v[0] === "V") && v[1] >= "0" && v[1] <= "9") {
    v = v.slice(1);
  }
  return v;
}

// Compare two semver strings. Returns:
//  -1 if a < b
//   1 if a > b
//   0 if equal
function compareVersions(a, b) {
  const an = normalizeVersion(a);
  const bn = normalizeVersion(b);
  if (!an || !bn) return 0;

  const aParts = an.split(".").map(Number);
  const bParts = bn.split(".").map(Number);
  const len = Math.max(aParts.length, bParts.length);

  for (let i = 0; i < len; i++) {
    const av = aParts[i] || 0;
    const bv = bParts[i] || 0;
    if (av > bv) return 1;
    if (av < bv) return -1;
  }
  return 0;
}

// Fetch latest release from GitHub API
function fetchLatestRelease() {
  return new Promise((resolve, reject) => {
    const url = new URL(GITHUB_API_URL);
    const options = {
      hostname: url.hostname,
      path: url.pathname + url.search,
      method: "GET",
      headers: {
        "User-Agent": "MIDITap-updater/1.0",
        "Accept": "application/vnd.github.v3+json",
      },
      timeout: 8000,
    };

    const req = https.request(options, (res) => {
      let body = "";
      res.on("data", (chunk) => { body += chunk; });
      res.on("end", () => {
        if (res.statusCode !== 200) {
          reject(new Error("GitHub API returned status " + res.statusCode));
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (e) {
          reject(new Error("Failed to parse GitHub API response"));
        }
      });
    });

    req.on("error", (err) => {
      reject(new Error("Network error: " + err.message));
    });
    req.on("timeout", () => {
      req.destroy();
      reject(new Error("Request timed out"));
    });
    req.end();
  });
}

// Check if an update is available.
// Returns:
//   { latest, current, url } — when an update is available
//   null — when current >= latest or the check fails
async function checkForUpdates(currentVersion) {
  try {
    const release = await fetchLatestRelease();
    const latestVersion = normalizeVersion(release.tag_name);
    if (!latestVersion) return null;

    if (compareVersions(latestVersion, currentVersion) > 0) {
      return {
        latest: release.tag_name,
        current: currentVersion,
        url: "https://github.com/marchsnow-1/miditap/releases/latest",
      };
    }
    return null;
  } catch (err) {
    throw err;
  }
}

module.exports = { checkForUpdates, compareVersions };
