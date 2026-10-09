# Changelog

## 0.3.0 - 2026-10-09

Background scanning, anchoring by fitting tag positions, and surveying a room
once for every device. See README and Documentation~/BackgroundScan.md.

- `RoomAnchor`: content-carrying room frame. Anchored by `RoomFit` on two or
  more known tags (one close tag only when the config has fewer than two
  measured tags, or in a survey); learns unmeasured/unlisted tags (position
  and yaw, `learned_tags.json`); handles drift and tracking resets; adapts the
  scan rate; hides its children until anchored; `StatusText`.
- Survey mode: anchor on the first configured tag seen (or, with none, the
  first tag defines the room), learn every other tag, `SaveSurveyedConfig()`
  writes a config listing all as measured.
- `AprilTagRoomLocalizer`: detection on a worker thread; continuous scanning
  and `TagObserved`; drops frames taken while turning > 8 deg/s; per-frame
  intrinsics when the source provides them; timing, spread and viewpoint logs.
- Config: `measured`, `learnUnlistedTags`, `defaultTagSizeMeters`; a config
  with no tags is valid when learning unlisted tags.
- `ARFoundationCameraSource`: per-frame intrinsics, autofocus off.
- New: `RoomScreenUI`, `SurveyScreenUI`, `RoomTagMarkers`,
  `RoomReferenceMarker`, opt-in `SessionLogFile`.

## 0.2.0 - 2026-10-06

Camera-source abstraction (Quest and AR Foundation/iPhone sources in separate
assemblies), `RoomFit`, `RoomAnchor` (first version).
