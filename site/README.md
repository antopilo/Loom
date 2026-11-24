# Loom website

A static showcase page: `index.html`, `site.css`, `site.js`. No build step.

Preview it locally:

```powershell
python -m http.server 8731 -d site
```

Then open <http://localhost:8731/>.

## The film

Section 01 plays `media/loom-trailer.mp4`, with `media/loom-trailer.jpg` as its poster. Until the file exists,
the frame shows "The film is on the loom". `assets/trailer/render.mjs` writes the film to
`artifacts/trailer/loom-trailer.mp4`; copy it here when it is done.

## Notes

- The app mock-ups are HTML in the colors of `Core/Theme.cs`. The theme picker in section 09 rewrites the
  `--c-*` variables, so every mock-up follows it.
- The backdrop (`#weave`) is drawn in `site.js` at half resolution. It stops when the tab is hidden and draws one
  still frame when the system asks for reduced motion.
- Keys 1–9 jump to the sections.
