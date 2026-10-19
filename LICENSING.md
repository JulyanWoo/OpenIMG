# OpenIMG licensing

## Project code

OpenIMG-authored source code and documentation in this repository are licensed
under the MIT License. See [`LICENSE`](LICENSE).

This grant applies only to material the project owner has the right to license.
It does not relicense third-party packages, models, icons, photographs, or other
assets. See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

## Models and artwork

- The Ultralytics YOLO model is excluded from this public snapshot and from the
  intended public installer. Do not add it back to a release unless its
  licensing route is separately approved and the distribution obligations are
  met.
- The local OpenIMG icon is not included because its creator and permission
  chain have not been documented. Do not bundle or relicense it until that
  provenance is confirmed.
- User photographs and calibration outputs are not covered by the project
  license and must not be included without the rights holder's authorization.

## Dependencies and release packages

The dependency inventory and current package license identifiers are in
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). Before publishing a binary,
verify the exact publish output and include the full license and notice files
required by every component actually shipped, including the .NET runtime and
WebView2-related components. This source-level inventory is not a substitute
for that binary-specific check.

This file is an informational summary; the full MIT terms are in `LICENSE`.
