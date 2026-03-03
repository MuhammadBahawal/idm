const fs = require("node:fs");
const path = require("node:path");

const arg = process.argv.find((value) => value.startsWith("--browser="));
const browser = (arg ? arg.split("=")[1] : "all").toLowerCase();
const targets = browser === "all" ? ["chromium", "firefox"] : [browser];
const supported = new Set(["chromium", "firefox"]);

for (const target of targets) {
    if (!supported.has(target)) {
        console.error(`Unsupported browser target: ${target}`);
        process.exit(1);
    }
}

const rootDir = __dirname;
const outputRoot = path.join(rootDir, "dist-pack");
const sharedEntries = ["dist", "icons", "popup.html", "options.html"];
const manifestByTarget = {
    chromium: "manifest.chromium.json",
    firefox: "manifest.firefox.json"
};

function assertExists(filePath) {
    if (!fs.existsSync(filePath)) {
        throw new Error(`Missing required path: ${filePath}`);
    }
}

function prepareTarget(target) {
    const targetDir = path.join(outputRoot, target);
    fs.rmSync(targetDir, { recursive: true, force: true });
    fs.mkdirSync(targetDir, { recursive: true });

    for (const entry of sharedEntries) {
        const src = path.join(rootDir, entry);
        const dest = path.join(targetDir, entry);
        assertExists(src);
        fs.cpSync(src, dest, { recursive: true });
    }

    const manifestTemplate = path.join(rootDir, manifestByTarget[target]);
    assertExists(manifestTemplate);
    fs.copyFileSync(manifestTemplate, path.join(targetDir, "manifest.json"));

    return targetDir;
}

try {
    fs.mkdirSync(outputRoot, { recursive: true });
    for (const target of targets) {
        const targetDir = prepareTarget(target);
        console.log(`Prepared ${target} extension package at: ${targetDir}`);
    }
} catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exit(1);
}
