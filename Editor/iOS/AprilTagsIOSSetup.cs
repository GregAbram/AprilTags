using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARKit;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;

// iPhone setup for an app on this package (AR Foundation + ARKit): player and
// XR settings, the app's iPhone scenes (created only if missing, never
// modified), and the Xcode project build. Re-runnable. Called from a project's
// own menu items with its AprilTagsAppInfo.
public static class AprilTagsIOSSetup
{
    public static void Configure(AprilTagsAppInfo app, Action<string, AprilTagsSceneParts> addContent = null)
    {
        ConfigurePlayer(app);
        ConfigureXR();
        foreach (var path in app.IPhoneScenes)
        {
            CreateSceneIfMissing(app, path, addContent);
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[AprilTagsIOSSetup] {app.ProductName}: iOS configured");
    }

    // Generates the Xcode project from the iPhone scenes; exits with an error
    // code in batchmode when the build fails.
    public static void Build(AprilTagsAppInfo app)
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = app.IPhoneScenes,
            locationPathName = app.IOSBuildPath,
            target = BuildTarget.iOS,
            options = BuildOptions.Development,
        });
        Debug.Log($"[AprilTagsIOSSetup] {app.ProductName}: iOS build {report.summary.result}: {report.summary.totalErrors} error(s)");
        if (Application.isBatchMode && report.summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }

    private static void ConfigurePlayer(AprilTagsAppInfo app)
    {
        PlayerSettings.companyName = app.CompanyName;
        PlayerSettings.productName = app.ProductName;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, app.BundleId);
        PlayerSettings.iOS.cameraUsageDescription = "The camera is used to find AprilTags, read room codes and track the room.";
        PlayerSettings.iOS.targetOSVersionString = "15.4";
        PlayerSettings.iOS.appleEnableAutomaticSigning = true;
        if (!string.IsNullOrEmpty(app.AppleTeamId))
        {
            PlayerSettings.iOS.appleDeveloperTeamID = app.AppleTeamId;
        }

        // ARKit's CPU image is landscape with the home button / USB port on the
        // right; locking the screen there makes the camera roll correction zero.
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

        // Less IL2CPP C++ cuts Xcode build time a lot at little runtime cost.
        // Medium stripping removes unused managed code first; packages that use
        // reflection ship link.xml rules for it.
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.iOS, Il2CppCodeGeneration.OptimizeSize);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Medium);

        ARKitSettings.GetOrCreateSettings().requirement = ARKitSettings.Requirement.Required;
    }

    private static void ConfigureXR()
    {
        var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>("Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
        var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.iOS);
        if (general == null)
        {
            general = ScriptableObject.CreateInstance<XRGeneralSettings>();
            general.name = "iPhone Settings";
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = "iPhone Providers";
            general.Manager = manager;
            AssetDatabase.AddObjectToAsset(general, perTarget);
            AssetDatabase.AddObjectToAsset(manager, perTarget);
            perTarget.SetSettingsForBuildTarget(BuildTargetGroup.iOS, general);
        }
        general.InitManagerOnStart = true;

        // Without these the loader is never initialized at startup and
        // AR Foundation reports no session subsystem.
        general.Manager.automaticLoading = true;
        general.Manager.automaticRunning = true;
        EditorUtility.SetDirty(general.Manager);

        if (!XRPackageMetadataStore.IsLoaderAssigned("UnityEngine.XR.ARKit.ARKitLoader", BuildTargetGroup.iOS))
        {
            XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.ARKit.ARKitLoader", BuildTargetGroup.iOS);
        }
        EditorUtility.SetDirty(perTarget);
        EditorUtility.SetDirty(general);

        // ARKit compiles its real implementation only with this define. The
        // ARKit package adds it from an editor coroutine, which never gets to
        // run in a batchmode setup-then-quit, leaving a build with stubs only
        // ("Failed to load session subsystem").
        var defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.iOS);
        if (!defines.Contains("UNITY_XR_ARKIT_LOADER_ENABLED"))
        {
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.iOS, string.IsNullOrEmpty(defines)
                ? "UNITY_XR_ARKIT_LOADER_ENABLED"
                : defines + ";UNITY_XR_ARKIT_LOADER_ENABLED");
        }
    }

    // A starting point only; once it exists the scene is edited by hand.
    // AR Foundation's AR Session and XR Origin (Mobile AR), the camera source and
    // localizer, the room (AprilTagsSceneBuilder.AddRoom) and on-screen controls.
    private static void CreateSceneIfMissing(AprilTagsAppInfo app, string path, Action<string, AprilTagsSceneParts> addContent)
    {
        if (File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session")
            || !EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)"))
        {
            Debug.LogError("[AprilTagsIOSSetup] AR Foundation's GameObject > XR menu items were not found; scene not created");
            return;
        }
        var cameraManager = UnityEngine.Object.FindAnyObjectByType<ARCameraManager>();

        var tags = new GameObject("AprilTags");
        var source = tags.AddComponent<ARFoundationCameraSource>();
        AprilTagsSceneBuilder.SetField(source, "cameraManager", cameraManager);
        var localizer = tags.AddComponent<AprilTagRoomLocalizer>();
        AprilTagsSceneBuilder.SetField(localizer, "cameraSource", source);

        var kind = AprilTagsSceneBuilder.KindOf(path);
        var parts = AprilTagsSceneBuilder.AddRoom(localizer, kind, app);
        var next = AprilTagsSceneBuilder.NextSceneName(app.IPhoneScenes, path);
        if (kind == AprilTagsSceneKind.Survey)
        {
            var ui = new GameObject("UI").AddComponent<SurveyScreenUI>();
            AprilTagsSceneBuilder.SetField(ui, "roomAnchor", parts.Anchor);
            AprilTagsSceneBuilder.SetField(ui, "roomSurvey", parts.Survey);
            AprilTagsSceneBuilder.SetString(ui, "otherSceneName", next);
            AprilTagsSceneBuilder.SetString(ui, "note", "a Quest survey is more accurate");
        }
        else
        {
            var ui = new GameObject("UI").AddComponent<RoomScreenUI>();
            AprilTagsSceneBuilder.SetField(ui, "roomAnchor", parts.Anchor);
            AprilTagsSceneBuilder.SetField(ui, "roomCodeReader", parts.RoomCodeReader);
            AprilTagsSceneBuilder.SetString(ui, "otherSceneName", next);
        }
        addContent?.Invoke(path, parts);

        EditorSceneManager.SaveScene(scene, path);
        Debug.Log($"[AprilTagsIOSSetup] Created {path}");
    }
}
