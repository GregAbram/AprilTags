# Changelog

## 0.8.0 - 2026-11-25

- A new survey starts from the virtual room's reference tags in
  `room_base.json` (`SharedRoom.BasePath`; on Quest
  `/sdcard/AprilTags/room_base.json`), so the survey stays in the virtual
  room's frame and content placed there lands where it was meant to. Without
  one, the first tag seen defines the room, as before.
- Snap to walls turns the learned tags about the anchor tag and leaves the
  anchor's pose alone (it turned the whole room about the origin), so a
  reference tag stays where the base config puts it.

## 0.7.1 - 2026-11-23

- `QuestRoomControls.ForgetLearnedOnX` (default on): an app that wants X for
  itself turns it off, and `XHint` sets what the status shows for X instead
  (PVAR uses X for push-to-talk).

## 0.7.0 - 2026-11-13

- Quest rendering defaults for apps on the package (README: Performance on
  Quest): `AprilTagsAppInfo.QuestMsaa` (default off) and `QuestShadows`
  (default off) are applied to Android's quality level and URP asset on every
  Quest build; `QuestMaxResolutionScale` (default 1.0) caps Meta's dynamic
  resolution in scenes the setup creates.
- Quest controls log `[QuestRendering]` at startup, and warn about MSAA or
  dynamic resolution above 1.0.

## 0.6.0 - 2026-11-12

- Quest: one room per headset. A survey saved on a Quest is also written to
  `/sdcard/AprilTags` (`SharedRoom`), and every app on the package takes it at
  startup when it's newer than its own room, so no room code needs scanning in
  each app. Needs All files access, declared by the Quest build
  (`AprilTagsAllFilesAccess`) and granted with adb.
- Surveys are saved square to the walls (`RoomAnchor` Snap To Walls, on by
  default): the room is turned about its origin by the learned tags' average
  departure from a multiple of 90°, correcting the first tag's yaw error.
- Quest: scanning no longer makes the headset drop frames. The camera frame is
  copied to one channel on the GPU before it's read back (a quarter of the
  RGBA readback, with no CPU conversion), and `RoomAnchor` scans at most 10
  frames a second (`maxScansPerSecond`).
- Quest setup no longer applies Meta's recommended 4x MSAA with its other
  Project Setup fixes; MSAA is left to the project's URP asset. With 4x, a
  dense mesh filling the view cost 40-60 ms a frame (each eye splits into 45
  tiles, and the mesh is drawn once per tile).

## 0.5.1 - 2026-10-11

- Reference post centered on room height 0 (crossbar there): in a surveyed
  room that's the first tag's center, not the floor. Placed again when the
  room changes (it stayed at the first room's spot).
- Survey save message: one tag is enough.

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
