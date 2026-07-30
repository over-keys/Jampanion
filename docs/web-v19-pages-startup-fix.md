# Web v19 Pages startup fix

This update corrects the first GitHub Pages deployment of Web v18.

- The downloaded Pages artifact was verified in Chromium and rendered the complete Autumn Leaves chart.
- The production page now removes Jampanion service workers and `jampanion-web-*` caches once when the application version changes.
- If the page was controlled by an older worker, it reloads once after unregistering it before loading Blazor.
- Production service-worker registration is temporarily disabled to prevent mixed framework versions during rapid Web development.
- The Pages workflow now actually invokes `scripts/verify-pages-smoke.sh` after publish and before artifact upload.
- The smoke test requires the Blazor loading screen to disappear and the Start session and Chord Sheet UI to render in headless Chrome.
