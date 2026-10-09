using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Meta.XR;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.OpenXR;

// Quest 3 setup for an app on this package: Meta's own stack (OVRCameraRig,
// Meta XR OpenXR feature, MRUK passthrough camera access). The scenes use
// OVRCameraRig rather than an XR Origin because MRUK's PassthroughCameraAccess
// reports camera poses in Meta's tracking space, which is Unity world space
// under OVRCameraRig. Re-runnable; scenes are created only if missing, never
// modified. Needs the Android build target, and the project's
// Assets/Plugins/Android/AndroidManifest.xml must carry
// horizonos.permission.HEADSET_CAMERA (Unity uses it with "Custom Main
// Manifest", which Configure ticks).
public static class AprilTagsQuestSetup
{
    private const string CameraRigPrefabPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";

    private static readonly string[] OpenXRFeatures =
    {
        "MetaXRFeature",
        "MetaXRSubsampledLayout",
        "OculusTouchControllerProfile",
        "OculusTouchControllerProximityProfile",
        "MetaQuestTouchPlusControllerProfile",
    };

    public static void Configure(AprilTagsAppInfo app, Action<string, AprilTagsSceneParts> addContent = null)
    {
        ConfigurePlayer(app);
        ConfigureXR();
        ApplyMetaProjectSetupFixes();
        foreach (var path in app.QuestScenes)
        {
            CreateSceneIfMissing(app, path, addContent);
        }
        // Build And Run in the editor uses this list; the iOS build passes its own.
        EditorBuildSettings.scenes = Array.ConvertAll(app.QuestScenes, path => new EditorBuildSettingsScene(path, true));
        AssetDatabase.SaveAssets();
        Debug.Log($"[AprilTagsQuestSetup] {app.ProductName}: Quest configured");
    }

