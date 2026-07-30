import { build } from "esbuild";
import { copyFile, mkdir, rm } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const outputDirectory = resolve(projectRoot, "wwwroot/js");
const licenseDirectory = resolve(projectRoot, "wwwroot/licenses");

// Remove old generated files first so a changed processor cannot be hidden by a
// stale build artifact.
await rm(outputDirectory, { recursive: true, force: true });
await mkdir(outputDirectory, { recursive: true });
await mkdir(licenseDirectory, { recursive: true });

await build({
    entryPoints: [resolve(projectRoot, "web-src/jampanion-audio.js")],
    outfile: resolve(outputDirectory, "jampanion-audio.js"),
    bundle: true,
    minify: true,
    format: "esm",
    platform: "browser",
    target: ["es2022"],
    legalComments: "eof"
});


await copyFile(
    resolve(projectRoot, "web-src/jampanion-browser.js"),
    resolve(outputDirectory, "jampanion-browser.js"));

await copyFile(
    resolve(projectRoot, "node_modules/spessasynth_lib/dist/spessasynth_processor.min.js"),
    resolve(outputDirectory, "spessasynth_processor.min.js"));
await copyFile(
    resolve(projectRoot, "node_modules/spessasynth_lib/LICENSE"),
    resolve(licenseDirectory, "SpessaSynth-Apache-2.0.txt"));
await copyFile(
    resolve(projectRoot, "node_modules/spessasynth_core/LICENSE"),
    resolve(licenseDirectory, "SpessaSynth-Core-Apache-2.0.txt"));
