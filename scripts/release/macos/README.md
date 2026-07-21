# Sunder DMG presentation

The macOS release wraps Velopack's signed and notarized `Sunder.app` in a drag-to-install DMG. The Finder background is rendered at package time with native AppKit so the release does not depend on a third-party DMG builder or embed packaging artwork in the App payload.

The presentation follows the canonical Sunder Graphite Dark palette from `SunderThemeDefinition.cs`:

- background: `#121313`, `#171818`, `#1E1F1F`
- borders: `#343535`, `#474949`
- primary and muted text: `#D8D5CE`, `#AAA69F`
- amber accent and highlight: `#D09132`, `#E7B765`

The Finder window is 820 by 460 logical points. `Sunder.app` is positioned on the left, the `/Applications` symlink is positioned on the right, and the background supplies the installation instruction and amber drag arrow. The renderer emits 1x and 2x PNGs which are combined into a HiDPI TIFF inside the hidden `.background` directory.
