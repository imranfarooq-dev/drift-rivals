using UnityEngine;

// Tyre smoke and skid marks. Works for local and remote cars (uses its own ground raycasts).
public class CarEffects : MonoBehaviour
{
    public float[] slip = new float[4];
    CarVisual vis;
    readonly ParticleSystem[] smoke = new ParticleSystem[4];
    readonly int[] lastSkid = { -1, -1, -1, -1 };
    int mask;

    public void Init(CarVisual v)
    {
        vis = v;
        mask = ~(1 << CarVisual.CarLayer);
        for (int i = 2; i < 4; i++) smoke[i] = MakeSmoke();
    }

    ParticleSystem MakeSmoke()
    {
        var go = new GameObject("Smoke");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.duration = 1f; main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.6f);
        main.startSize = new ParticleSystem.MinMaxCurve(1.0f, 2.0f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new Color(0.92f, 0.92f, 0.94f, 0.55f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 700;
        main.gravityModifier = -0.015f;
        var em = ps.emission; em.rateOverTime = 0f;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.25f;
        var sol = ps.sizeOverLifetime; sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 4f));
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.85f, 0.85f, 0.88f), 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.6f, 0.08f), new GradientAlphaKey(0.25f, 0.5f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);
        var rot = ps.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
        var noise = ps.noise; noise.enabled = true; noise.strength = 0.4f; noise.frequency = 0.3f;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = Resources.Load<Material>("Materials/Smoke");
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortMode = ParticleSystemSortMode.Distance;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ps.Play();
        return ps;
    }

    void LateUpdate()
    {
        if (vis == null) return;
        for (int i = 0; i < 4; i++)
        {
            Vector3 wp = vis.WheelWorld(i);
            float s = slip[i];
            bool hitGround = Physics.Raycast(wp + transform.up * 0.2f, -transform.up, out RaycastHit hit, vis.wheelRadius + 0.5f, mask, QueryTriggerInteraction.Ignore);
            if (smoke[i] != null)
            {
                smoke[i].transform.position = (hitGround ? hit.point : wp) + Vector3.up * 0.3f;
                var em = smoke[i].emission;
                em.rateOverTime = hitGround && s > 0.5f ? (s - 0.45f) * 170f : 0f;
            }
            if (hitGround && s > 0.55f && SkidMarks.I != null)
                lastSkid[i] = SkidMarks.I.Add(hit.point, hit.normal, Mathf.Clamp01(s * 1.2f), lastSkid[i], transform.right);
            else lastSkid[i] = -1;
        }
    }
}

// Pooled ring-buffer mesh for all skid marks.
public class SkidMarks : MonoBehaviour
{
    public static SkidMarks I;
    const int Max = 4000;
    const float Width = 0.28f, MinDist = 0.3f;
    struct Pt { public Vector3 pos, l, r; public float a; }
    Pt[] pts = new Pt[Max];
    Vector3[] verts = new Vector3[Max * 4];
    Color32[] cols = new Color32[Max * 4];
    Vector2[] uvs = new Vector2[Max * 4];
    int[] tris = new int[Max * 6];
    int next;
    bool dirty;
    Mesh mesh;

    public static void Create()
    {
        var go = new GameObject("SkidMarks");
        I = go.AddComponent<SkidMarks>();
        I.mesh = new Mesh { name = "Skids", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        I.mesh.MarkDynamic();
        for (int i = 0; i < Max; i++)
        {
            int v = i * 4, t = i * 6;
            I.tris[t] = v; I.tris[t + 1] = v + 2; I.tris[t + 2] = v + 1;
            I.tris[t + 3] = v + 2; I.tris[t + 4] = v + 3; I.tris[t + 5] = v + 1;
            I.uvs[v] = new Vector2(0, 0); I.uvs[v + 1] = new Vector2(1, 0); I.uvs[v + 2] = new Vector2(0, 1); I.uvs[v + 3] = new Vector2(1, 1);
        }
        I.mesh.vertices = I.verts; I.mesh.colors32 = I.cols; I.mesh.uv = I.uvs; I.mesh.triangles = I.tris;
        I.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        go.AddComponent<MeshFilter>().sharedMesh = I.mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = Resources.Load<Material>("Materials/Skid");
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // Returns index to pass back next time (-1 = start new strip).
    public int Add(Vector3 pos, Vector3 normal, float alpha, int last, Vector3 side)
    {
        pos += normal * 0.02f;
        Vector3 s = Vector3.ProjectOnPlane(side, normal).normalized * Width * 0.5f;
        if (last >= 0)
        {
            if ((pts[last].pos - pos).sqrMagnitude < MinDist * MinDist) return last;
        }
        int idx = next;
        next = (next + 1) % Max;
        pts[idx] = new Pt { pos = pos, l = pos - s, r = pos + s, a = alpha };
        int v = idx * 4;
        if (last >= 0)
        {
            var p0 = pts[last];
            verts[v] = p0.l; verts[v + 1] = p0.r; verts[v + 2] = pts[idx].l; verts[v + 3] = pts[idx].r;
            byte a0 = (byte)(p0.a * 200), a1 = (byte)(alpha * 200);
            var c0 = new Color32(10, 10, 10, a0); var c1 = new Color32(10, 10, 10, a1);
            cols[v] = c0; cols[v + 1] = c0; cols[v + 2] = c1; cols[v + 3] = c1;
        }
        else
        {
            verts[v] = verts[v + 1] = verts[v + 2] = verts[v + 3] = Vector3.zero;
        }
        dirty = true;
        return idx;
    }

    void LateUpdate()
    {
        if (!dirty) return;
        dirty = false;
        mesh.vertices = verts;
        mesh.colors32 = cols;
    }
}
