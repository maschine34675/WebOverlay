# WebOverlay icon concepts

Monochrome icon set for Anvil-WebOverlay. All final assets use a pure black
background and a white, flat geometric glyph. The generated masters were
thresholded to strict black/white; the smaller exports retain only the gray
edge pixels introduced by high-quality downscaling.

## Variants

| No. | Concept | Master | 144 px |
|---|---|---|---|
| 01 | Floating browser panel in front of a monitor | `masters/weboverlay-01-floating-window-master.png` | `144/weboverlay-01-floating-window-144.png` |
| 02 | Two offset and interlocking browser windows | `masters/weboverlay-02-stacked-windows-master.png` | `144/weboverlay-02-stacked-windows-144.png` |
| 03 | Browser panel inside viewport corner brackets | `masters/weboverlay-03-viewport-panel-master.png` | `144/weboverlay-03-viewport-panel-144.png` |
| 04 | Browser window inside a geometric visor/eye | `masters/weboverlay-04-visor-window-master.png` | `144/weboverlay-04-visor-window-144.png` |
| 05 | Browser window split into two offset layers | `masters/weboverlay-05-split-layer-master.png` | `144/weboverlay-05-split-layer-144.png` |
| 06 | Browser panel inside a HUD-style frame | `masters/weboverlay-06-hud-frame-master.png` | `144/weboverlay-06-hud-frame-144.png` |

The matching profile icon is a geometric anvil:

- `masters/anvil-profile-master.png` — 1254 x 1254 master
- `anvil-profile-512.png` — 512 x 512 profile export
- `144/anvil-profile-144.png` — 144 x 144 thumbnail

Preview sheets:

- `weboverlay-variations-preview.png` — six large variants
- `weboverlay-variations-144-preview.png` — variants at their actual 144 px size
- `weboverlay-and-profile-preview.png` — variants and matching anvil profile

## Generation prompt set

Shared style prompt:

> Original flat vector-like glyph for a square game-mod icon; minimal,
> geometric and slightly industrial; strong silhouette and consistent bold
> stroke weight; centered with generous padding; pure white pictogram on a
> perfectly uniform pure black square. Icon only: no text, letters, numbers,
> brands, trademarks, watermark, gradients, shadows, glow, texture, lighting,
> bevel, 3D or mockup. Must remain readable at 144 x 144 pixels.

The six subjects were, in order: floating panel over a monitor; offset
interlocking windows; viewport brackets around a panel; geometric visor with a
browser-window pupil; diagonally split layered window; HUD frame around a
browser panel. The profile prompt used the same style with a classic anvil as
the dominant silhouette and two small viewport-corner accents.

Generated with the built-in ImageGen workflow and normalized locally to
monochrome RGB PNG files.
