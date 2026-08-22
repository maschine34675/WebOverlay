# Shared look for overlay pages

Every page built on this library so far - the demo panel, the HUD, the glass
panel, the cube, and the mods using it - declared the same handful of colours
by copy-paste. This is that palette written down once, plus the recipe the
panels use. Nothing here is required: a mod with its own look should have it.

## Tokens

Set `OverlayOptions.InjectTheme = true` and the library defines these on
`:root` before the page's own scripts run, so a stylesheet can use them
directly:

| Variable | Value | What it is |
|---|---|---|
| `--wo-gold` | `#c2ad6d` | Headings, borders, anything that should read as a control |
| `--wo-ink` | `rgba(16,17,13,0.74)` | Panel background - translucent, so glass HUDs blend |
| `--wo-text` | `#d0cdbd` | Body text |
| `--wo-dim` | `#918e7e` | Labels, secondary text |
| `--wo-accent` | `#72ba80` | Live values, confirmations |
| `--wo-border` | `rgba(194,173,109,0.35)` | Panel outline |
| `--wo-radius` | `8px` | Corner radius |
| `--wo-font` | `'Segoe UI',system-ui,sans-serif` | The game's interface is close enough to this |

Always give them a fallback, so the same page still looks right when a mod
opens it without the theme, or a browser opens it outside the game:

```css
background: var(--wo-ink, rgba(16,17,13,0.74));
```

## The panel recipe

```css
.panel {
  background: var(--wo-ink, rgba(16,17,13,0.74));
  border: 1px solid var(--wo-border, rgba(194,173,109,0.35));
  border-radius: var(--wo-radius, 8px);
  box-shadow: 0 4px 18px rgba(0,0,0,0.45);
  color: var(--wo-text, #d0cdbd);
  font-family: var(--wo-font, 'Segoe UI', system-ui, sans-serif);
  padding: 10px 16px;
}
.panel h1 { color: var(--wo-gold, #c2ad6d); font-size: 1rem; letter-spacing: .1em; }
.panel .label { color: var(--wo-dim, #918e7e); font-size: .7rem;
                text-transform: uppercase; letter-spacing: .1em; }
.panel .value { color: var(--wo-accent, #72ba80); font-variant-numeric: tabular-nums; }
```

## Adapting to the transparency you got

The library also puts one of `wo-composed`, `wo-chroma` or `wo-opaque` on the
root element. That matters: with the chroma-key fallback, semi-transparent
pixels blend towards near-black instead of towards the game, so translucent
panels and soft shadows look wrong there and solid ones do not.

```css
.wo-chroma .panel { background: #1b1c18; box-shadow: none; }
```

A page that never runs on old systems can ignore this; a page that wants to be
safe everywhere writes those three lines.
