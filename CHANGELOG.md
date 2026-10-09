# Changelog

## 0.5.0 - 2026-10-09

App building blocks, so a Survey app, the Locator template and apps built on
it share one setup instead of copies.

- `RoomSurvey`: a survey session - room name, printed tag size, optional
  DataManager address (kept between runs), new survey from nothing (the first
  tag seen defines the room), save with room code.
- `SurveyScreenUI`: optional `RoomSurvey` adds name and tag size fields.
- Quest: `QuestRoomControls` and `QuestSurveyControls` (system keyboard for
  the name and size), `QuestStatusPanel`.
- `AprilTagsAppStartup`: installs the QR decoder and starts the session log
  from a scene object (reliable on device).
- Editor: `AprilTagsAppInfo`, `AprilTagsSceneBuilder`, `AprilTagsIOSSetup`
  (ARKit) and `AprilTagsQuestSetup` (Meta) - project settings, starting
  scenes and builds; iOS file sharing for the app's Documents folder.
- `RoomCodeReader` applies a code that changes only the name or DataManager.

## 0.4.0 - 2026-10-09

Room codes: a room's surveyed config as a QR code that devices scan, instead
of a config built into each app.

- `RoomCode`: compact text form of a config (name, DataManager address, tags)
  and `SurveyId`. `RoomConfig` gains `name` and `dataManager`.
- `RoomCodeReader`: scans when the device has no room or on `BeginScan()`,
  applies and remembers a new room (persistentDataPath).
- `AprilTagRoomLocalizer`: `ApplyConfig()` at runtime and `ConfigChanged`;
  starts without a config; room-code decoding on the worker thread
  (`ScanForRoomCode`, `RoomCodeFound`). `RoomAnchor` starts over on a new room.
- `TaccAprilTags.RoomCode` assembly with ZXing.Net 0.16.11 (Apache 2.0):
  decoder and QR image encoder; `RoomCodeImage.Save` writes room_code.txt/.png.
- Surveys save the room code too; `SurveyScreenUI` shows it on screen;
  `RoomScreenUI` shows the room and survey ID and a Scan room code button.
- Editor: Tools > AprilTags > Save Room Code Image.

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
