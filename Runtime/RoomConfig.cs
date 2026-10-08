using System;

[Serializable]
public class RoomConfig
{
    public RoomDimensions room;
    public TagPlacement[] tags;

    // Also detect tags that aren't listed (any ID of the family), at
    // defaultTagSizeMeters; RoomAnchor learns their positions relative to the
    // measured tags. A tag printed at another size must be listed with its size.
    public bool learnUnlistedTags;
    public float defaultTagSizeMeters;
}

[Serializable]
public class RoomDimensions
{
    public float width;
    public float depth;
    public float height;
}

[Serializable]
public class TagPlacement
{
    public int id;
    public float x;
    public float y;
    public float z;
    public float yawDegrees;
    public float sizeMeters;

    // Carefully surveyed: its position and yaw define the room frame. Other
    // tags' positions are learned relative to the measured ones at runtime
    // (RoomAnchor), so theirs only need to be rough. If no tag is marked
    // measured, all are treated as measured.
    public bool measured;
}
