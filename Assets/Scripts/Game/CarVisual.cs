using System.Collections.Generic;
using UnityEngine;

// Loads the car model, auto-aligns it, re-pivots the wheels and handles paint, lights and wheel animation.
// Wheel order: 0 FL, 1 FR, 2 RL, 3 RR.
public class CarVisual : MonoBehaviour
{
    public const int CarLayer = 8;

    public Transform[] steerPivot = new Transform[4];
    public Transform[] spinPivot = new Transform[4];
    public Vector3[] wheelLocal = new Vector3[4];
    public float wheelRadius = 0.34f;
    public Bounds body;
    readonly float[] spin = new float[4];
    readonly List<Material> brakeMats = new List<Material>();

    public static readonly Color[] Palette =
    {
        new Color(0.70f, 0.02f, 0.02f), // carmine
        new Color(0.02f, 0.18f, 0.75f), // racing blue
        new Color(0.85f, 0.85f, 0.85f), // pearl white
        new Color(0.95f, 0.35f, 0.02f), // orange
        new Color(0.30f, 0.80f, 0.05f), // lime
        new Color(0.03f, 0.03f, 0.03f), // black
        new Color(0.95f, 0.75f, 0.02f), // yellow
        new Color(0.40f, 0.05f, 0.65f), // purple
    };
    public static readonly string[] PaletteNames = { "Carmine", "Racing Blue", "Pearl", "Orange", "Lime", "Black", "Yellow", "Purple" };

    static readonly string[] WheelNames = { "WheelFrontL", "WheelFrontR", "WheelRearL", "WheelRearR" };

