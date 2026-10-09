using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// What an app built on this package is: its name, IDs and scenes. A project
// declares one (see the Locator and Survey projects' Assets/Editor) and passes
// it to AprilTagsIOSSetup / AprilTagsQuestSetup, which configure the project,
// create missing scenes and build. Scene paths are the project's; the first of
// each list is the scene the app starts in.
[Serializable]
public class AprilTagsAppInfo
{
    public string ProductName = "AprilTags App";
    public string CompanyName = "TACC";
    public string BundleId = "edu.utexas.tacc.apriltagsapp";
    public string AppleTeamId = "";
    public string LogFileName = "session.log";
    public string[] IPhoneScenes = Array.Empty<string>();
    public string[] QuestScenes = Array.Empty<string>();
    public string IOSBuildPath = "Builds/iOS";
    public string ApkPath => $"Builds/Quest/{ProductName.Replace(" ", "")}.apk";
}

// The kinds of scene the setups can create.
public enum AprilTagsSceneKind
{
    // RoomAnchor + RoomCodeReader: scans a room code when it has no room,
    // remembers it, anchors; reference post, tag markers and a Content object.
    Locator,
    // RoomAnchor in survey mode + RoomSurvey: name, tag size, save + room code.
    Survey,
}

// What a created scene contains, for a project to add its own objects before
// the scene is saved (e.g. content under Content).
public class AprilTagsSceneParts
{
    public AprilTagRoomLocalizer Localizer;
    public RoomAnchor Anchor;
    public RoomCodeReader RoomCodeReader;   // Locator only
    public RoomSurvey Survey;               // Survey only
    // Locator only: an empty child of the room for the app's content (hidden
    // until the room is anchored, in room coordinates).
    public Transform Content;
}

// Scene pieces shared by the iOS and Quest setups.
public static class AprilTagsSceneBuilder
{
    public const string MaterialsFolder = "Assets/AprilTags/Materials";

    // The room: RoomAnchor (survey mode for a survey), a white post on the
    // origin, markers on placed tags, and for a locator the magenta reference
    // post, a RoomCodeReader and the Content object. Also the app startup
    // component (QR decoder, session log) on the localizer's object.
    public static AprilTagsSceneParts AddRoom(AprilTagRoomLocalizer localizer, AprilTagsSceneKind kind, AprilTagsAppInfo app)
    {
        var parts = new AprilTagsSceneParts { Localizer = localizer };
        var startup = localizer.gameObject.AddComponent<AprilTagsAppStartup>();
        SetString(startup, "logFileName", app.LogFileName);

        var room = new GameObject("Room");
        var anchor = room.AddComponent<RoomAnchor>();
        SetField(anchor, "localizer", localizer);
        parts.Anchor = anchor;
        if (kind == AprilTagsSceneKind.Survey)
        {
            var so = new SerializedObject(anchor);
            so.FindProperty("surveyMode").boolValue = true;
            // Its own learned file, so a survey never mixes with a locator's learning.
            so.FindProperty("learnedFileName").stringValue = "survey_tags.json";
            so.ApplyModifiedPropertiesWithoutUndo();
            var survey = room.AddComponent<RoomSurvey>();
            SetField(survey, "roomAnchor", anchor);
            SetField(survey, "localizer", localizer);
            parts.Survey = survey;
        }
        else
        {
            var reader = localizer.gameObject.AddComponent<RoomCodeReader>();
            SetField(reader, "localizer", localizer);
            parts.RoomCodeReader = reader;
        }

        Post("Origin", Material("Origin", Color.white)).transform.SetParent(room.transform, false);

        // Markers on placed tags; the template stays outside the room, whose
        // children RoomAnchor hides and shows.
        var template = GameObject.CreatePrimitive(PrimitiveType.Cube);
        template.name = "Tag Marker Template";
        template.transform.localScale = Vector3.one * 0.04f;
        UnityEngine.Object.DestroyImmediate(template.GetComponent<Collider>());
        template.GetComponent<Renderer>().sharedMaterial = Material("TagMarker", new Color(0.95f, 0.3f, 0.25f));
        var markers = room.AddComponent<RoomTagMarkers>();
        SetField(markers, "markerTemplate", template.GetComponent<Renderer>());

        if (kind == AprilTagsSceneKind.Locator)
        {
            AddReferenceMarker(anchor, Material("ReferenceMarker", new Color(1f, 0.2f, 0.9f)));
            var content = new GameObject("Content");
            content.transform.SetParent(room.transform, false);
            parts.Content = content.transform;
        }
        return parts;
    }

    // A magenta post on the floor below the center of the measured tags
    // (RoomReferenceMarker), 1.6 m tall with a crossbar at 1 m: every device
    // should see it in the same place.
    public static void AddReferenceMarker(RoomAnchor anchor, Material material)
    {
        var marker = new GameObject("Reference Marker");
        marker.transform.SetParent(anchor.transform, false);
        Bar(marker.transform, new Vector3(0f, 0.8f, 0f), new Vector3(0.03f, 1.6f, 0.03f), material);
        Bar(marker.transform, new Vector3(0f, 1.0f, 0f), new Vector3(0.3f, 0.03f, 0.03f), material);
        Bar(marker.transform, new Vector3(0f, 1.0f, 0f), new Vector3(0.03f, 0.03f, 0.3f), material);
        var component = marker.AddComponent<RoomReferenceMarker>();
        SetField(component, "roomAnchor", anchor);
    }

    // A thin post standing on a point: visible even when a marker sits there.
    public static GameObject Post(string name, Material material)
    {
        var marker = new GameObject(name);
        Bar(marker.transform, new Vector3(0f, 0.3f, 0f), new Vector3(0.02f, 0.6f, 0.02f), material);
        return marker;
    }

    // An unlit URP material in Assets/AprilTags/Materials, created if missing.
    public static Material Material(string name, Color color)
    {
        var path = $"{MaterialsFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null)
        {
            return material;
        }
        Directory.CreateDirectory(MaterialsFolder);
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        material = new Material(shader) { color = color };
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    public static void SetField(UnityEngine.Object target, string field, UnityEngine.Object value)
    {
        var so = new SerializedObject(target);
        so.FindProperty(field).objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    public static void SetString(UnityEngine.Object target, string field, string value)
    {
        var so = new SerializedObject(target);
        so.FindProperty(field).stringValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // The scene kind comes from the file name: "Survey" in it makes a survey.
    public static AprilTagsSceneKind KindOf(string path) =>
        Path.GetFileNameWithoutExtension(path).Contains("Survey") ? AprilTagsSceneKind.Survey : AprilTagsSceneKind.Locator;

    // Path of the scene that follows this one in the list (wrapping), as a scene
    // name for SceneManager.LoadScene; empty for a single scene.
    public static string NextSceneName(string[] scenes, string path)
    {
        if (scenes.Length < 2)
        {
            return "";
        }
        var next = scenes[(Array.IndexOf(scenes, path) + 1) % scenes.Length];
        return Path.GetFileNameWithoutExtension(next);
    }

    private static void Bar(Transform parent, Vector3 position, Vector3 scale, Material material)
    {
        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "Bar";
        UnityEngine.Object.DestroyImmediate(bar.GetComponent<Collider>());
        bar.transform.SetParent(parent, false);
        bar.transform.localPosition = position;
        bar.transform.localScale = scale;
        bar.GetComponent<Renderer>().sharedMaterial = material;
    }
}
