# Sunder DMG presentation

The macOS release wraps Velopack's signed and notarized `Sunder.app` in a drag-to-install DMG. The Finder background is rendered at package time with native AppKit so the release does not depend on a third-party DMG builder or embed packaging artwork in the App payload.

The presentation follows the canonical Sunder Graphite Dark palette from `SunderThemeDefinition.cs`:

- background: `#121313`, `#171818`, `#1E1F1F`
- borders: `#343535`, `#474949`
- primary and muted text: `#D8D5CE`, `#AAA69F`
- amber accent and highlight: `#D09132`, `#E7B765`

The Finder window is 820 by 460 logical points. `Sunder.app` is positioned on the left, the `/Applications` symlink is positioned on the right, and the background supplies the installation instruction and amber drag arrow. The renderer emits 1x and 2x PNGs which are combined into a HiDPI TIFF inside the hidden `.background` directory.

## Attachment lifecycle

`create-sunder-dmg.sh` requires macOS, Swift/AppKit, Python 3 and the native disk-image
tools. `dmg_image.py` also owns the read-only inspection attachments in CI/release.
It reads native plists, checks the backing image and device identity before each
detach, and confirms that detachment completed. `hdiutil` output goes to regular
files, not pipes; errors and bounded open-handle diagnostics remain visible.

Busy detaches (exit 16) get five normal attempts with 2/4/8/16-second waits. Other
errors fail immediately. Conversion requires normal detachment: forced detach is
only for discarding a failed build/inspection attachment. Container checksum
verification is not a substitute for filesystem integrity after forced detach.
Cleanup preserves the original failure status and retains the staging directory
when detachment cannot be confirmed, including partial attach failures.

The release-script fixtures cover retry, ownership and cleanup behavior without
mounting disks. The native unsigned smoke still builds and inspects a real image,
including the app, Applications link, saved Finder layout and both TIFF resolutions.
It must also detach successfully; a runner OS upgrade does not replace this gate.
