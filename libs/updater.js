// Update checker — queries GitHub Releases API for latest version
const https = require("https");
const semver = require("semver");

const GITHUB_API_URL = "https://api.github.com/repos/marchsnow-1/miditap/releases/latest";

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
// 检查是否有可用更新。
// Returns:
//   { latest, current, url } — when an update is available
//   null — when current >= latest, or the check fails (network error, etc.)
// 调用方无需 try/catch：任何失败（网络错误、非 200、解析失败）都会在此吞掉
// 并返回 null，避免未处理的 Promise rejection。
async function checkForUpdates(currentVersion) {
  try {
    const release = await fetchLatestRelease();
    const latestVersion = semver.valid(release.tag_name);
    const current = semver.valid(currentVersion);
    if (!latestVersion || !current) return null;

    if (semver.gt(latestVersion, current)) {
      return {
        latest: release.tag_name,
        current: currentVersion,
        url: "https://github.com/marchsnow-1/miditap/releases/latest",
      };
    }
    return null;
  } catch (err) {
    // 吞掉并返回 null（与文档契约一致），但保留一条日志便于排查网络/API 问题。
    // Swallow failures and return null per the documented contract, logging a
    // warning so network/API problems remain diagnosable.
    console.warn("[miditap.updater]: Update check failed: " + (err && err.message));
    return null;
  }
}

module.exports = { checkForUpdates };
