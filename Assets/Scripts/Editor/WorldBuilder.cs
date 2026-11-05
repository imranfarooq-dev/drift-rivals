using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the landscape around the circuit: terrain heightmap (hills + mountains, flattened under the track),
// textured terrain layers, waving grass, procedural fir trees made from Poly Haven twig/bark textures,
// and the trackside materials (concrete barriers, catch fence, crowd, ads, glass, metal).
public static class WorldBuilder
{
    public const float Size = 3600f, BaseY = -20f, HeightRange = 460f;
    const string Art = "Assets/Art";
    const string GenDir = "Assets/Art/Gen";
    const string Mats = "Assets/Resources/Materials";
    const string TerrainDir = "Assets/Resources/Terrain";
    const string TreeDir = "Assets/Resources/Trees";

    public static void BuildAll()
    {
        Directory.CreateDirectory(GenDir);
        Directory.CreateDirectory(TerrainDir);
        Directory.CreateDirectory(TreeDir);
        AssetDatabase.Refresh();
        ImportSettings();
        TrackMaterials();
        var trees = BuildTrees();
        BuildTerrain(trees);
        IncludeDetailShaders();
        AssetDatabase.SaveAssets();
    }

    // ---------------- helpers ----------------
    static Texture2D T(string p) => AssetDatabase.LoadAssetAtPath<Texture2D>(p);

