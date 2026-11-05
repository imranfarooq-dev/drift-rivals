using UnityEngine;

public class ChaseCamera : MonoBehaviour
{
    public Rigidbody target;
    public int mode; // 0 chase, 1 far chase, 2 bumper
    public bool lookBack;
    public float boostKick;
    Camera cam;
    Vector3 camDir = Vector3.forward;
    float shake;

    public static readonly string[] ModeNames = { "Chase", "Far", "Bumper" };

    void Awake() { cam = GetComponent<Camera>(); }

    public void Snap()
    {
        if (!target) return;
        camDir = Flat(target.transform.forward);
        Place(1f);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward; }

    void LateUpdate() { Place(Time.deltaTime); }

    void Place(float dt)
    {
        if (!target) return;
        Transform t = target.transform;
        Vector3 v = target.linearVelocity;
        float spd = v.magnitude;

        if (mode == 3) // test-only side view of the wheels
        {
            transform.position = t.position + t.right * 4.2f + Vector3.up * 0.6f + t.forward * 0.3f;
            transform.rotation = Quaternion.LookRotation(t.position + Vector3.up * 0.35f - transform.position, Vector3.up);
            if (cam) cam.fieldOfView = 50f;
            return;
        }
        if (mode == 2)
        {
            Vector3 p = t.TransformPoint(new Vector3(0, 0.95f, lookBack ? -2.3f : 0.6f));
            transform.position = p;
            transform.rotation = Quaternion.LookRotation(lookBack ? -t.forward : t.forward, t.up);
        }
        else
        {
            Vector3 fwd = Flat(t.forward);
            Vector3 dir = fwd;
            Vector3 fv = Flat(v);
            if (spd > 4f && Vector3.Dot(fv, fwd) > -0.2f) dir = Vector3.Slerp(fwd, fv, 0.4f).normalized;
            camDir = Vector3.Slerp(camDir, dir, 1f - Mathf.Exp(-5f * dt));
            Vector3 d = lookBack ? -camDir : camDir;
            float dist = mode == 0 ? 5.3f : 8.5f;
            float height = mode == 0 ? 1.5f : 2.7f;
            dist += Mathf.Clamp01(spd / 70f) * 0.9f;
            Vector3 focus = t.position + Vector3.up * 1.0f;
            Vector3 desired = focus - d * dist + Vector3.up * (height - 1.0f);
            if (Physics.Linecast(focus, desired, out RaycastHit hit, ~(1 << CarVisual.CarLayer), QueryTriggerInteraction.Ignore))
                desired = hit.point + hit.normal * 0.3f;
            transform.position = dt >= 1f ? desired : Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-14f * dt));
            Vector3 look = focus + d * 3f;
            transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
        }

        // speed shake
        shake = Mathf.Clamp01((spd - 40f) / 40f) * 0.04f + boostKick * 0.05f;
        if (shake > 0.001f)
        {
            float tt = Time.time * 25f;
            transform.position += transform.right * (Mathf.PerlinNoise(tt, 0.3f) - 0.5f) * shake + transform.up * (Mathf.PerlinNoise(0.7f, tt) - 0.5f) * shake;
        }

        if (cam)
        {
            float baseFov = mode == 2 ? 70f : 58f;
            float targetFov = baseFov + Mathf.Clamp01(spd / 85f) * 16f + boostKick * 10f;
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, 1f - Mathf.Exp(-4f * Mathf.Min(dt, 0.1f)));
        }
    }
}
