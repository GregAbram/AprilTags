#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

// Shows the app's Documents folder (persistentDataPath: the session log,
// learned tags, the room config, a survey's room_code.png) in the Files app and
// in Finder with the phone connected. Unity has no Player setting for it, so
// set the Info.plist keys in the generated Xcode project.
public static class AprilTagsIOSFileSharing
{
    [PostProcessBuild]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS)
        {
            return;
        }
        var plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        plist.root.SetBoolean("UIFileSharingEnabled", true);
        plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        plist.WriteToFile(plistPath);
    }
}
#endif
