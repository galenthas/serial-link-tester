# Changelog

All notable changes to this project are documented here.

## [1.3.2] - 2026-11-19

- Remove demo mode from the application. Simulated ports are used only in automated tests.
- Rename Serial Link Tester to Serial Port Tester. The portable executable is now `SerialPortTester.exe`.
- Remove the top banner and numbered headings to give the test settings and measurements more room.
- Size metric cards to their text and fit the number of columns to the available width.
- Scroll the results pane on short windows to reach the remaining cards, chart and report.

## [1.3.1] - 2026-11-19

### Fixed

- Keep the Details report's scroll position and text selection during live updates,
  direction changes and test completion. Starting a new test still resets the view.
- Update the complete report once per refresh without briefly displaying partial content.

### Added

- Seven Windows UI regression tests covering scrolling, selection, hidden tabs,
  growing/shorter reports, direction changes and a timed bidirectional demo.
- UI test results in the package manifest and CI summary; packaging requires both
  the existing 30 core tests and the new UI tests to pass.

## [1.3.0] - 2026-11-17

### Added

- First public release of the portable Windows serial link test tool.
- One-way, bidirectional and round-trip loopback configurations.
- Independent per-direction loss, corruption, goodput, latency and jitter metrics.
- Live charts, simulated demo and HTML/CSV/JSON reports.
- Named amber serial warnings separated from red test-stopping errors.
- Default bidirectional setup and a 1000 ms final receive window.
- Multi-resolution application icon and dark English interface.
- 30 automated tests, Windows CI and tag-triggered release publication.
- Version metadata in `version.json` and SHA-256 package manifests.

Earlier local 1.0–1.3 development packages are not separate public releases.
