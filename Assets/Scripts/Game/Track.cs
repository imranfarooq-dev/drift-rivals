using System.Collections.Generic;
using UnityEngine;

// Builds the whole circuit (road, curbs, run-off, barriers, scenery) from a closed spline.
public class Track : MonoBehaviour
{
    public const float HalfWidth = 7f;
    public const float CurbOuter = 8.2f;
    public const float BarrierLat = 17f;
    public const float BarrierThick = 0.6f;
    public const float BarrierHeight = 1.1f;
    public const float Spacing = 2f;

    public Vector3[] P;   // centerline points
    public Vector3[] F;   // forward
    public Vector3[] R;   // right (horizontal)
    public float Length;
    public float StepLen;
    public Vector3 Center;
    public int Count => P.Length;

    // x, height, z (meters). Closed loop, driven in this order.
    static readonly Vector3[] Ctrl =
    {
        new Vector3(0, 0, 0),
        new Vector3(0, 1, 150),
        new Vector3(0, 2, 300),
        new Vector3(40, 3, 420),
        new Vector3(160, 6, 470),
        new Vector3(300, 8, 440),
        new Vector3(380, 7, 360),
        new Vector3(360, 5, 260),
        new Vector3(280, 3, 220),
        new Vector3(240, 2, 140),
        new Vector3(300, 2, 60),
        new Vector3(420, 4, 20),
        new Vector3(480, 6, -80),
        new Vector3(440, 5, -200),
        new Vector3(340, 3, -230),
        new Vector3(260, 1.5f, -160),
        new Vector3(180, 0.5f, -220),
        new Vector3(80, 0, -260),
        new Vector3(10, 0, -180),
        new Vector3(0, 0, -90),
    };

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    public static Track Build()
    {
        var go = new GameObject("Track");
        var t = go.AddComponent<Track>();
        t.Sample();
        t.BuildMeshes();
        t.BuildTerrain();
        return t;
    }

