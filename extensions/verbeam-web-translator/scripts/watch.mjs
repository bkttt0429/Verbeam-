import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const EXTENSION_ROOT = path.resolve(__dirname, "..");
const RELOAD_SCRIPT = path.resolve(__dirname, "reload.mjs");

const IGNORED_NAMES = new Set([
  ".git",
  "node_modules",
  "scripts",
  ".DS_Store",
  "Thumbs.db"
]);

let debounceTimer = null;
let isReloading = false;

function isWatched(filePath) {
  const relative = path.relative(EXTENSION_ROOT, filePath);
  if (!relative || relative.startsWith("..")) {
    return false;
  }

  const parts = relative.split(path.sep);
  return !parts.some((part) => IGNORED_NAMES.has(part));
}

function reloadExtension() {
  if (isReloading) {
    return;
  }

  isReloading = true;
  const child = spawn(process.execPath, [RELOAD_SCRIPT], {
    stdio: "inherit",
    windowsHide: true
  });

  child.on("error", (error) => {
    console.error("Failed to run reload script:", error.message);
    isReloading = false;
  });

  child.on("exit", (code) => {
    isReloading = false;
    if (code !== 0) {
      console.error(`Reload script exited with code ${code}.`);
    }
  });
}

function scheduleReload(filePath) {
  if (!isWatched(filePath)) {
    return;
  }

  console.log(`Changed: ${path.relative(EXTENSION_ROOT, filePath)}`);
  if (debounceTimer) {
    clearTimeout(debounceTimer);
  }
  debounceTimer = setTimeout(() => {
    reloadExtension();
  }, 300);
}

function watchDirectory(dir) {
  const watcher = fs.watch(dir, { recursive: false }, (eventType, filename) => {
    if (!filename) {
      return;
    }
    const fullPath = path.resolve(dir, filename);
    scheduleReload(fullPath);
  });

  watcher.on("error", (error) => {
    console.error(`Watch error on ${dir}:`, error.message);
  });

  // Recursively watch subdirectories
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (!entry.isDirectory()) {
      continue;
    }
    const childPath = path.resolve(dir, entry.name);
    if (!isWatched(childPath)) {
      continue;
    }
    watchDirectory(childPath);
  }
}

console.log(`Watching ${EXTENSION_ROOT} for changes...`);
console.log("Make sure Chrome is running with --remote-debugging-port=9222");
watchDirectory(EXTENSION_ROOT);
