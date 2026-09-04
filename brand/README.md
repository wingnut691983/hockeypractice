# Brand source artwork

Master files that other assets are generated *from*. Nothing here is served or shipped.

Neither Dockerfile reaches this folder: `Dockerfile.fast` copies only `publish/`, and the
canonical `Dockerfile` copies only `global.json` and `HockeyPractice/`. So a large master image
costs nothing at runtime and never bloats the image, which matters here because an oversized
layer has already caused a registry upload to time out once.

Keep the largest version you have. Everything downscales from it, and nothing upscales well.

## What gets generated from `icon-source.png`

Generated icons live in `HockeyPractice/wwwroot/`, at the root rather than a subfolder, because
browsers and iOS probe fixed paths for two of them:

| File | Path it must live at | Used by |
|---|---|---|
| `favicon.ico` (16/32/48) | `/favicon.ico` | Browser tabs. Requested automatically |
| `apple-touch-icon.png` (180) | `/apple-touch-icon.png` | iOS home screen. Probed automatically |
| `icon-192.png`, `icon-512.png` | anywhere, named in the manifest | Android home screen, PWA install |

Do not put the source image in `wwwroot`. Everything there is served publicly and copied into
the container, so the master file would be a pointless public download and dead weight in the
image.
