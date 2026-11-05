using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

// One-click project setup + macOS build. Also runnable headless:
//   Unity -batchmode -quit -projectPath . -executeMethod ProjectSetup.SetupAndBuild
public static class ProjectSetup
{
    const string Tex = "Assets/Resources/Textures";
    const string Gen = "Assets/Resources/Textures/Gen";
    const string Mats = "Assets/Resources/Materials";
    const string Settings = "Assets/Settings";
    const string ScenePath = "Assets/Scenes/Main.unity";
    public const string BuildPath = "Builds/DriftRivals.app";

    [MenuItem("Drift Rivals/1. Setup Project")]
    public static void Setup()
    {
        Directory.CreateDirectory(Gen);
        Directory.CreateDirectory(Mats);
        Directory.CreateDirectory(Settings);
        Directory.CreateDirectory("Assets/Scenes");
        AssetDatabase.Refresh();

        ConfigureImports();
        GenerateTextures();
        CreateMaterials();
        WorldBuilder.BuildAll();
        ConfigureURP();
        // glTFast picks shaders for the active pipeline at import time, so re-import after URP is on.
        AssetDatabase.ImportAsset("Assets/Resources/Models/CarConcept.glb", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        ConfigurePlayer();
        CreateScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[DriftRivals] Setup complete.");
    }

    [MenuItem("Drift Rivals/2. Build macOS App")]
    public static void Build()
    {
        Directory.CreateDirectory("Builds");
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = BuildPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[DriftRivals] Build result: " + report.summary.result + " size " + report.summary.totalSize / (1024 * 1024) + " MB, errors " + report.summary.totalErrors);
        if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    public static void SetupAndBuild()
    {
        Setup();
        Build();
    }

    // ---------------- imports ----------------
    static void ConfigureImports()
    {
        foreach (var n in new[] { "asphalt_track_nor_gl", "sparse_grass_nor_gl" })
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath($"{Tex}/{n}.jpg");
            if (ti == null) continue;
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
        }
        foreach (var n in new[] { "asphalt_track_diff", "sparse_grass_diff" })
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath($"{Tex}/{n}.jpg");
            if (ti == null) continue;
            ti.anisoLevel = 8;
            ti.SaveAndReimport();
        }
        var sky = (TextureImporter)AssetImporter.GetAtPath($"{Tex}/sky.hdr");
        if (sky != null)
        {
            sky.mipmapEnabled = false;
            sky.maxTextureSize = 4096;
            sky.wrapModeU = TextureWrapMode.Repeat;
            sky.wrapModeV = TextureWrapMode.Clamp;
            sky.textureCompression = TextureImporterCompression.CompressedHQ;
            sky.SaveAndReimport();
        }
    }

    // ---------------- generated textures ----------------
    static Texture2D NewTex(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, true);

    static void SavePng(Texture2D t, string name, bool linear = false, bool clamp = false)
    {
        string path = $"{Gen}/{name}.png";
        File.WriteAllBytes(path, t.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.sRGBTexture = !linear;
        ti.alphaIsTransparency = !linear;
        ti.anisoLevel = 4;
        ti.wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
        ti.SaveAndReimport();
    }

    static void GenerateTextures()
    {
        // curb: red / white blocks along the track
        var curb = NewTex(64, 128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 64; x++)
            {
                bool red = y < 64;
                Color c = red ? new Color(0.75f, 0.06f, 0.05f) : new Color(0.93f, 0.93f, 0.92f);
                float shade = 0.85f + 0.15f * Mathf.Sin(x / 63f * Mathf.PI);
                curb.SetPixel(x, y, c * shade);
            }
        curb.Apply(); SavePng(curb, "curb");

        // checker for the start line: 14 x 4 squares
        var chk = NewTex(448, 128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 448; x++)
                chk.SetPixel(x, y, ((x / 32) + (y / 32)) % 2 == 0 ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.04f, 0.04f, 0.04f));
        chk.Apply(); SavePng(chk, "checker");

        // barrier: white concrete with blue band
        var bar = NewTex(64, 128);
        var rnd = new System.Random(5);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 64; x++)
            {
                float u = x / 63f;
                Color c = (u > 0.55f && u < 0.8f) ? new Color(0.05f, 0.2f, 0.6f) : new Color(0.82f, 0.82f, 0.8f);
                if (y % 64 < 2) c *= 0.6f; // segment seams
                c *= 0.92f + 0.08f * (float)rnd.NextDouble();
                c.a = 1;
                bar.SetPixel(x, y, c);
            }
        bar.Apply(); SavePng(bar, "barrier");

