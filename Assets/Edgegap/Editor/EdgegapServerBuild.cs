// Command-line Linux dedicated server build for Edgegap. Place under any Assets/**/Editor/ folder.
// Usage (the project must NOT be open in another Editor instance):
//   <Unity> -batchmode -quit -projectPath <project> -executeMethod EdgegapServerBuild.Build -logFile -
// Output: Builds/EdgegapServer/ServerBuild (+ ServerBuild_Data, UnityPlayer.so), matching the Dockerfile.
// Requires Unity Hub modules: Linux Build Support (Mono or IL2CPP) + Linux Dedicated Server Build Support.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class EdgegapServerBuild
{
    public const string OutputPath = "Builds/EdgegapServer/ServerBuild";

    [MenuItem("Tools/Edgegap/Build Linux Dedicated Server")]
    public static void Build()
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
        {
            Fail("Linux Build Support is not installed. Unity Hub > Installs > this version > Add Modules > "
                + "Linux Build Support + Linux Dedicated Server Build Support, then restart Unity.");
            return;
        }

        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0)
        {
            Fail("No enabled scenes in Build Settings. Add the game scene(s) (including the one with the network manager).");
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server, // Dedicated Server: defines UNITY_SERVER, strips rendering
            locationPathName = OutputPath,
            options = BuildOptions.None,
        };

        Debug.Log($"EdgegapServerBuild | Building {scenes.Length} scene(s): {string.Join(", ", scenes)}");
        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            Fail($"Build {report.summary.result} with {report.summary.totalErrors} error(s). See the log above.");
            return;
        }

        Debug.Log($"EdgegapServerBuild | Succeeded: {OutputPath} ({report.summary.totalSize / (1024 * 1024)} MB)");
    }

    /// <summary>
    /// Client build for end-to-end tests: -executeMethod EdgegapServerBuild.BuildClient -edgegapClientTarget webgl|win64|mac|linux|android
    /// Output: Builds/Client/&lt;target&gt;/. Switches the active build target (slow the first time).
    /// </summary>
    public static void BuildClient()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-edgegapClientTarget");
        string name = i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "webgl";

        (BuildTarget target, string path) = name switch
        {
            "win64" => (BuildTarget.StandaloneWindows64, "Builds/Client/win64/Game.exe"),
            "mac" => (BuildTarget.StandaloneOSX, "Builds/Client/mac/Game.app"),
            "linux" => (BuildTarget.StandaloneLinux64, "Builds/Client/linux/Game.x86_64"),
            "android" => (BuildTarget.Android, "Builds/Client/android/MegacityMetro.apk"),
            _ => (BuildTarget.WebGL, "Builds/Client/webgl"),
        };

        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0)
        {
            Fail("No enabled scenes in Build Settings.");
            return;
        }

        bool standalone = target != BuildTarget.WebGL && target != BuildTarget.Android;

        // Android: an APK (not an App Bundle) that can be sideloaded for testing. ARM64 + IL2CPP come from
        // the project's Player settings; without a custom keystore Unity signs it with the debug key.
        if (target == BuildTarget.Android)
            EditorUserBuildSettings.buildAppBundle = false;

        if (standalone)
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;

        // -edgegapClientMono: Mono instead of the project's IL2CPP for quick test clients (minutes instead of
        // ~40 min of IL2CPP Master linking). The project setting is restored after the build.
        var group = UnityEditor.Build.NamedBuildTarget.Standalone;
        var backend = PlayerSettings.GetScriptingBackend(group);
        bool mono = standalone && Array.IndexOf(args, "-edgegapClientMono") >= 0;
        if (mono)
        {
            PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.Mono2x);
            path = path.Replace($"Builds/Client/{name}/", $"Builds/Client/{name}-mono/"); // IL2CPP output can't be reused
        }

        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                target = target,
                subtarget = standalone ? (int)StandaloneBuildSubtarget.Player : 0,
                locationPathName = path,
            });
        }
        finally
        {
            if (mono)
                PlayerSettings.SetScriptingBackend(group, backend);
        }

        if (report.summary.result != BuildResult.Succeeded)
        {
            Fail($"Client build {report.summary.result} with {report.summary.totalErrors} error(s).");
            return;
        }
        Debug.Log($"EdgegapServerBuild | Client build succeeded: {path}");
    }

    private static void Fail(string message)
    {
        Debug.LogError($"EdgegapServerBuild | {message}");
        if (Application.isBatchMode)
            EditorApplication.Exit(1);
    }
}
