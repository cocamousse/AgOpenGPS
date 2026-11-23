# Tablet scripts

Helpers for the AgOpen dev tablet. They live in `Desktop\Dev` on the tablet,
or run from here after `check-and-build.bat` has pulled this folder.

- **check-and-build.bat** (kept in `Desktop\Dev`, not in this folder): checks the
  tools, gets or updates the normal test version (branch `agopen-v1`) in
  `Dev\AgOpenGPS` and builds it. Results: `check-log.txt`, `build-log.txt`.
- **build-labels-version.bat**: gets or updates the labels version (branch
  `agopen-labels`: everything in `agopen-v1` plus labels for the buttons) in
  `Dev\AgOpenGPS-labels`, builds it, and puts an "AgOpenGPS (labels)" shortcut on
  the desktop. The normal test version isn't touched. Results:
  `labels-check-log.txt`, `labels-build-log.txt`.

Both versions use the same fields and settings (`Documents\AgOpenGPS`), and only
one can run at a time. Close AgOpenGPS before building the version you're
rebuilding.
