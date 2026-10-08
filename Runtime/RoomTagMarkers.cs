using System.Collections.Generic;
using UnityEngine;

// Debug view for RoomAnchor: a small marker at the center of every placed tag,
// as a child of the anchor - red for measured tags (surveyed position), green
// for learned ones (position learned this or an earlier session). If the room
// is right, each marker sits on its real tag. Markers are created as tags
// become placed, from markerTemplate (left inactive in the scene).
[RequireComponent(typeof(RoomAnchor))]
public class RoomTagMarkers : MonoBehaviour
{
    [SerializeField] private Renderer markerTemplate;
    [SerializeField] private Color measuredColor = new(0.95f, 0.25f, 0.2f);
    [SerializeField] private Color learnedColor = new(0.25f, 0.85f, 0.35f);

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private RoomAnchor anchor;
    private readonly Dictionary<int, Renderer> markers = new();
    private MaterialPropertyBlock block;

    private void Awake()
    {
        anchor = GetComponent<RoomAnchor>();
        block = new MaterialPropertyBlock();
        if (markerTemplate != null)
        {
            markerTemplate.gameObject.SetActive(false);
        }
    }

    private void LateUpdate()
    {
        if (markerTemplate == null || !anchor.IsLocalized)
        {
            return;
        }

        foreach (var id in anchor.PlacedTagIds)
        {
            anchor.TryGetTagRoomPosition(id, out var roomPosition, out var measured);
            if (!markers.TryGetValue(id, out var marker))
            {
                marker = Instantiate(markerTemplate, transform);
                marker.name = $"Tag {id} marker";
                markers[id] = marker;
            }
            marker.gameObject.SetActive(true);
            marker.transform.localPosition = roomPosition;
            marker.transform.localRotation = Quaternion.identity;
            var color = measured ? measuredColor : learnedColor;
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            marker.SetPropertyBlock(block);
        }

        // Unlearned tags lose their marker.
        foreach (var (id, marker) in markers)
        {
            if (!anchor.TryGetTagRoomPosition(id, out _, out _))
            {
                marker.gameObject.SetActive(false);
            }
        }
    }
}