    // Writes the APK (AprilTagsAppInfo.ApkPath) from the Quest scenes; exits with
    // an error code in batchmode when the build fails.
    public static void Build(AprilTagsAppInfo app)
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = app.QuestScenes,
            locationPathName = app.ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.Development,
        });
        Debug.Log($"[AprilTagsQuestSetup] {app.ProductName}: Quest build {report.summary.result}: {report.summary.totalErrors} error(s)");
        if (Application.isBatchMode && report.summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }

    private static void ConfigurePlayer(AprilTagsAppInfo app)
    {
        var android = NamedBuildTarget.Android;
        PlayerSettings.companyName = app.CompanyName;
        PlayerSettings.productName = app.ProductName;
        PlayerSettings.SetApplicationIdentifier(android, app.BundleId);
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)34;
        PlayerSettings.Android.forceInternetPermission = true;   // e.g. a PVLink DataManager
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });

        // "Custom Main Manifest" has no public scripting API.
        var playerSettings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
        playerSettings.FindProperty("useCustomMainManifest").boolValue = true;
        playerSettings.ApplyModifiedPropertiesWithoutUndo();

        var config = OVRProjectConfig.CachedProjectConfig;
        config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
        // The system keyboard, for typing a survey's room name and tag size.
        config.requiresSystemKeyboard = true;
        OVRProjectConfig.CommitProjectConfig(config);
    }

    private static void ConfigureXR()
    {
        var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>("Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
        var manager = perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        perTarget.SettingsForBuildTarget(BuildTargetGroup.Android).InitManagerOnStart = true;
        if (!XRPackageMetadataStore.IsLoaderAssigned("UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android))
        {
            XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android);
        }
        EditorUtility.SetDirty(perTarget);

        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        foreach (var feature in settings.GetFeatures())
        {
            if (OpenXRFeatures.Contains(feature.GetType().Name))
            {
                feature.enabled = true;
            }
        }
        EditorUtility.SetDirty(settings);
        var enabled = settings.GetFeatures().Where(f => f.enabled).Select(f => f.GetType().Name).OrderBy(n => n).ToList();
        var missing = OpenXRFeatures.Except(enabled).ToList();
        Debug.Log($"[AprilTagsQuestSetup] OpenXR Android features enabled: {string.Join(", ", enabled)}" +
                  (missing.Count > 0 ? $"; NOT FOUND: {string.Join(", ", missing)}" : ""));
    }

    // Meta's Project Setup Tool fixes (its "Fix All"), Required + Recommended
    // only; Optional ones turn on features these apps don't use. The fixer API
    // is internal, hence reflection. Android only.
    public static void ApplyMetaProjectSetupFixes()
    {
        var setupType = typeof(OVRProjectSetup);
        var fixTasks = setupType.GetMethod("FixTasks", BindingFlags.Static | BindingFlags.NonPublic);
        var logMessagesType = setupType.GetNestedType("LogMessages", BindingFlags.NonPublic);
        var taskType = fixTasks?.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments()[0];
        if (fixTasks == null || logMessagesType == null || taskType == null)
        {
            Debug.LogError("[AprilTagsQuestSetup] Meta setup tool API changed; run Meta > Tools > Project Setup Tool > Fix All manually.");
            return;
        }

        var target = BuildTargetGroup.Android;
        var filter = typeof(AprilTagsQuestSetup)
            .GetMethod(nameof(MakeFilter), BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(taskType)
            .Invoke(null, new object[] { (Func<object, bool>)(task => GetTaskLevel(task, target) >= 1) });

        // (target, filter, log level, blocking, onCompleted); later SDKs add
        // optional parameters, which get their defaults.
        var parameters = fixTasks.GetParameters();
        var args = new object[parameters.Length];
        object[] known = { target, filter, Enum.ToObject(logMessagesType, 3), true, null };
        for (var i = 0; i < args.Length; i++)
        {
            args[i] = i < known.Length ? known[i] : parameters[i].DefaultValue;
        }
        for (var pass = 0; pass < 3; pass++)
        {
            fixTasks.Invoke(null, args);
        }
        Debug.Log("[AprilTagsQuestSetup] Applied Meta Project Setup Tool fixes (Required + Recommended)");
    }

    private static Func<IEnumerable<T>, List<T>> MakeFilter<T>(Func<object, bool> keep) =>
        tasks => tasks.Where(task => keep(task)).ToList();

    private static int GetTaskLevel(object task, BuildTargetGroup target)
    {
        var level = task.GetType().GetProperty("Level")?.GetValue(task);
        var value = level?.GetType().GetMethod("GetValue", new[] { typeof(BuildTargetGroup) })?.Invoke(level, new object[] { target });
        return value == null ? -1 : Convert.ToInt32(value);
    }

    // A starting point only; once it exists the scene is edited by hand. The
    // camera rig with passthrough, the camera source and localizer, the room
    // (AprilTagsSceneBuilder.AddRoom) and the controller controls with a status
    // label that follows the head. "Survey" in the file name makes a survey.
    private static void CreateSceneIfMissing(AprilTagsAppInfo app, string path, Action<string, AprilTagsSceneParts> addContent)
    {
        if (File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPrefabPath), scene);
        var ovrManager = rig.GetComponent<OVRManager>();
        ovrManager.isInsightPassthroughEnabled = true;
        var managerSo = new SerializedObject(ovrManager);
        managerSo.FindProperty("requestPassthroughCameraAccessPermissionOnStartup").boolValue = true;
        managerSo.ApplyModifiedPropertiesWithoutUndo();
        rig.AddComponent<OVRPassthroughLayer>();   // always an underlay as of SDK 207
        var centerEye = rig.transform.Find("TrackingSpace/CenterEyeAnchor");
        var eyeCamera = centerEye.GetComponent<Camera>();
        eyeCamera.clearFlags = CameraClearFlags.SolidColor;
        eyeCamera.backgroundColor = Color.clear;

        var cameraAccess = new GameObject("PassthroughCameraAccess").AddComponent<PassthroughCameraAccess>();

        var status = new GameObject("Status Text").AddComponent<TextMeshPro>();
        status.fontSize = 0.4f;
        status.alignment = TextAlignmentOptions.TopLeft;
        status.rectTransform.sizeDelta = new Vector2(1.2f, 0.6f);
        status.color = Color.white;
        status.outlineWidth = 0.2f;
        status.outlineColor = Color.black;

        var tags = new GameObject("AprilTags");
        var source = tags.AddComponent<PassthroughCameraSource>();
        AprilTagsSceneBuilder.SetField(source, "cameraAccess", cameraAccess);
        var localizer = tags.AddComponent<AprilTagRoomLocalizer>();
        AprilTagsSceneBuilder.SetField(localizer, "cameraSource", source);

        var kind = AprilTagsSceneBuilder.KindOf(path);
        var parts = AprilTagsSceneBuilder.AddRoom(localizer, kind, app);
        var next = AprilTagsSceneBuilder.NextSceneName(app.QuestScenes, path);
        if (kind == AprilTagsSceneKind.Survey)
        {
            var controls = tags.AddComponent<QuestSurveyControls>();
            AprilTagsSceneBuilder.SetField(controls, "roomSurvey", parts.Survey);
            AprilTagsSceneBuilder.SetField(controls, "statusText", status);
            AprilTagsSceneBuilder.SetField(controls, "head", centerEye);
            AprilTagsSceneBuilder.SetString(controls, "otherSceneName", next);
            AprilTagsSceneBuilder.SetString(controls, "title", $"{app.ProductName} SURVEY");
        }
        else
        {
            var controls = tags.AddComponent<QuestRoomControls>();
            AprilTagsSceneBuilder.SetField(controls, "roomAnchor", parts.Anchor);
            AprilTagsSceneBuilder.SetField(controls, "roomCodeReader", parts.RoomCodeReader);
            AprilTagsSceneBuilder.SetField(controls, "statusText", status);
            AprilTagsSceneBuilder.SetField(controls, "head", centerEye);
            AprilTagsSceneBuilder.SetString(controls, "otherSceneName", next);
            AprilTagsSceneBuilder.SetString(controls, "title", app.ProductName);
        }
        addContent?.Invoke(path, parts);

        EditorSceneManager.SaveScene(scene, path);
        Debug.Log($"[AprilTagsQuestSetup] Created {path}");
    }
}
