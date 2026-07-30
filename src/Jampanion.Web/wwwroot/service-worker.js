const CACHE_NAME = "jampanion-web-v18";
const SHELL = [
    "./",
    "./index.html",
    "./css/app.css?v=18",
    "./manifest.webmanifest?v=18",
    "./icons/jampanion-32.png?v=18",
    "./icons/jampanion-48.png?v=18",
    "./icons/jampanion-180.png?v=18",
    "./icons/jampanion-192.png?v=18",
    "./icons/jampanion-512.png?v=18",
    "./icons/jampanion-maskable-192.png?v=18",
    "./icons/jampanion-maskable-512.png?v=18"
];

self.addEventListener("install", event => {
    event.waitUntil(
        caches.open(CACHE_NAME).then(cache => cache.addAll(
            SHELL.map(path => new Request(path, { cache: "reload" }))
        ))
    );
    self.skipWaiting();
});

self.addEventListener("activate", event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(
                keys.filter(key => key.startsWith("jampanion-web-") && key !== CACHE_NAME)
                    .map(key => caches.delete(key))))
            .then(() => self.clients.claim())
    );
});

async function networkFirst(request, fallback) {
    const cache = await caches.open(CACHE_NAME);
    try {
        const response = await fetch(request);
        if (response.ok && response.type === "basic") {
            void cache.put(request, response.clone());
        }
        return response;
    } catch {
        return (await caches.match(request)) || (fallback ? await caches.match(fallback) : null) || Response.error();
    }
}

self.addEventListener("fetch", event => {
    const request = event.request;
    if (request.method !== "GET") return;

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    if (request.mode === "navigate") {
        event.respondWith(networkFirst(request, "./index.html"));
        return;
    }

    // The SoundFont is large and immutable within a release, so favor the
    // installed copy. App code, CSS, manifest and framework files are
    // network-first to prevent an older worker from mixing older and v18 assets.
    if (url.pathname.includes("/soundfonts/")) {
        event.respondWith(
            caches.match(request).then(cached => cached || networkFirst(request))
        );
        return;
    }

    event.respondWith(networkFirst(request));
});