    public static CarVisual Create(GameObject root, int colorIndex)
    {
        var vis = root.AddComponent<CarVisual>();
        var prefab = Resources.Load<GameObject>("Models/CarConcept");
        if (prefab != null && vis.BuildFromModel(prefab)) { }
        else vis.BuildFallback();
        vis.Paint(Palette[Mathf.Abs(colorIndex) % Palette.Length]);
        SetLayer(root.transform, CarLayer);
        return vis;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        foreach (Transform c in t) SetLayer(c, layer);
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t) { var r = FindDeep(c, name); if (r) return r; }
        return null;
    }

    static bool BoundsOf(Transform t, out Bounds b)
    {
        b = new Bounds(); bool has = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>())
        {
            if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
        }
        return has;
    }

    bool BuildFromModel(GameObject prefab)
    {
        // Root must be at identity while we measure.
        Vector3 rp = transform.position; Quaternion rr = transform.rotation;
        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        var model = Instantiate(prefab);
        model.name = "Model";
        model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        var wheels = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            wheels[i] = FindDeep(model.transform, WheelNames[i]);
            if (wheels[i] == null) { Destroy(model); transform.SetPositionAndRotation(rp, rr); return false; }
        }

        // The model ships in a showroom pose with the front wheels steered ~30 degrees.
        // Straighten them by copying the rear wheel orientation on the same side.
        wheels[0].rotation = wheels[2].rotation;
        wheels[1].rotation = wheels[3].rotation;

        // Wheel node origins are the exact hub centres (better than mesh bounds, which wobble when spinning).
        Vector3[] c = new Vector3[4];
        void Measure() { for (int i = 0; i < 4; i++) c[i] = wheels[i].position; }
        Measure();

        Vector3 fwd = ((c[0] + c[1]) - (c[2] + c[3])).normalized;
        Vector3 right = Vector3.ProjectOnPlane(c[1] - c[0], fwd).normalized;
        Vector3 up = Vector3.Cross(fwd, right);
        BoundsOf(model.transform, out var all);
        Vector3 wc = (c[0] + c[1] + c[2] + c[3]) / 4f;
        if (Vector3.Dot(all.center - wc, up) < 0) { right = -right; up = -up; }

        model.transform.rotation = Quaternion.Inverse(Quaternion.LookRotation(fwd, up));
        Measure();

        float wheelbase = Vector3.Distance((c[0] + c[1]) / 2, (c[2] + c[3]) / 2);
        if (wheelbase < 1.5f || wheelbase > 4.5f)
        {
            model.transform.localScale *= 2.7f / Mathf.Max(0.01f, wheelbase);
            Measure();
        }
        wc = (c[0] + c[1] + c[2] + c[3]) / 4f;
        model.transform.position -= wc;
        Measure();

        BoundsOf(wheels[0], out var wb);
        wheelRadius = Mathf.Clamp(wb.extents.y, 0.2f, 0.6f);

        model.transform.SetParent(transform, false);

        // Assign by geometry so FL is really front-left.
        var order = new int[4];
        for (int i = 0; i < 4; i++)
        {
            bool front = c[i].z > 0, left = c[i].x < 0;
            order[i] = (front ? 0 : 2) + (left ? 0 : 1);
        }

        for (int i = 0; i < 4; i++)
        {
            int slot = order[i];
            var steer = new GameObject("WheelSteer" + slot).transform;
            steer.SetParent(transform, false);
            steer.localPosition = c[i];
            var sp = new GameObject("WheelSpin" + slot).transform;
            sp.SetParent(steer, false);
            var brakeParts = new List<Transform>();
            foreach (Transform ch in wheels[i]) if (ch.name.Contains("BrakePad")) brakeParts.Add(ch);
            wheels[i].SetParent(sp, true);
            foreach (var bp in brakeParts) bp.SetParent(steer, true);
            steerPivot[slot] = steer; spinPivot[slot] = sp; wheelLocal[slot] = c[i];
        }

        // Body bounds (exclude wheels)
        bool has = false;
        foreach (var r in model.GetComponentsInChildren<Renderer>())
        {
            if (!has) { body = r.bounds; has = true; } else body.Encapsulate(r.bounds);
        }

        // Materials: lights
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            var mats = r.materials;
            for (int m = 0; m < mats.Length; m++)
            {
                string n = mats[m].name;
                if (n.Contains("Brakelight")) brakeMats.Add(mats[m]);
                if (n.Contains("Headlight")) SetEmission(mats[m], new Color(1f, 0.95f, 0.85f) * 3f);
            }
        }

        transform.SetPositionAndRotation(rp, rr);
        return true;
    }

    void BuildFallback()
    {
        var mat = Resources.Load<Material>("Materials/Concrete");
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(b.GetComponent<Collider>());
        b.name = "Body"; b.transform.SetParent(transform, false);
        b.transform.localPosition = new Vector3(0, 0.35f, 0);
        b.transform.localScale = new Vector3(1.9f, 0.8f, 4.4f);
        b.GetComponent<Renderer>().material.name = "Paint 1";
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2, left = i % 2 == 0;
            var steer = new GameObject("WheelSteer" + i).transform;
            steer.SetParent(transform, false);
            steer.localPosition = new Vector3(left ? -0.85f : 0.85f, 0, front ? 1.35f : -1.35f);
            var sp = new GameObject("WheelSpin" + i).transform; sp.SetParent(steer, false);
            var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(w.GetComponent<Collider>());
            w.transform.SetParent(sp, false);
            w.transform.localRotation = Quaternion.Euler(0, 0, 90);
            w.transform.localScale = new Vector3(0.68f, 0.13f, 0.68f);
            if (mat) w.GetComponent<Renderer>().sharedMaterial = mat;
            steerPivot[i] = steer; spinPivot[i] = sp; wheelLocal[i] = steer.localPosition;
        }
        wheelRadius = 0.34f;
        body = new Bounds(new Vector3(0, 0.35f, 0), new Vector3(1.9f, 0.8f, 4.4f));
    }

    static void SetColor(Material m, string prop, Color c) { if (m.HasProperty(prop)) m.SetColor(prop, c); }

    static void SetEmission(Material m, Color c)
    {
        SetColor(m, "emissiveFactor", c);
        SetColor(m, "_EmissionColor", c);
        m.EnableKeyword("_EMISSION");
    }

    public void Paint(Color c)
    {
        foreach (var r in GetComponentsInChildren<Renderer>())
            foreach (var m in r.materials)
                if (m.name.Contains("Paint 1"))
                {
                    SetColor(m, "baseColorFactor", c);
                    SetColor(m, "_BaseColor", c);
                    SetColor(m, "_Color", c);
                }
    }

    public void SetBrakeLights(bool on)
    {
        var col = on ? new Color(1f, 0.05f, 0.02f) * 6f : new Color(0.5f, 0.02f, 0.01f) * 0.6f;
        foreach (var m in brakeMats) SetEmission(m, col);
    }

    // steerDeg: front wheel steer angle. omega: wheel angular speed (rad/s). susp: vertical offset of each wheel.
    public void Animate(float steerDeg, float[] omega, float[] susp, float dt)
    {
        for (int i = 0; i < 4; i++)
        {
            if (steerPivot[i] == null) continue;
            // cap visible rotation per frame (~28 deg) so fast wheels don't strobe / look like they spin backwards
            float step = Mathf.Clamp(omega[i] * Mathf.Rad2Deg * dt, -28f, 28f);
            spin[i] = Mathf.Repeat(spin[i] + step, 360f);
            float s = i < 2 ? steerDeg : 0f;
            steerPivot[i].localRotation = Quaternion.Euler(0, s, 0);
            steerPivot[i].localPosition = wheelLocal[i] + Vector3.up * (susp != null ? susp[i] : 0f);
            spinPivot[i].localRotation = Quaternion.Euler(spin[i], 0, 0);
        }
    }

    public Vector3 WheelWorld(int i) => steerPivot[i] ? steerPivot[i].position : transform.position;
}
