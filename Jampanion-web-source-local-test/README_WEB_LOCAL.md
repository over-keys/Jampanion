# Jampanion Web: local test

This archive contains the complete browser-side source additions, the verified
FluidR3_Jampanion.sf3 SoundFont, and the GitHub Pages workflow.

It is intended to be extracted directly into the root of an existing Jampanion
source checkout, because `src/Jampanion.Web` references the existing
`src/Jampanion.Core/Jampanion.Core.csproj` project.

## Requirements

- .NET SDK 10
- Node.js 20 or later (Node.js 22 or 24 recommended)
- npm

No SoundFont download or conversion is required.

## macOS / Linux

From the Jampanion repository root:

```bash
chmod +x run-web-local.sh
./run-web-local.sh
```

Then open:

```text
http://localhost:5279/
```

Use another port if necessary:

```bash
./run-web-local.sh 5280
```

To reuse an existing `node_modules` directory:

```bash
SKIP_NPM_INSTALL=1 ./run-web-local.sh
```

## Windows PowerShell

From the Jampanion repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-web-local.ps1
```

Use another port if necessary:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-web-local.ps1 -Port 5280
```

To reuse an existing `node_modules` directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-web-local.ps1 -SkipNpmInstall
```

## Manual commands

```bash
cd src/Jampanion.Web
npm install --no-audit --no-fund
npm run build
cd ../..
dotnet run --project src/Jampanion.Web/Jampanion.Web.csproj --urls http://localhost:5279
```

## SoundFont verification

Expected SHA-256:

```text
2e4aa17f20743930c87ada7cc1fee2228ecd2bb0e2de75a83cd590c53bcd0d63
```

SoundFont path:

```text
src/Jampanion.Web/wwwroot/soundfonts/FluidR3_Jampanion.sf3
```

The local launch scripts verify the hash before starting the app.

## GitHub Pages

The public web version is available at:

[https://over-keys.github.io/Jampanion/](https://over-keys.github.io/Jampanion/)

GitHub Pages uses **GitHub Actions** as its deployment source. The included
workflow is:

```text
.github/workflows/deploy-pages.yml
```
