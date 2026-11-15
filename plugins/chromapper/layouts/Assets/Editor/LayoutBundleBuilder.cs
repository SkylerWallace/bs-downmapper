using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class LayoutBundleBuilder
{
    private const string BundleName = "bsdownmap-layouts";
    private static readonly string[] Prefabs =
    {
        "Assets/Layouts/MainSettings.prefab",
        "Assets/Layouts/EditNoteTimings.prefab"
    };
    private static readonly string[][] BodySlots =
    {
        new[] { "Heading", "Expert", "Hard", "Normal", "Easy", "Options", "PatternLabel", "Pattern",
            "TimingLabel", "Timing", "Edit", "AdvancedButton", "Advanced", "Help", "Scroll", "Viewport",
            "Content", "StartingChart", "Model", "RepeatMatching", "height", "width", "crossover",
            "diagonalPreference", "parityAdherence", "rampAmount", "RestoreDefaults", "ProfileError",
            "Scrollbar", "Empty" },
        new[] { "Undo", "Redo", "LinkRepeats", "LinkSubSections", "FollowPlayhead", "Overview", "Play", "Stop",
            "Position", "Timeline", "SubdivisionLabel", "Subdivision", "Zoom", "Metronome", "NoteTicks" }
    };
    private static readonly string[][] FooterSlots = { new[] { "Generate", "Cancel" }, new[] { "Apply", "Cancel" } };

    [MenuItem("BS Downmapper/Build Layout Bundle")]
    public static void Build() => Build(BuildTarget.StandaloneWindows64, "Build");

    [MenuItem("BS Downmapper/Build Linux x64 Layout Bundle")]
    public static void BuildLinux() => Build(BuildTarget.StandaloneLinux64, "Build/linux-x64");

    private static void Build(BuildTarget target, string outputDirectory)
    {
        for (int i = 0; i < Prefabs.Length; i++)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs[i]);
            Validate(prefab, i);
        }

        var project = Path.GetDirectoryName(Application.dataPath);
        var output = Environment.GetEnvironmentVariable("BS_DOWNMAP_LAYOUT_OUTPUT");
        if (string.IsNullOrEmpty(output)) output = Path.Combine(project, outputDirectory);
        Directory.CreateDirectory(output);
        var bundle = new AssetBundleBuild { assetBundleName = BundleName, assetNames = Prefabs };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { bundle },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            target);
        if (manifest == null) throw new InvalidOperationException("Building the BS Downmapper layouts failed.");
        Debug.Log($"Built {Path.Combine(output, BundleName)}");
    }

    internal static void Validate(GameObject prefab, int index)
    {
        string path = Prefabs[index];
        if (prefab == null || !prefab.transform.Find("Frame"))
            throw new InvalidOperationException($"Missing layout or its Frame in {path}.");

        Check("Body", BodySlots[index]);
        Check("Footer", FooterSlots[index]);

        void Check(string group, string[] expected)
        {
            var root = prefab.transform.Find(group);
            if (root == null) throw new InvalidOperationException($"Missing {group} in {path}.");
            var names = root.GetComponentsInChildren<RectTransform>(true)
                .Where(rect => rect.name.StartsWith("Slot ", StringComparison.Ordinal)).Select(rect => rect.name);
            var counts = names.ToLookup(name => name);
            foreach (string slot in expected)
            {
                int count = counts[$"Slot {slot}"].Count();
                if (count != 1)
                    throw new InvalidOperationException($"Expected one Slot {slot} under {group} in {path}, found {count}.");
            }
        }
    }
}