    void Sample()
    {
        var dense = new List<Vector3>();
        int n = Ctrl.Length;
        for (int s = 0; s < n; s++)
        {
            Vector3 p0 = Ctrl[(s - 1 + n) % n], p1 = Ctrl[s], p2 = Ctrl[(s + 1) % n], p3 = Ctrl[(s + 2) % n];
            for (int k = 0; k < 200; k++)
            {
                var c = CatmullRom(p0, p1, p2, p3, k / 200f);
                dense.Add(new Vector3(c.x, c.y + 0.15f, c.z));
            }
        }
        float total = 0;
        var cum = new float[dense.Count + 1];
        for (int i = 0; i < dense.Count; i++)
        {
            total += Vector3.Distance(dense[i], dense[(i + 1) % dense.Count]);
            cum[i + 1] = total;
        }
        int count = Mathf.RoundToInt(total / Spacing);
        StepLen = total / count;
        Length = total;
        P = new Vector3[count];
        int j = 0;
        for (int i = 0; i < count; i++)
        {
            float d = i * StepLen;
            while (cum[j + 1] < d) j++;
            float f = (d - cum[j]) / Mathf.Max(1e-5f, cum[j + 1] - cum[j]);
            P[i] = Vector3.Lerp(dense[j], dense[(j + 1) % dense.Count], f);
        }
        F = new Vector3[count];
        R = new Vector3[count];
        Center = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Vector3 fw = P[(i + 1) % count] - P[(i - 1 + count) % count];
            F[i] = fw.normalized;
            Vector3 flat = new Vector3(fw.x, 0, fw.z).normalized;
            R[i] = Vector3.Cross(Vector3.up, flat).normalized;
            Center += P[i];
        }
        Center /= count;
    }

    // ---------- queries ----------
    public int Wrap(int i) { i %= Count; return i < 0 ? i + Count : i; }

    public int Nearest(Vector3 pos, int hint = -1)
    {
        int best = 0; float bd = float.MaxValue;
        if (hint < 0)
        {
            for (int i = 0; i < Count; i++)
            {
                float d = (P[i] - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
        for (int k = -60; k <= 60; k++)
        {
            int i = Wrap(hint + k);
            float d = (P[i] - pos).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        // jumped far (reset / teleport): do a full search
        if (bd > 60f * 60f) return Nearest(pos, -1);
        return best;
    }

    public float DistAlong(Vector3 pos, int i)
    {
        float along = Vector3.Dot(pos - P[i], F[i]);
        float d = i * StepLen + along;
        if (d < 0) d += Length;
        if (d >= Length) d -= Length;
        return d;
    }

    public float Lateral(Vector3 pos, int i) => Vector3.Dot(pos - P[i], R[i]);

    public Vector3 PointAt(float dist, float lateral = 0)
    {
        dist = Mathf.Repeat(dist, Length);
        float f = dist / StepLen;
        int i = Mathf.FloorToInt(f);
        float u = f - i;
        Vector3 p = Vector3.Lerp(P[Wrap(i)], P[Wrap(i + 1)], u);
        Vector3 r = Vector3.Lerp(R[Wrap(i)], R[Wrap(i + 1)], u).normalized;
        return p + r * lateral;
    }

    public Vector3 ForwardAt(float dist)
    {
        int i = Wrap(Mathf.FloorToInt(Mathf.Repeat(dist, Length) / StepLen));
        return F[i];
    }

    public void GridSlot(int slot, out Vector3 pos, out Quaternion rot)
    {
        int row = slot / 2;
        float d = Length - 12f - row * 9f;
        float lat = (slot % 2 == 0 ? -3.2f : 3.2f);
        pos = PointAt(d - (slot % 2) * 4f, lat) + Vector3.up * 0.6f;
        Vector3 f = ForwardAt(d);
        rot = Quaternion.LookRotation(new Vector3(f.x, f.y, f.z), Vector3.up);
    }

    // ---------- mesh building ----------
    class MB
    {
        public List<Vector3> v = new List<Vector3>();
        public List<Vector2> uv = new List<Vector2>();
        public List<int> t = new List<int>();
        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }
    }

    delegate float YFunc(int i);

    // Strip along the track between lateral offset A and B. Front face normal = Cross(forward, B - A).
    void Strip(MB mb, float latA, YFunc yA, float latB, YFunc yB, float uWidth, float vTile, int from = 0, int to = -1)
    {
        if (to < 0) to = Count;
        int baseIdx = mb.v.Count;
        float width = Mathf.Abs(latB - latA);
        int rings = to - from + 1;
        for (int k = 0; k < rings; k++)
        {
            int i = from + k;
            int w = Wrap(i);
            Vector3 a = P[w] + R[w] * latA; a.y = yA(w);
            Vector3 b = P[w] + R[w] * latB; b.y = yB(w);
            mb.v.Add(a); mb.v.Add(b);
            float vv = i * StepLen / vTile;
            mb.uv.Add(new Vector2(0, vv));
            mb.uv.Add(new Vector2(uWidth > 0 ? width / uWidth : 1f, vv));
        }
        for (int k = 0; k < rings - 1; k++)
        {
            int a0 = baseIdx + k * 2, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
            mb.t.Add(a0); mb.t.Add(a1); mb.t.Add(b0);
            mb.t.Add(b0); mb.t.Add(a1); mb.t.Add(b1);
        }
    }

    GameObject MakeObj(string name, Mesh mesh, Material mat, bool collider, float grip = 1f)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        if (collider)
        {
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.AddComponent<Surface>().grip = grip;
        }
        return go;
    }

    static Material Mat(string n)
    {
        var m = Resources.Load<Material>("Materials/" + n);
        if (m == null) Debug.LogWarning("Missing material " + n);
        return m;
    }

    void BuildMeshes()
    {
        YFunc H = i => P[i].y;

        // Road
        var road = new MB();
        Strip(road, -HalfWidth, H, HalfWidth, H, 7f, 7f);
        MakeObj("Road", road.ToMesh("Road"), Mat("Road"), true, 1f);

        // White edge lines (visual only)
        var lines = new MB();
        Strip(lines, -6.75f, i => P[i].y + 0.006f, -6.45f, i => P[i].y + 0.006f, 0, 4f);
        Strip(lines, 6.45f, i => P[i].y + 0.006f, 6.75f, i => P[i].y + 0.006f, 0, 4f);
        var lo = MakeObj("Lines", lines.ToMesh("Lines"), Mat("Line"), false);
        lo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Start / finish checker
        var chk = new MB();
        Strip(chk, -HalfWidth, i => P[i].y + 0.008f, HalfWidth, i => P[i].y + 0.008f, 14f, 2f * StepLen, 0, 2);
        var co = MakeObj("StartLine", chk.ToMesh("Start"), Mat("Checker"), false);
        co.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Curbs (slightly raised outer edge)
        var curb = new MB();
        Strip(curb, HalfWidth, H, CurbOuter, i => P[i].y + 0.07f, 0, 6f);
        Strip(curb, -CurbOuter, i => P[i].y + 0.07f, -HalfWidth, H, 0, 6f);
        MakeObj("Curb", curb.ToMesh("Curb"), Mat("Curb"), true, 0.95f);

        // Grass run-off
        var grass = new MB();
        Strip(grass, CurbOuter, i => P[i].y + 0.07f, BarrierLat, H, 8f, 8f);
        Strip(grass, -BarrierLat, H, -CurbOuter, i => P[i].y + 0.07f, 8f, 8f);
        MakeObj("Runoff", grass.ToMesh("Runoff"), Mat("Grass"), true, 0.6f);

        // Jersey-profile concrete barriers + catch fence on both sides
        BuildBarriers();
        BuildFence();

        BuildGantry();
        BuildGrandstands();
        BuildAdBoards();
    }

    void BuildBarriers()
    {
        // profile (lateral offset from BarrierLat, height) from road side to outside
        float[] pl = { 0f, 0.08f, 0.22f, 0.40f, 0.55f, 0.62f, 0.62f };
        float[] ph = { 0f, 0.10f, 0.32f, 1.05f, 1.05f, 0.95f, -0.4f };
        var mb = new MB();
        for (int side = -1; side <= 1; side += 2)
        {
            for (int k = 0; k < pl.Length - 1; k++)
            {
                float la = BarrierLat + pl[k], lb = BarrierLat + pl[k + 1];
                float ha = ph[k], hb = ph[k + 1];
                float u = Mathf.Sqrt((lb - la) * (lb - la) + (hb - ha) * (hb - ha));
                if (side > 0)
                    Strip(mb, la, i => P[i].y + ha, lb, i => P[i].y + hb, u > 0 ? 1.5f : 0, 3f);
                else
                    Strip(mb, -lb, i => P[i].y + hb, -la, i => P[i].y + ha, u > 0 ? 1.5f : 0, 3f);
            }
        }
        MakeObj("Barrier", mb.ToMesh("Barrier"), Mat("Barrier"), true, 1f);
    }

    void BuildFence()
    {
        float lat = BarrierLat + 0.5f, h0 = 1.0f, h1 = 4.2f;
        var mb = new MB();
        Strip(mb, lat, i => P[i].y + h0, lat, i => P[i].y + h1, 1.6f, 1.6f);
        Strip(mb, -lat, i => P[i].y + h1, -lat, i => P[i].y + h0, 1.6f, 1.6f);
        var f = MakeObj("Fence", mb.ToMesh("Fence"), Mat("Fence"), false);
        f.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        var posts = new List<CombineInstance>();
        for (int i = 0; i < Count; i += 3)
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 p = P[i] + R[i] * (s * (lat + 0.05f)); p.y = P[i].y + (h0 + h1) * 0.5f;
                AddBox(posts, p, new Vector3(0.08f, h1 - h0 + 0.2f, 0.08f), Quaternion.LookRotation(F[i]));
            }
        Combine("FencePosts", posts, Mat("Metal"), false);
    }

    void BuildAdBoards()
    {
        string[] mats = { "Ad1", "Ad2", "Ad3" };
        var lists = new[] { new List<CombineInstance>(), new List<CombineInstance>(), new List<CombineInstance>() };
        int n = 0;
        // boards along long, gently curved sections, just behind the barrier
        for (int i = 10; i < Count - 10; i += 9)
        {
            float turn = Vector3.Angle(F[Wrap(i - 6)], F[Wrap(i + 6)]);
            if (turn > 6f) continue;
            if ((i / 9) % 3 == 1) continue;
            for (int s = -1; s <= 1; s += 2)
            {
                if ((i + s) % 4 == 0) continue;
                Vector3 p = P[i] + R[i] * (s * (BarrierLat + 0.75f)); p.y = P[i].y + 0.55f;
                Quaternion q = Quaternion.LookRotation(R[i] * s);
                lists[n % 3].Add(new CombineInstance { mesh = Quad(), transform = Matrix4x4.TRS(p, q, new Vector3(StepLen * 4.4f, 1.1f, 1f)) });
                n++;
            }
        }
        for (int k = 0; k < 3; k++)
            if (lists[k].Count > 0)
            {
                var g = Combine("Ads" + k, lists[k], Mat(mats[k]), false);
                g.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
    }

    static Mesh quad;
    static Mesh Quad()
    {
        if (quad == null)
        {
            quad = new Mesh { name = "Quad" };
            quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            quad.RecalculateNormals(); quad.RecalculateTangents();
        }
        return quad;
    }

    // ---------- props (combined meshes) ----------
    static Mesh cube;
    static Mesh Cube()
    {
        if (cube == null) cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        return cube;
    }

    void AddBox(List<CombineInstance> list, Vector3 pos, Vector3 size, Quaternion rot)
    {
        list.Add(new CombineInstance { mesh = Cube(), transform = Matrix4x4.TRS(pos, rot, size) });
    }

    GameObject Combine(string name, List<CombineInstance> list, Material mat, bool collider)
    {
        var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.CombineMeshes(list.ToArray(), true, true);
        m.RecalculateBounds();
        return MakeObj(name, m, mat, collider);
    }

    void BuildGantry()
    {
        var list = new List<CombineInstance>();
        Vector3 p = P[1]; Vector3 r = R[1]; Quaternion q = Quaternion.LookRotation(F[1]);
        for (int s = -1; s <= 1; s += 2)
            AddBox(list, p + r * (s * 12.5f) + Vector3.up * 3.75f, new Vector3(0.8f, 7.5f, 0.8f), q);
        AddBox(list, p + Vector3.up * 7.6f, new Vector3(26f, 1.6f, 0.8f), q);
        Combine("Gantry", list, Mat("Metal"), true);
        var ban = new List<CombineInstance>();
        AddBox(ban, p + Vector3.up * 7.6f - F[1] * 0.42f, new Vector3(22f, 1.2f, 0.05f), q);
        AddBox(ban, p + Vector3.up * 7.6f + F[1] * 0.42f, new Vector3(22f, 1.2f, 0.05f), q);
        var b = Combine("Banner", ban, Mat("Checker"), false);
        b.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void BuildGrandstands()
    {
        var concrete = new List<CombineInstance>();
        var seats = new List<CombineInstance>();
        var roofs = new List<CombineInstance>();
        for (int seg = 0; seg < 4; seg++)
        {
            int i = 25 + seg * 22;
            if (i >= Count) break;
            Vector3 p = P[i]; Vector3 r = R[i]; Quaternion q = Quaternion.LookRotation(F[i]);
            float baseLat = -(BarrierLat + 5f);
            // raised base wall
            Vector3 bw = p + r * (baseLat + 0.5f); bw.y = p.y + 0.6f;
            AddBox(concrete, bw, new Vector3(1f, 2.2f, 38f), q);
            for (int step = 0; step < 9; step++)
            {
                float lat = baseLat - 0.6f - step * 1.2f;
                Vector3 pos = p + r * lat; pos.y = p.y + 1.7f + step * 0.75f - 2f;
                AddBox(seats, pos, new Vector3(1.2f, 4f, 36f), q);
            }
            Vector3 back = p + r * (baseLat - 11.6f); back.y = p.y + 5f;
            AddBox(concrete, back, new Vector3(0.6f, 12f, 38f), q);
            for (int k = -1; k <= 1; k += 2)
            {
                Vector3 side = p + r * (baseLat - 6f) + F[i] * (k * 18.7f); side.y = p.y + 4.5f;
                AddBox(concrete, side, new Vector3(11.6f, 11f, 0.6f), q);
                Vector3 col = p + r * (baseLat - 1.5f) + F[i] * (k * 18f); col.y = p.y + 6f;
                AddBox(roofs, col, new Vector3(0.35f, 12f, 0.35f), q);
            }
            Vector3 roof = p + r * (baseLat - 6.5f); roof.y = p.y + 12.2f;
            AddBox(roofs, roof, new Vector3(14f, 0.35f, 40f), q * Quaternion.Euler(0, 0, -7f));
        }
        Combine("StandConcrete", concrete, Mat("Concrete"), true);
        Combine("StandSeats", seats, Mat("Crowd"), true);
        Combine("StandRoof", roofs, Mat("Metal"), false);

        // pit building on the right side of the straight: concrete + glass band
        int pi = 60;
        Quaternion pq = Quaternion.LookRotation(F[pi]);
        Vector3 pb = P[pi] + R[pi] * (BarrierLat + 14f);
        var pit = new List<CombineInstance>();
        var glass = new List<CombineInstance>();
        Vector3 b0 = pb; b0.y = P[pi].y + 2.2f;
        AddBox(pit, b0, new Vector3(12f, 4.4f, 120f), pq);
        Vector3 g1 = pb - R[pi] * 0.5f; g1.y = P[pi].y + 6.3f;
        AddBox(glass, g1, new Vector3(11f, 3.8f, 119f), pq);
        Vector3 rf = pb; rf.y = P[pi].y + 8.4f;
        AddBox(pit, rf, new Vector3(14f, 0.5f, 122f), pq);
        Combine("PitBuilding", pit, Mat("Concrete"), true);
        Combine("PitGlass", glass, Mat("Glass"), false);
    }

    // Landscape generated in the editor by WorldBuilder (same size/origin constants).
    public const float TerrainSize = 3600f, TerrainBaseY = -20f;

    void BuildTerrain()
    {
        var td = Resources.Load<TerrainData>("Terrain/TrackTerrain");
        if (td == null) { Debug.LogWarning("TrackTerrain missing - run Drift Rivals/1. Setup Project"); return; }
        var g = Terrain.CreateTerrainGameObject(td);
        g.name = "Terrain";
        g.transform.position = new Vector3(Center.x - TerrainSize / 2, TerrainBaseY, Center.z - TerrainSize / 2);
        var t = g.GetComponent<Terrain>();
        t.materialTemplate = Mat("Terrain");
        t.drawInstanced = true;
        t.heightmapPixelError = 3f;
        t.basemapDistance = 700f;
        t.treeDistance = 1200f;
        t.treeBillboardDistance = 5000f;
        t.treeMaximumFullLODCount = 10000;
        t.detailObjectDistance = 100f;
        t.detailObjectDensity = 1f;
        t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        g.AddComponent<Surface>().grip = 0.5f;
    }

    public static Track BuildData()
    {
        var go = new GameObject("TrackData");
        var t = go.AddComponent<Track>();
        t.Sample();
        return t;
    }

    // ---------- minimap ----------
    public Texture2D MakeMinimap(int size, out Rect worldRect)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in P) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
        float pad = 30f;
        float w = Mathf.Max(maxX - minX, maxZ - minZ) + pad * 2;
        float cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
        worldRect = new Rect(cx - w / 2, cz - w / 2, w, w);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
        for (int i = 0; i < Count; i++)
        {
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = Vector3.Lerp(P[i], P[Wrap(i + 1)], k / 4f);
                int x = (int)((p.x - worldRect.x) / w * size), y = (int)((p.z - worldRect.y) / w * size);
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                        bool edge = Mathf.Abs(dx) == 2 || Mathf.Abs(dy) == 2;
                        var c = edge ? new Color32(20, 20, 20, 200) : new Color32(235, 235, 235, 255);
                        int idx = yy * size + xx;
                        if (!edge || px[idx].a == 0) px[idx] = c;
                    }
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }
}

// Attached to track colliders to give per-surface tyre grip.
public class Surface : MonoBehaviour
{
    public float grip = 1f;
}
