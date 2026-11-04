// The Revit Matrix - build and MSI packaging, driven by package.json.
//
//   npm run publish   -> build\publish\TheRevitMatrix.exe (+ help\)
//   npm run msi       -> build\msi\TheRevitMatrix-<version>-x64.msi (from an existing publish)
//   npm run package   -> clean + publish + msi
//   npm run clean     -> delete build\publish and build\msi (leaves build\msix alone)
//
// The version comes from package.json "version" and is stamped into both the exe and the MSI,
// so bump it there (e.g. `npm version patch`) before packaging a release.
// Needs Windows, the .NET 8 SDK and Node 18+. WiX is restored from NuGet automatically.

import { readFileSync, rmSync, existsSync, readdirSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const pkg = JSON.parse(readFileSync(join(root, "package.json"), "utf8"));
const cfg = pkg.msi;

// MSI versions must be numeric major.minor.build (each part limited): drop any "-beta" etc.
const version = pkg.version.split(/[-+]/)[0];
if (!/^\d+\.\d+\.\d+$/.test(version)) fail(`package.json version "${pkg.version}" must look like 1.2.3`);

const publishDir = join(root, cfg.publishDir);
const outputDir = join(root, cfg.outputDir);

function fail(msg) {
  console.error(`\n✖ ${msg}`);
  process.exit(1);
}

function dotnet(args) {
  console.log(`\n> dotnet ${args.join(" ")}`);
  const r = spawnSync("dotnet", args, { cwd: root, stdio: "inherit" });
  if (r.error) fail(`Could not run dotnet (${r.error.message}). Install the .NET 8 SDK.`);
  if (r.status !== 0) fail(`dotnet exited with code ${r.status}`);
}

function clean() {
  // Only this script's output: the MSIX build (npm run msix) keeps its own folder.
  rmSync(publishDir, { recursive: true, force: true });
  rmSync(outputDir, { recursive: true, force: true });
  console.log(`Removed ${cfg.publishDir} and ${cfg.outputDir}`);
}

function publish() {
  rmSync(publishDir, { recursive: true, force: true });
  dotnet([
    "publish", cfg.project,
    "-c", "Release",
    "-r", cfg.runtime,
    "--self-contained", String(cfg.selfContained),
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    `-p:Version=${version}`,
    "-o", publishDir,
  ]);
}

function msi() {
  if (!existsSync(join(publishDir, "TheRevitMatrix.exe"))) fail("Nothing published yet - run `npm run publish` first.");
  dotnet([
    "build", cfg.installer,
    "-c", "Release",
    `-p:Version=${version}`,
    `-p:AppPublishDir=${publishDir}\\`,
    "-o", outputDir,
  ]);
  const files = existsSync(outputDir)
    ? readdirSync(outputDir, { recursive: true }).filter((f) => String(f).endsWith(".msi"))
    : [];
  console.log(files.length ? `\n✔ ${files.map((f) => join(outputDir, f)).join("\n✔ ")}` : "\n(no .msi found in output)");
}

const step = process.argv[2] ?? "package";
console.log(`${pkg.productName} ${version} - ${step}`);
switch (step) {
  case "clean": clean(); break;
  case "publish": publish(); break;
  case "msi": msi(); break;
  case "package": clean(); publish(); msi(); break;
  default: fail(`Unknown step "${step}". Use clean, publish, msi or package.`);
}
