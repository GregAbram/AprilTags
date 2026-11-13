#if UNITY_ANDROID
using System;
using System.IO;
using UnityEditor.Android;

// Quest apps on this package share the headset's surveyed room through
// /sdcard/AprilTags (SharedRoom), which needs All files access. Declared here
// in every Quest build; granted once per app with
//   adb shell appops set <app id> MANAGE_EXTERNAL_STORAGE allow
public class AprilTagsAllFilesAccess : IPostGenerateGradleAndroidProject
{
    private const string Permission = "android.permission.MANAGE_EXTERNAL_STORAGE";

    public int callbackOrder => 0;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        var text = File.ReadAllText(manifest);
        if (text.Contains(Permission))
        {
            return;
        }
        var at = text.IndexOf("<application", StringComparison.Ordinal);
        text = text.Insert(at, $"<uses-permission android:name=\"{Permission}\" />\n  ");
        File.WriteAllText(manifest, text);
    }
}
#endif
