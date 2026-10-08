# Background scanning: measurements and design

Goal: no Acquire button. The device keeps looking for tags at a low rate,
places the room as soon as it sees one configured tag, and keeps refining
(and correcting drift and tracking resets) as it sees more.

## Measurements (Quest 3, 2026-10-08)

Per-frame cost while scanning, from `AprilTagRoomLocalizer`'s timing log
(passthrough camera 1280×1280, `quadDecimate` 2, tagStandard41h12):

| Per frame | Mean | Max |
|---|---|---|
| prepare: GPU readback copy + grayscale (main thread) | 2.3 ms | 3.0 ms |
| detect (main thread) | 25–29 ms | 38 ms |
| process: pose for known tags | ~1 ms with a tag, ~0.1 ms without | 1.7 ms |
| latency, request → delivery (async readback) | 36 ms | 44 ms |

Detection costs the same with or without a tag in view (the quad search over
the whole image dominates). At 72–90 Hz the frame budget is 11–14 ms, so one
detection on the main thread costs 2–3 display frames: even a 1 Hz scan on the
main thread would judder. **Detection must run off the main thread.**

Per-frame spread within one 1 s acquisition window (16–18 frames), from the
localizer's spread log:

| Tag, distance | Tag position sd / max | Single-tag yaw sd / max | Room origin sd / max |
|---|---|---|---|
| 8, 0.8 m | 0.05 / 0.14 cm | 0.29° / 0.62° | 0.5 / 1.1 cm |
| 8, 2.4 m | 0.9 / 2.1 cm | 3.70° / 6.97° | 6.7 / 12.9 cm |
| 8, 2.4 m | 0.9 / 2.1 cm | 1.13° / 2.04° | 1.7 / 3.0 cm |
| 7, 2.1 m | 0.6 / 1.3 cm | 3.37° / 8.58° | 13.4 / 33.7 cm |
| 9, 2.6 m | 1.0 / 2.3 cm | 2.44° / 4.34° | 21.4 / 37.4 cm |

- A single frame **locates** a tag to ~1 cm (2 cm worst) even at 2.5 m.
- A single frame's **orientation** is poor beyond ~1 m (1–4° sd, up to 9°).
- Room-origin error from one tag is its yaw error × the tag's distance from
  the origin (1.8–5 m here): up to 37 cm per frame, and the 1 s *averages*
  still disagreed by ~3° (10–15 cm across the room) between tags.
- RoomFit over the three tags (positions only, yaw from the tags' relative
  placement) gave RMS 3.6 cm, at the level of the tape-measured config.

## Design

1. **Detection on a worker thread.** The main thread only requests frames and
   applies results; detection and per-tag pose estimation run on a thread-pool
   task. One frame in flight at a time; the source's image is not rewritten
   until the next request, so no copy is needed.
2. **Continuous scan at a low rate** (`AprilTagRoomLocalizer.ContinuousScan`,
   `ScanIntervalSeconds`). Every frame's known-tag detections are reported as
   single-frame observations (`TagObserved`). The manual Acquire window
   (`BeginAcquisition` / `EstimateAcquired`) still works unchanged.
3. **`RoomAnchor` builds the room from observations:**
   - per tag, a short history of world positions (recent ones only, so drift
     ages out), averaged;
   - **two or more tags: RoomFit on the averaged positions**, ignoring tag
     orientations - the accurate mode, and the one that makes devices agree;
   - **one tag: provisional** placement from that tag's own yaw, weighted
     toward close-range observations, until a second tag is seen;
   - **outliers and resets:** a single observation far from its tag's average
     is dropped; several in a row mean the tracking frame moved (recenter,
     relocalization) and the history is restarted;
   - **rate control:** scan every frame briefly while a visible tag still
     needs samples, then at the normal interval, and slower once the fit is
     stable;
   - the displayed transform eases toward the new pose instead of jumping
     (snaps on first placement and after a reset).

## Open questions to measure next

- Coarser decimation for idle scans: 4 would cut detection ~4× but may lose
  tags at 2–3 m (a 9.4 cm tag is only ~20 px there).
- iPhone 11 timings (not measured yet).
- Drift rate on Quest and iPhone over minutes, to set the history age.