        // smoke puff
        var sm = NewTex(128, 128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float dx = (x - 63.5f) / 63.5f, dy = (y - 63.5f) / 63.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float n = Mathf.PerlinNoise(x * 0.06f, y * 0.06f) * 0.6f + Mathf.PerlinNoise(x * 0.15f + 10, y * 0.15f) * 0.4f;
                float a = Mathf.Clamp01(1f - d) ;
                a = a * a * (0.55f + 0.45f * n);
                sm.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        sm.Apply(); SavePng(sm, "smoke", false, true);

        // skid strip
        var sk = NewTex(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                float u = x / 31f;
                float edge = Mathf.Clamp01(Mathf.Min(u, 1 - u) * 6f);
                float grain = 0.75f + 0.25f * Mathf.PerlinNoise(x * 0.5f, y * 0.2f);
                sk.SetPixel(x, y, new Color(1, 1, 1, edge * grain));
            }
        sk.Apply(); SavePng(sk, "skid", false, false);

        // metallic/smoothness masks from roughness maps
        foreach (var n in new[] { "asphalt_track", "sparse_grass" })
        {
            string src = $"{Tex}/{n}_rough.jpg";
            if (!File.Exists(src)) continue;
            var rough = new Texture2D(2, 2);
            rough.LoadImage(File.ReadAllBytes(src));
            var mask = new Texture2D(rough.width, rough.height, TextureFormat.RGBA32, true);
            var px = rough.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                byte smooth = (byte)(255 - px[i].r);
                px[i] = new Color32(0, 0, 0, smooth);
            }
            mask.SetPixels32(px); mask.Apply();
            SavePng(mask, n + "_mask", true);
        }
    }

    // ---------------- materials ----------------
    static Texture2D T(string path) => AssetDatabase.LoadAssetAtPath<Texture2D>(path);

    static Material SaveMat(Material m, string name)
    {
        string path = $"{Mats}/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Material Lit(string name, Color color, float smooth, Texture2D baseMap = null, Texture2D normal = null, Texture2D mask = null, float normalScale = 1f)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetFloat("_WorkflowMode", 1);
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Metallic", 0);
        if (baseMap) m.SetTexture("_BaseMap", baseMap);
        if (normal) { m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", normalScale); m.EnableKeyword("_NORMALMAP"); }
        if (mask) { m.SetTexture("_MetallicGlossMap", mask); m.EnableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_SmoothnessTextureChannel", 0); }
        m.enableInstancing = true;
        return SaveMat(m, name);
    }

    static Material Particle(string name, Texture2D tex, Color c, bool cullOff)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        m.SetTexture("_BaseMap", tex);
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Surface", 1);
        m.SetFloat("_Blend", 0);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.SetFloat("_Cull", cullOff ? 0 : 2);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
        return SaveMat(m, name);
    }

    static void CreateMaterials()
    {
        Lit("Road", new Color(1.25f, 1.25f, 1.25f), 0.85f, T($"{Tex}/asphalt_track_diff.jpg"), T($"{Tex}/asphalt_track_nor_gl.jpg"), T($"{Gen}/asphalt_track_mask.png"), 1.2f);
        Lit("Grass", new Color(0.62f, 0.9f, 0.5f), 0.6f, T($"{Tex}/sparse_grass_diff.jpg"), T($"{Tex}/sparse_grass_nor_gl.jpg"), T($"{Gen}/sparse_grass_mask.png"));
        Lit("Ground", new Color(0.55f, 0.82f, 0.45f), 0.5f, T($"{Tex}/sparse_grass_diff.jpg"), T($"{Tex}/sparse_grass_nor_gl.jpg"), T($"{Gen}/sparse_grass_mask.png"));
        Lit("Curb", Color.white, 0.35f, T($"{Gen}/curb.png"));
        Lit("Barrier", Color.white, 0.2f, T($"{Gen}/barrier.png"));
        Lit("Checker", Color.white, 0.3f, T($"{Gen}/checker.png"));
        Lit("Line", new Color(0.95f, 0.95f, 0.93f), 0.35f);
        Lit("Concrete", new Color(0.62f, 0.62f, 0.64f), 0.15f);
        Lit("Trunk", new Color(0.24f, 0.16f, 0.1f), 0.05f);
        Lit("Foliage", new Color(0.1f, 0.26f, 0.08f), 0.08f);
        Lit("Mountain", new Color(0.26f, 0.33f, 0.28f), 0f);
        Particle("Smoke", T($"{Gen}/smoke.png"), new Color(1, 1, 1, 1), false);
        Particle("Skid", T($"{Gen}/skid.png"), new Color(1, 1, 1, 1), true);

        var sky = new Material(Shader.Find("Skybox/Panoramic"));
        sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture>($"{Tex}/sky.hdr"));
        sky.SetFloat("_Mapping", 1);
        sky.SetFloat("_ImageType", 0);
        sky.SetFloat("_Exposure", 1.0f);
        sky.SetFloat("_Rotation", 0);
        sky.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
        SaveMat(sky, "Sky");
    }

    // ---------------- URP ----------------
    static void SetProp(SerializedObject so, string name, System.Action<SerializedProperty> set)
    {
        var p = so.FindProperty(name);
        if (p != null) set(p); else Debug.LogWarning("[DriftRivals] URP property not found: " + name);
    }

    static void ConfigureURP()
    {
        string rdPath = $"{Settings}/URP_Renderer.asset";
        string urpPath = $"{Settings}/URP_Asset.asset";
        var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rdPath);
        if (rd == null)
        {
            rd = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rd, rdPath);
        }
        var rso = new SerializedObject(rd);
        var ppd = rso.FindProperty("postProcessData");
        if (ppd != null && ppd.objectReferenceValue == null)
        {
            var data = AssetDatabase.LoadAssetAtPath<Object>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            if (data != null) { ppd.objectReferenceValue = data; rso.ApplyModifiedProperties(); }
        }

        // SSAO renderer feature
        try
        {
            var ssaoType = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            bool has = false;
            foreach (var f in rd.rendererFeatures) if (f != null && f.GetType() == ssaoType) has = true;
            if (ssaoType != null && !has)
            {
                var feat = (ScriptableRendererFeature)ScriptableObject.CreateInstance(ssaoType);
                feat.name = "SSAO";
                AssetDatabase.AddObjectToAsset(feat, rd);
                rd.rendererFeatures.Add(feat);
                var fso = new SerializedObject(feat);
                var settings = fso.FindProperty("m_Settings");
                if (settings != null)
                {
                    var inten = settings.FindPropertyRelative("Intensity"); if (inten != null) inten.floatValue = 1.2f;
                    var rad = settings.FindPropertyRelative("Radius"); if (rad != null) rad.floatValue = 0.4f;
                    fso.ApplyModifiedProperties();
                }
                rso = new SerializedObject(rd);
                var map = rso.FindProperty("m_RendererFeatureMap");
                if (map != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feat, out string _, out long lid))
                {
                    map.arraySize++;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = lid;
                    rso.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(rd);
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[DriftRivals] SSAO not added: " + e.Message); }

        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(urpPath);
        if (urp == null)
        {
            urp = UniversalRenderPipelineAsset.Create(rd);
            AssetDatabase.CreateAsset(urp, urpPath);
        }
        var so = new SerializedObject(urp);
        SetProp(so, "m_SupportsHDR", p => p.boolValue = true);
        SetProp(so, "m_MSAA", p => p.intValue = 1); // TAA is used instead
        SetProp(so, "m_RenderScale", p => p.floatValue = 1f);
        SetProp(so, "m_RequireDepthTexture", p => p.boolValue = true);
        SetProp(so, "m_RequireOpaqueTexture", p => p.boolValue = true);
        SetProp(so, "m_MainLightShadowmapResolution", p => p.intValue = 4096);
        SetProp(so, "m_ShadowDistance", p => p.floatValue = 220f);
        SetProp(so, "m_ShadowCascadeCount", p => p.intValue = 4);
        SetProp(so, "m_SoftShadowsSupported", p => p.boolValue = true);
        SetProp(so, "m_ColorGradingMode", p => p.intValue = 1); // HDR grading
        SetProp(so, "m_ColorGradingLutSize", p => p.intValue = 32);
        SetProp(so, "m_UseSRPBatcher", p => p.boolValue = true);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(urp);

        GraphicsSettings.defaultRenderPipeline = urp;
        int cur = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urp;
        }
        QualitySettings.SetQualityLevel(QualitySettings.names.Length - 1, false);
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        AssetDatabase.SaveAssets();
    }

    // ---------------- player ----------------
    static void ConfigurePlayer()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.companyName = "DriftRivals";
        PlayerSettings.productName = "Drift Rivals";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.driftrivals.game");
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
        PlayerSettings.defaultIsNativeResolution = true;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.bundleVersion = "1.0";
        try { PlayerSettings.SplashScreen.show = false; } catch { }

        // Legacy input manager (no extra package needed)
        var ps = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
        if (ps != null)
        {
            var so = new SerializedObject(ps);
            var p = so.FindProperty("activeInputHandler");
            if (p != null) { p.intValue = 0; so.ApplyModifiedProperties(); }
        }
    }

    // ---------------- scene + environment lighting ----------------
    static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var sky = AssetDatabase.LoadAssetAtPath<Material>($"{Mats}/Sky.mat");
        RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.defaultReflectionResolution = 256;

        string lsPath = $"{Settings}/Lighting.lighting";
        var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(lsPath);
        if (ls == null)
        {
            ls = new LightingSettings { name = "Lighting" };
            AssetDatabase.CreateAsset(ls, lsPath);
        }
        ls.bakedGI = false;
        ls.realtimeGI = false;
        ls.autoGenerate = false;
        Lightmapping.lightingSettings = ls;

        EditorSceneManager.SaveScene(scene, ScenePath);
        try { Lightmapping.Bake(); } catch (System.Exception e) { Debug.LogWarning("[DriftRivals] Bake skipped: " + e.Message); }
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    // ---------------- post build: local network permission text + re-sign ----------------
    [PostProcessBuild]
    public static void OnPostBuild(BuildTarget target, string path)
    {
        if (target != BuildTarget.StandaloneOSX) return;
        string plist = Path.Combine(path, "Contents/Info.plist");
        if (!File.Exists(plist)) return;
        string s = File.ReadAllText(plist);
        if (!s.Contains("NSLocalNetworkUsageDescription"))
        {
            int i = s.IndexOf("<dict>", System.StringComparison.Ordinal);
            if (i >= 0)
            {
                s = s.Insert(i + 6, "\n\t<key>NSLocalNetworkUsageDescription</key>\n\t<string>Drift Rivals finds and joins races on your local Wi-Fi network.</string>");
                File.WriteAllText(plist, s);
            }
        }
        try
        {
            var p = Process.Start(new ProcessStartInfo("/usr/bin/codesign", $"--force --deep --sign - \"{path}\"") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(120000);
        }
        catch (System.Exception e) { Debug.LogWarning("[DriftRivals] codesign failed: " + e.Message); }
    }
}
