# Orbital Sentinel web

React, TypeScript, and CesiumJS dashboard for the first catalog-and-propagation increment. The backend is authoritative for orbital elements, propagation, and coordinate conversion. No orbital dynamics or SGP4 library runs in the browser.

## Run locally

Use Node.js 22.12+ (Node.js 24 is used by the container).

```sh
npm ci
npm run dev
```

Open [localhost:5173](http://localhost:5173). Vite forwards `/api` and `/hubs` to `http://localhost:5080`, matching the backend port exposed by the root Compose file. Set `API_PROXY_TARGET` to change that target. For the complete application, run `docker compose up --build` from the repository root and open [localhost:8088](http://localhost:8088). Set `WEB_PORT` to change the Compose web port.

```sh
npm test
npm run build
```

`npm run preview` serves the production files but does not proxy API requests. Use the Nginx container to verify the complete production deployment.

## Responsibilities and data flow

- `src/api` validates the responses used for rendering, applies a 12-second request timeout, respects cancellation, and exposes safe ProblemDetails titles. HTTP 202 predictions are a pending state, not an error.
- `src/domain` contains the view contracts, freshness checks, and linear interpolation of Earth-fixed positions for display. It does not convert coordinate frames or recalculate orbits.
- `src/hooks` owns polling, request cleanup, and stable SignalR notifications. Catalog polling runs once per minute; the compact positions endpoint runs every 15 seconds. The selected object's trajectory is the only full trajectory downloaded. Requests do not overlap for a resource; canceled or superseded requests cannot replace newer results.
- `src/components/Globe.tsx` owns Cesium lifecycle and rendering. Backend `positionEcefMeters` values enter `Cartesian3` directly as metres in `ReferenceFrame.FIXED`.

The UI loads the first 500 catalog entries, matching the configured catalog limit. Search and type filters apply to that loaded set, with an explicit message if the total exceeds 500. The backend uses the supplied object type, or clear CelesTrak `DEB`/`R/B` name markers when that field is absent. Ambiguous names remain **Unclassified**. The client recognizes the full type labels and SATCAT codes `PAY`, `R/B`, `DEB`, and `UNK`; it does not make additional guesses from names. Filters, globe colors, and detail labels use the same classification.

The API's frame is an approximate Earth-fixed representation derived from TEME by the backend. It is not precise ITRF. Cesium renders these coordinates against its WGS84 globe. The frontend does not interpret TEME vectors as latitude/longitude.

## Time and scientific honesty

“Now” shows backend-predicted positions, **not observed telemetry**. Catalog markers display samples from the compact position feed and are hidden when their timestamp differs from current UTC by more than two minutes. The selected marker uses Cesium's linear interpolation between backend trajectory samples and is unavailable outside their time window; it is never extrapolated.

The timeline previews the selected object's entire prediction window. While previewing, other catalog markers are hidden so samples from different times are not presented as a shared scene. The Earth-fixed path shows up to one orbital period ahead, capped at three hours to keep it readable. This path is a visualization of sampled future Earth-fixed positions, not a closed inertial Keplerian orbit.

An epoch older than 48 hours gets an age warning. This is a UI freshness threshold, **not an accuracy guarantee**. Public general-perturbation elements and linear visualization interpolation cannot support operational conjunction assessment. The Conjunctions tab displays backend-computed closest approaches and immutable assessment history. Selecting an encounter displays both original-version trajectories with exact TCA samples and a closest-approach marker. The Create simulation dialog starts a separate deterministic scenario with an explicitly paused virtual clock. Real and simulation sources remain separate. Risk classification, collision probability, alerts, and avoidance manoeuvres are not implemented.

## Delivery and reliability

The footer's About link opens the project notes and scientific limitations. Privacy explains the absence of application cookies, persistent preferences, analytics, and advertising trackers, while distinguishing normal browser asset caching and operational server logs. Both dialogs support keyboard dismissal. The operational spacecraft warning remains visible in the footer.

Cesium's official prebuilt distribution, workers, widgets, and Natural Earth II imagery are copied from the pinned npm package into `public/cesium` before development or production builds. No Cesium ion token, paid imagery service, remote font, or external globe request is needed. Built assets are generated and ignored by Git. The prebuilt distribution avoids bundling Cesium's large dependency graph on every Vite build and bounds build memory. Cesium/Natural Earth credits remain visible.

The Nginx container serves the SPA and proxies API calls and SignalR WebSocket upgrades to `backend:8080`. `/health` checks the web server. Hashed app assets are cached; the HTML entry is not. Deployment behind HTTPS should terminate TLS at a trusted ingress and configure forwarded headers there.

SignalR `TrajectoryAvailable` messages are coalesced for one second before refreshing compact data. Only a notification for the selected object invalidates its full trajectory. Automatic reconnect uses bounded delays; if SignalR cannot connect or exhausts reconnection, the UI says **Polling fallback** and continues REST polling. HTTP 202 uses a Retry-After interval clamped to 5–60 seconds; all other trajectory refreshes occur once per minute. This is periodic observation of server state, not unlimited retries inside a request.

Empty catalogs, initial loading, pending propagation, service errors, stale positions, expired predictions, and missing WebGL all have explicit states. No sample catalog silently replaces live data when the backend is unavailable.

## Verification

Vitest covers nullable external identity, invalid coordinates, trajectory ordering/window validation, rejecting a different object's trajectory, bounded pending polling, safe API errors, cancellation, interpolation boundaries, and stale/future sample handling. `npm run build` performs strict TypeScript checking before production bundling. Browser verification should additionally check selection/search, type filters, timeline preview, current-time reset, trajectory visibility, local globe imagery, SignalR status, and the responsive layout against the running backend.
