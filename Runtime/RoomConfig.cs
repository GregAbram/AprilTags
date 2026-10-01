using System;

[Serializable]
public class RoomConfig
{
    public RoomDimensions room;
    public TagPlacement[] tags;
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
}