    static void Normal(string path)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null || ti.textureType == TextureImporterType.NormalMap) return;
        ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport();
    }

    static void Aniso(string path)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return;
        ti.anisoLevel = 8; ti.SaveAndReimport();
    }

    static string SavePng(Texture2D t, string name, bool alpha, bool linear = false, float coverage = -1f)
    {
        string path = $"{GenDir}/{name}.png";
        File.WriteAllBytes(path, t.EncodeToPNG());
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.sRGBTexture = !linear;
        ti.alphaIsTransparency = alpha;
        ti.anisoLevel = 4;
        ti.wrapMode = TextureWrapMode.Repeat;
        if (coverage > 0) { ti.mipMapsPreserveCoverage = true; ti.alphaTestReferenceValue = coverage; }
        ti.SaveAndReimport();
        return path;
    }

    static string MaskFromRough(string roughPath, string name)
    {
        var rough = new Texture2D(2, 2);
        rough.LoadImage(File.ReadAllBytes(roughPath));
        var px = rough.GetPixels32();
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, (byte)(255 - px[i].r));
        var m = new Texture2D(rough.width, rough.height, TextureFormat.RGBA32, true);
        m.SetPixels32(px); m.Apply();
        return SavePng(m, name, false, true);
    }

    static Material SaveMat(Material m, string name)
    {
        string path = $"{Mats}/{name}.mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Material Lit(string name, Color c, float smooth, float metal, Texture2D albedo = null, Texture2D normal = null, Texture2D mask = null, Vector2? tiling = null, bool clip = false, bool twoSided = false)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetFloat("_WorkflowMode", 1);
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Metallic", metal);
        if (albedo) m.SetTexture("_BaseMap", albedo);
        if (tiling.HasValue) m.SetTextureScale("_BaseMap", tiling.Value);
        if (normal) { m.SetTexture("_BumpMap", normal); m.EnableKeyword("_NORMALMAP"); }
        if (mask) { m.SetTexture("_MetallicGlossMap", mask); m.EnableKeyword("_METALLICSPECGLOSSMAP"); }
        if (clip)
        {
            m.SetFloat("_AlphaClip", 1); m.SetFloat("_Cutoff", 0.45f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
            m.SetOverrideTag("RenderType", "TransparentCutout");
        }
        if (twoSided) m.SetFloat("_Cull", 0);
        m.enableInstancing = true;
        return SaveMat(m, name);
    }

    static void ImportSettings()
    {
        foreach (var f in Directory.GetFiles(Art, "*nor_gl*", SearchOption.AllDirectories))
            if (!f.EndsWith(".meta")) Normal(f.Replace('\\', '/'));
        foreach (var f in Directory.GetFiles(Art, "*diff*", SearchOption.AllDirectories))
            if (!f.EndsWith(".meta")) Aniso(f.Replace('\\', '/'));
    }

    // ---------------- trackside materials ----------------
    static void TrackMaterials()
    {
        string c = $"{Art}/Concrete/concrete_wall_008";
        var cMask = T(MaskFromRough(c + "_rough.jpg", "concrete_mask"));
        Lit("Barrier", new Color(0.72f, 0.73f, 0.75f), 0.6f, 0f, T(c + "_diffuse.jpg"), T(c + "_nor_gl.jpg"), cMask);
        Lit("Concrete", new Color(0.7f, 0.71f, 0.73f), 0.5f, 0f, T(c + "_diffuse.jpg"), T(c + "_nor_gl.jpg"), cMask, new Vector2(4, 4));
        Lit("Metal", new Color(0.62f, 0.64f, 0.66f), 0.6f, 0.85f);
        string lg = $"{Art}/Terrain/leafy_grass";
        Lit("Grass", new Color(0.5f, 0.66f, 0.38f), 0.7f, 0f, T(lg + "_diffuse.jpg"), T(lg + "_nor_gl.jpg"), T(MaskFromRough(lg + "_rough.jpg", "leafy_mask")));
        Lit("Glass", new Color(0.08f, 0.12f, 0.16f), 0.95f, 0.7f);

        // chain-link fence
        int n = 128;
        var fence = new Texture2D(n, n, TextureFormat.RGBA32, true);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = (x + y) % 32, b = (x - y + 128) % 32;
                bool wire = a < 2 || b < 2;
                fence.SetPixel(x, y, wire ? new Color(0.75f, 0.77f, 0.8f, 1) : new Color(0.5f, 0.5f, 0.5f, 0));
            }
        fence.Apply();
        Lit("Fence", Color.white, 0.6f, 0.8f, T(SavePng(fence, "fence", true, false, 0.4f)), null, null, null, true, true);

        // crowd on seats
        var rnd = new System.Random(3);
        int w = 512, h = 128;
        var crowd = new Texture2D(w, h, TextureFormat.RGBA32, true);
        Color[] shirts = { new Color(0.8f, 0.1f, 0.1f), new Color(0.1f, 0.25f, 0.7f), Color.white, new Color(0.95f, 0.8f, 0.1f), new Color(0.1f, 0.1f, 0.1f), new Color(0.2f, 0.6f, 0.2f), new Color(0.9f, 0.45f, 0.1f), new Color(0.6f, 0.6f, 0.65f) };
        Color[] skin = { new Color(0.95f, 0.78f, 0.62f), new Color(0.75f, 0.55f, 0.4f), new Color(0.45f, 0.3f, 0.2f), new Color(0.88f, 0.68f, 0.5f) };
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) crowd.SetPixel(x, y, (y % 32) < 6 ? new Color(0.25f, 0.25f, 0.27f) : new Color(0.08f, 0.2f, 0.5f));
        for (int row = 0; row < h / 32; row++)
            for (int sx = 0; sx < w; sx += 10)
            {
                if (rnd.NextDouble() < 0.18) continue;
                Color sh = shirts[rnd.Next(shirts.Length)], sk = skin[rnd.Next(skin.Length)];
                int bx = sx + rnd.Next(3), by = row * 32 + 6;
                for (int yy = 0; yy < 14; yy++) for (int xx = 0; xx < 8; xx++) crowd.SetPixel(bx + xx, by + yy, sh * (0.8f + 0.2f * yy / 14f));
                for (int yy = 0; yy < 6; yy++) for (int xx = 1; xx < 7; xx++) crowd.SetPixel(bx + xx, by + 15 + yy, sk);
            }
        crowd.Apply();
        Lit("Crowd", Color.white, 0.15f, 0f, T(SavePng(crowd, "crowd", false)), null, null, new Vector2(10, 1));

        // ad boards (fictional sponsors, shapes only)
        Ad("Ad1", new Color(0.8f, 0.05f, 0.05f), Color.white, 0);
        Ad("Ad2", new Color(0.03f, 0.1f, 0.35f), new Color(1f, 0.8f, 0.1f), 1);
        Ad("Ad3", new Color(0.05f, 0.05f, 0.05f), new Color(1f, 0.45f, 0.05f), 2);
    }

    static void Ad(string name, Color bg, Color fg, int style)
    {
        int w = 512, h = 128;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = bg;
                float u = x / (float)w, v = y / (float)h;
                if (style == 0) { if (Mathf.Repeat(u * 6f + v * 1.2f, 1f) < 0.18f && v > 0.2f && v < 0.8f) c = fg; }
                else if (style == 1)
                {
                    float dx = (u - 0.18f) * 4f, dy = v - 0.5f;
                    if (dx * dx + dy * dy < 0.12f) c = fg;
                    if (u > 0.35f && u < 0.92f && Mathf.Abs(v - 0.5f) < 0.12f) c = fg;
                }
                else { if (Mathf.Abs(v - 0.5f - 0.25f * Mathf.Sin(u * 18f)) < 0.09f) c = fg; if (v < 0.08f || v > 0.92f) c = fg * 0.8f; }
                t.SetPixel(x, y, c);
            }
        t.Apply();
        Lit(name, Color.white, 0.45f, 0f, T(SavePng(t, name.ToLower(), false)));
    }

    // ---------------- trees ----------------
    struct Sprite { public int x, y, w, h; public Sprite(int x, int yTop, int w, int h) { this.x = x; this.y = 1024 - yTop - h; this.w = w; this.h = h; } }

    static string BuildBranchTexture()
    {
        var diff = new Texture2D(2, 2); diff.LoadImage(File.ReadAllBytes($"{Art}/Tree/twig_diff.png"));
        var alph = new Texture2D(2, 2); alph.LoadImage(File.ReadAllBytes($"{Art}/Tree/twig_alpha.png"));
        int W = 1024, H = 512;
        var outC = new Color[W * H];
        // stem
        for (int x = 0; x < (int)(W * 0.95f); x++)
        {
            float th = Mathf.Lerp(7f, 1.5f, x / (W * 0.95f));
            for (int y = (int)(H / 2 - th); y <= (int)(H / 2 + th); y++) outC[y * W + x] = new Color(0.28f, 0.2f, 0.13f, 1);
        }
        var sprites = new[] { new Sprite(320, 400, 340, 400), new Sprite(640, 460, 340, 380), new Sprite(670, 50, 290, 330), new Sprite(190, 50, 240, 270) };
        var rnd = new System.Random(9);
        void Stamp(Sprite s, float ax, float ay, float angleDeg, float scale, float shade)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Vector2 right = new Vector2(dir.y, -dir.x);
            float reach = Mathf.Max(s.w, s.h) * scale;
            int x0 = Mathf.Max(0, (int)(ax - reach)), x1 = Mathf.Min(W - 1, (int)(ax + reach));
            int y0 = Mathf.Max(0, (int)(ay - reach)), y1 = Mathf.Min(H - 1, (int)(ay + reach));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 l = new Vector2(x - ax, y - ay);
                    float sx = Vector2.Dot(l, right) / scale + s.w * 0.5f;
                    float sy = Vector2.Dot(l, dir) / scale;
                    if (sx < 0 || sy < 0 || sx >= s.w || sy >= s.h) continue;
                    int px = s.x + (int)sx, py = s.y + (int)sy;
                    float al = alph.GetPixel(px, py).r;
                    if (al < 0.05f) continue;
                    Color c = diff.GetPixel(px, py) * shade; c.a = 1;
                    int idx = y * W + x;
                    Color d = outC[idx];
                    outC[idx] = new Color(Mathf.Lerp(d.r, c.r, al), Mathf.Lerp(d.g, c.g, al), Mathf.Lerp(d.b, c.b, al), Mathf.Max(d.a, al));
                }
        }
        for (float u = 0.06f; u < 0.9f; u += 0.055f)
        {
            float sc = Mathf.Lerp(0.75f, 0.45f, u) * (0.9f + 0.2f * (float)rnd.NextDouble());
            float shade = Mathf.Lerp(0.65f, 1.05f, u);
            var s = sprites[rnd.Next(sprites.Length)];
            Stamp(s, u * W, H / 2, 52f + (float)rnd.NextDouble() * 15f, sc, shade);
            s = sprites[rnd.Next(sprites.Length)];
            Stamp(s, u * W + 10, H / 2, -52f - (float)rnd.NextDouble() * 15f, sc, shade);
        }
        Stamp(sprites[0], W * 0.84f, H / 2, 0f, 0.5f, 1.05f);
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, true);
        tex.SetPixels(outC); tex.Apply();
        return SavePng(tex, "fir_branch", true, false, 0.45f);
    }

    static Mesh FirMesh(float height, float radius, float whorlStep, int perWhorl, bool doubleCard, int seed, string name)
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>();
        var trunkT = new List<int>(); var leafT = new List<int>();
        var rnd = new System.Random(seed);
        // trunk
        int sides = 7;
        float r0 = height * 0.022f + 0.12f;
        for (int k = 0; k <= sides; k++)
        {
            float a = k / (float)sides * Mathf.PI * 2f;
            Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            v.Add(d * r0 + Vector3.down * 0.3f); n.Add(d); uv.Add(new Vector2(k / (float)sides * 2f, 0));
            v.Add(d * 0.04f + Vector3.up * height); n.Add(d); uv.Add(new Vector2(k / (float)sides * 2f, height / 1.5f));
        }
        for (int k = 0; k < sides; k++)
        {
            int b = k * 2;
            trunkT.AddRange(new[] { b, b + 1, b + 2, b + 2, b + 1, b + 3 });
        }
        // branches
        float golden = 137.5f;
        int idx = 0;
        for (float y = height * 0.14f; y < height - 0.35f; y += whorlStep)
        {
            float t = y / height;
            float len = 0.35f + Mathf.Pow(1f - t, 0.85f) * radius * (0.85f + 0.3f * (float)rnd.NextDouble());
            for (int b = 0; b < perWhorl; b++)
            {
                float ang = (idx++ * golden + b * 360f / perWhorl) * Mathf.Deg2Rad;
                Vector3 outDir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
                Vector3 tip = outDir * len + Vector3.down * len * (0.28f + 0.1f * (float)rnd.NextDouble());
                Vector3 axis = tip.normalized;
                Vector3 side = Vector3.Cross(Vector3.up, outDir).normalized;
                float width = len * 0.62f + 0.25f;
                int cards = doubleCard ? 2 : 1;
                for (int c = 0; c < cards; c++)
                {
                    float tilt = doubleCard ? (c == 0 ? 28f : -28f) : 0f;
                    Vector3 sd = Quaternion.AngleAxis(tilt, axis) * side * (width * 0.5f);
                    Vector3 basePt = new Vector3(0, y, 0) + outDir * 0.05f;
                    Vector3 tipPt = basePt + tip;
                    Vector3 nrm = (outDir * 0.55f + Vector3.up * 0.85f).normalized;
                    int i0 = v.Count;
                    v.Add(basePt - sd * 0.5f); v.Add(basePt + sd * 0.5f); v.Add(tipPt - sd); v.Add(tipPt + sd);
                    n.Add(nrm); n.Add(nrm); n.Add(nrm); n.Add(nrm);
                    uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1));
                    leafT.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3 });
                }
            }
        }
        // top tuft
        for (int k = 0; k < 3; k++)
        {
            float a = k * 60f * Mathf.Deg2Rad;
            Vector3 sd = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.45f;
            int i0 = v.Count;
            Vector3 b0 = new Vector3(0, height - 1.4f, 0), t0 = new Vector3(0, height + 0.25f, 0);
            v.Add(b0 - sd); v.Add(b0 + sd); v.Add(t0 - sd * 0.3f); v.Add(t0 + sd * 0.3f);
            for (int q = 0; q < 4; q++) n.Add(Vector3.up);
            uv.Add(new Vector2(0.3f, 0)); uv.Add(new Vector2(0.3f, 1)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1));
            leafT.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3 });
        }
        var m = new Mesh { name = name, subMeshCount = 2 };
        m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv);
        m.SetTriangles(trunkT, 0); m.SetTriangles(leafT, 1);
        m.RecalculateBounds(); m.RecalculateTangents();
        return m;
    }

    static List<GameObject> BuildTrees()
    {
        string branch = BuildBranchTexture();
        var leaf = Lit("FirLeaves", new Color(0.66f, 0.78f, 0.6f), 0.12f, 0f, T(branch), null, null, null, true, true);
        var bark = Lit("FirBark", new Color(0.8f, 0.75f, 0.7f), 0.1f, 0f, T($"{Art}/Tree/bark_diff.png"), T($"{Art}/Tree/bark_nor_gl.png"), null, new Vector2(1, 1));
        var mats = new[] { bark, leaf };

        var list = new List<GameObject>();
        var specs = new[] { (h: 17f, r: 4.2f, name: "FirTall"), (h: 11f, r: 3.4f, name: "FirMedium"), (h: 22f, r: 4.8f, name: "FirGiant") };
        int seed = 1;
        foreach (var s in specs)
        {
            var m0 = FirMesh(s.h, s.r, 0.55f, 5, true, seed, s.name + "_LOD0");
            var m1 = FirMesh(s.h, s.r * 1.1f, 0.95f, 4, true, seed, s.name + "_LOD1");
            seed++;
            string meshPath = $"{TreeDir}/{s.name}_meshes.asset";
            if (File.Exists(meshPath)) AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(m0, meshPath);
            AssetDatabase.AddObjectToAsset(m1, meshPath);

            var root = new GameObject(s.name);
            var l0 = new GameObject("LOD0"); l0.transform.SetParent(root.transform, false);
            l0.AddComponent<MeshFilter>().sharedMesh = m0;
            var r0 = l0.AddComponent<MeshRenderer>(); r0.sharedMaterials = mats;
            var l1 = new GameObject("LOD1"); l1.transform.SetParent(root.transform, false);
            l1.AddComponent<MeshFilter>().sharedMesh = m1;
            var r1 = l1.AddComponent<MeshRenderer>(); r1.sharedMaterials = mats;
            r1.shadowCastingMode = ShadowCastingMode.Off;
            var lg = root.AddComponent<LODGroup>();
            lg.SetLODs(new[] { new LOD(0.16f, new Renderer[] { r0 }), new LOD(0.004f, new Renderer[] { r1 }) });
            lg.RecalculateBounds();
            string prefabPath = $"{TreeDir}/{s.name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            list.Add(prefab);
        }
        return list;
    }

    // ---------------- terrain ----------------
    static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

    static float Fbm(float x, float z, int oct)
    {
        float s = 0, amp = 0.5f, f = 1f, norm = 0;
        for (int i = 0; i < oct; i++) { s += Mathf.PerlinNoise(x * f, z * f) * amp; norm += amp; amp *= 0.5f; f *= 2.03f; }
        return s / norm;
    }

    public static float Natural(float x, float z, Vector3 c)
    {
        float hills = Fbm(x * 0.0021f + 11.3f, z * 0.0021f + 7.1f, 5) * 60f - 12f;
        float dc = new Vector2(x - c.x, z - c.z).magnitude;
        float ridge = 1f - Mathf.Abs(Mathf.PerlinNoise(x * 0.0013f + 3f, z * 0.0013f + 9f) * 2f - 1f);
        float bowl = Smooth(650f, 1750f, dc) * (140f + 220f * ridge * Fbm(x * 0.0011f + 40f, z * 0.0011f + 2f, 4));
        return hills + bowl;
    }

    static void BuildTerrain(List<GameObject> trees)
    {
        var track = Track.BuildData();
        Vector3 c = track.Center;
        Vector3 origin = new Vector3(c.x - Size / 2, BaseY, c.z - Size / 2);
        int res = 1025;
        float cell = Size / (res - 1);

        // distance to the track centreline + track height, stamped around each track point
        var dist = new float[res, res];
        var trackY = new float[res, res];
        for (int i = 0; i < res; i++) for (int j = 0; j < res; j++) dist[i, j] = 1e9f;
        float R = 130f;
        int rc = Mathf.CeilToInt(R / cell);
        for (int k = 0; k < track.Count; k++)
        {
            Vector3 p = track.P[k];
            int ci = Mathf.RoundToInt((p.z - origin.z) / cell), cj = Mathf.RoundToInt((p.x - origin.x) / cell);
            for (int i = Mathf.Max(0, ci - rc); i <= Mathf.Min(res - 1, ci + rc); i++)
                for (int j = Mathf.Max(0, cj - rc); j <= Mathf.Min(res - 1, cj + rc); j++)
                {
                    float wx = origin.x + j * cell, wz = origin.z + i * cell;
                    float d = new Vector2(wx - p.x, wz - p.z).magnitude;
                    if (d < dist[i, j]) { dist[i, j] = d; trackY[i, j] = p.y; }
                }
        }

        var heights = new float[res, res];
        for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
            {
                float wx = origin.x + j * cell, wz = origin.z + i * cell;
                float nat = Natural(wx, wz, c);
                float d = dist[i, j];
                float h = nat;
                if (d < 140f)
                {
                    float ty = trackY[i, j] - 0.35f;
                    float t = Smooth(19.5f, 95f, d);
                    h = Mathf.Lerp(ty, nat, t);
                }
                heights[i, j] = Mathf.Clamp01((h - BaseY) / HeightRange);
            }

        var td = new TerrainData();
        td.heightmapResolution = res;
        td.size = new Vector3(Size, HeightRange, Size);
        td.SetHeights(0, 0, heights);

        // layers
        TerrainLayer Layer(string name, string tex, float tile, Color tint)
        {
            string path = $"{TerrainDir}/{name}.terrainlayer";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            var l = new TerrainLayer
            {
                diffuseTexture = T(tex + "_diffuse.jpg") ?? T(tex + "_diff.jpg"),
                normalMapTexture = T(tex + "_nor_gl.jpg"),
                tileSize = new Vector2(tile, tile),
                diffuseRemapMax = tint,
                smoothness = 0.05f,
                metallic = 0f,
                normalScale = 1f
            };
            AssetDatabase.CreateAsset(l, path);
            return l;
        }
        var grass = Layer("L_Grass", $"{Art}/Terrain/leafy_grass", 5f, new Color(0.46f, 0.6f, 0.34f, 1f));
        var meadow = Layer("L_Meadow", $"{Art}/Terrain/aerial_grass_rock", 32f, new Color(0.5f, 0.58f, 0.4f, 1f));
        var rock = Layer("L_Rock", $"{Art}/Terrain/rocky_terrain_02", 16f, Color.white);
        var gravel = Layer("L_Gravel", $"{Art}/Terrain/gravel", 4f, Color.white);
        td.terrainLayers = new[] { grass, meadow, rock, gravel };

        int ar = 1024;
        td.alphamapResolution = ar;
        var alpha = new float[ar, ar, 4];
        float acell = Size / ar;
        for (int i = 0; i < ar; i++)
            for (int j = 0; j < ar; j++)
            {
                float u = (j + 0.5f) / ar, v = (i + 0.5f) / ar;
                float wx = origin.x + u * Size, wz = origin.z + v * Size;
                int hi = Mathf.Clamp(Mathf.RoundToInt(v * (res - 1)), 0, res - 1), hj = Mathf.Clamp(Mathf.RoundToInt(u * (res - 1)), 0, res - 1);
                float d = dist[hi, hj];
                float slope = td.GetSteepness(u, v);
                float wGravel = (d > 16.8f && d < 22f) ? 1f : 0f;
                float wRock = Smooth(26f, 40f, slope) * (1 - wGravel);
                float m = Smooth(0.42f, 0.66f, Mathf.PerlinNoise(wx * 0.0045f + 5f, wz * 0.0045f + 1f));
                float alt = Smooth(40f, 160f, td.GetInterpolatedHeight(u, v) + BaseY);
                float wMeadow = Mathf.Clamp01(m * 0.8f + alt * 0.8f) * (1 - wRock - wGravel);
                float wGrass = Mathf.Max(0, 1 - wRock - wGravel - wMeadow);
                alpha[i, j, 0] = wGrass; alpha[i, j, 1] = wMeadow; alpha[i, j, 2] = wRock; alpha[i, j, 3] = wGravel;
            }
        td.SetAlphamaps(0, 0, alpha);

        // grass details
        string grassTex = GrassTexture();
        td.SetDetailResolution(1536, 32);
        td.detailPrototypes = new[]
        {
            new DetailPrototype
            {
                prototypeTexture = T(grassTex),
                renderMode = DetailRenderMode.Grass,
                healthyColor = new Color(0.62f, 0.78f, 0.42f),
                dryColor = new Color(0.78f, 0.74f, 0.45f),
                minWidth = 0.6f, maxWidth = 1.2f, minHeight = 0.35f, maxHeight = 0.8f,
                noiseSpread = 0.35f, useInstancing = false
            }
        };
        int dr = 1536;
        var det = new int[dr, dr];
        var rnd = new System.Random(11);
        for (int i = 0; i < dr; i++)
            for (int j = 0; j < dr; j++)
            {
                float u = (j + 0.5f) / dr, v = (i + 0.5f) / dr;
                int hi = Mathf.Clamp(Mathf.RoundToInt(v * (res - 1)), 0, res - 1), hj = Mathf.Clamp(Mathf.RoundToInt(u * (res - 1)), 0, res - 1);
                float d = dist[hi, hj];
                if (d < 9.6f || (d > 16.8f && d < 22f)) continue;
                if (td.GetSteepness(u, v) > 30f) continue;
                float wx = origin.x + u * Size, wz = origin.z + v * Size;
                float nz = Mathf.PerlinNoise(wx * 0.02f, wz * 0.02f);
                det[i, j] = nz > 0.35f ? 3 + (int)(nz * 6) : (rnd.NextDouble() < 0.3 ? 1 : 0);
            }
        td.SetDetailLayer(0, 0, 0, det);

        // trees
        td.treePrototypes = trees.ConvertAll(t => new TreePrototype { prefab = t }).ToArray();
        var inst = new List<TreeInstance>();
        float sp = 11f;
        for (float z = origin.z; z < origin.z + Size; z += sp)
            for (float x = origin.x; x < origin.x + Size; x += sp)
            {
                float wx = x + (float)rnd.NextDouble() * sp, wz = z + (float)rnd.NextDouble() * sp;
                float u = (wx - origin.x) / Size, v = (wz - origin.z) / Size;
                if (u <= 0 || v <= 0 || u >= 1 || v >= 1) continue;
                int hi = Mathf.Clamp(Mathf.RoundToInt(v * (res - 1)), 0, res - 1), hj = Mathf.Clamp(Mathf.RoundToInt(u * (res - 1)), 0, res - 1);
                float d = dist[hi, hj];
                if (d < 30f) continue;
                if (td.GetSteepness(u, v) > 34f) continue;
                float forest = Fbm(wx * 0.0032f + 50f, wz * 0.0032f + 50f, 3);
                float dc = new Vector2(wx - c.x, wz - c.z).magnitude;
                float p = Smooth(0.47f, 0.6f, forest) * 0.75f + 0.01f;
                if (dc > 1650f) p *= 0.4f;
                if (rnd.NextDouble() > p) continue;
                float y = td.GetInterpolatedHeight(u, v) / HeightRange;
                float s = 0.75f + (float)rnd.NextDouble() * 0.55f;
                byte g = (byte)(200 + rnd.Next(55));
                inst.Add(new TreeInstance
                {
                    position = new Vector3(u, y, v),
                    prototypeIndex = rnd.NextDouble() < 0.5 ? 0 : (rnd.NextDouble() < 0.6 ? 1 : 2),
                    widthScale = s * (0.9f + 0.2f * (float)rnd.NextDouble()),
                    heightScale = s,
                    rotation = (float)rnd.NextDouble() * Mathf.PI * 2f,
                    color = new Color32(g, g, g, 255),
                    lightmapColor = new Color32(255, 255, 255, 255)
                });
            }
        td.SetTreeInstances(inst.ToArray(), true);
        Debug.Log("[DriftRivals] Terrain trees: " + inst.Count);

        string tdPath = $"{TerrainDir}/TrackTerrain.asset";
        if (File.Exists(tdPath)) AssetDatabase.DeleteAsset(tdPath);
        AssetDatabase.CreateAsset(td, tdPath);
        Object.DestroyImmediate(track.gameObject);

        var tm = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
        tm.enableInstancing = true;
        SaveMat(tm, "Terrain");
    }

    static string GrassTexture()
    {
        int w = 256, h = 256;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true);
        var px = new Color[w * h];
        var rnd = new System.Random(4);
        for (int b = 0; b < 46; b++)
        {
            float x0 = 10 + (float)rnd.NextDouble() * (w - 20);
            float height = h * (0.55f + 0.45f * (float)rnd.NextDouble());
            float bend = ((float)rnd.NextDouble() - 0.5f) * 60f;
            float width = 3f + (float)rnd.NextDouble() * 3f;
            Color col = Color.Lerp(new Color(0.35f, 0.55f, 0.2f), new Color(0.6f, 0.75f, 0.3f), (float)rnd.NextDouble());
            for (int y = 0; y < height; y++)
            {
                float t01 = y / height;
                float cx = x0 + bend * t01 * t01;
                float hw = width * (1 - t01);
                for (int x = (int)(cx - hw - 1); x <= (int)(cx + hw + 1); x++)
                {
                    if (x < 0 || x >= w) continue;
                    float a = Mathf.Clamp01(hw + 0.5f - Mathf.Abs(x - cx));
                    if (a <= 0) continue;
                    Color c = col * Mathf.Lerp(0.55f, 1.1f, t01); c.a = 1;
                    int i = y * w + x;
                    px[i] = Color.Lerp(px[i], c, a); px[i].a = Mathf.Max(px[i].a, a);
                }
            }
        }
        t.SetPixels(px); t.Apply();
        return SavePng(t, "grass_blades", true, false, 0.5f);
    }

    // Terrain detail (grass) shaders are only referenced at runtime, so make sure builds keep them.
    static void IncludeDetailShaders()
    {
        string[] names =
        {
            "Hidden/TerrainEngine/Details/UniversalPipeline/WavingDoublePass",
            "Hidden/TerrainEngine/Details/UniversalPipeline/BillboardWavingDoublePass",
            "Hidden/TerrainEngine/Details/UniversalPipeline/Vertexlit",
            "Universal Render Pipeline/Terrain/Lit",
        };
        var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
        var so = new SerializedObject(gs);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        foreach (var n in names)
        {
            var sh = Shader.Find(n);
            if (sh == null) { Debug.LogWarning("[DriftRivals] shader not found: " + n); continue; }
            bool has = false;
            for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) has = true;
            if (has) continue;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
        }
        so.ApplyModifiedProperties();
    }
}
