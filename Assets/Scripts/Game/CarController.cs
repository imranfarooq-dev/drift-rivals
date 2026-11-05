using System.Collections.Generic;
using UnityEngine;

// Raycast-suspension car with a slip-angle tyre model, rear-wheel drive, auto gearbox,
// friction circle (power oversteer), handbrake drifts, drift assist and nitro.
[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    // Inputs
    [HideInInspector] public float throttleIn, brakeIn, steerIn;
    [HideInInspector] public bool handbrakeIn, boostIn;
    public bool locked = true;

    // Tuning
    public float mass = 1350f;
    public float springK = 42000f;
    public float damperC = 3800f;
    public float travel = 0.24f;
    public float frontGrip = 1.15f, rearGrip = 1.28f;
    public float handbrakeGripMul = 0.42f;
    public float driftAssist = 0.5f;
    public float tractionSlip = 0.12f; // how much wheelspin traction control allows (AI uses less)
    public float peakTorque = 560f;
    public float finalDrive = 3.7f;
    readonly float[] ratios = { 3.3f, 2.35f, 1.78f, 1.39f, 1.12f, 0.93f };
    public const float Redline = 7800f, Idle = 900f;

    // State (read by visuals / audio / net / HUD)
    public Rigidbody rb;
    public CarVisual vis;
    public float steerAngle;
    public int gear = 1;
    public float rpm = Idle;
    public float boost = 1f; // nitro tank 0..1
    public bool boosting;
    public readonly float[] omega = new float[4];
    public readonly float[] slip = new float[4];
    public readonly float[] susp = new float[4];
    public readonly bool[] grounded = new bool[4];
    public float onGrass;
    public float driftAngle;  // body slip angle, degrees
    public bool driftMode;    // true only after you start a drift with the handbrake
    float driftExitTimer;
    public float SpeedKmh => rb ? rb.linearVelocity.magnitude * 3.6f : 0f;
    public float ForwardSpeed => rb ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;

    readonly float[] comp = new float[4], prevComp = new float[4];
    float topOffset;
    float shiftTimer, reverseTimer, flipTimer;
    float spinExtra;
    int groundMask;
    static readonly Dictionary<int, float> gripCache = new Dictionary<int, float>();

    public void Init(CarVisual v)
    {
        vis = v;
        rb = GetComponent<Rigidbody>();
        rb.mass = mass;
        rb.linearDamping = 0f;
        rb.angularDamping = 0.05f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.centerOfMass = new Vector3(0, 0.08f, 0.08f);
        rb.inertiaTensor = new Vector3(2470f, 2650f, 560f);
        rb.inertiaTensorRotation = Quaternion.identity;
        topOffset = travel - (mass * 9.81f / 4f) / springK;
        groundMask = ~(1 << CarVisual.CarLayer);
    }

    float SurfaceGrip(Collider c)
    {
        int id = c.GetInstanceID();
        if (!gripCache.TryGetValue(id, out float g))
        {
            var s = c.GetComponent<Surface>();
            g = s ? s.grip : 1f;
            gripCache[id] = g;
        }
        return g;
    }

    float GearRatio => gear < 0 ? 3.2f : ratios[Mathf.Clamp(gear - 1, 0, ratios.Length - 1)];

    float EngineTorque(float r)
    {
        float x = r / Redline;
        float t = peakTorque * Mathf.Clamp01(0.55f + 0.95f * x - 0.6f * x * x);
        return r >= Redline ? 0f : t;
    }

    void FixedUpdate()
    {
        if (rb == null) return;
        float dt = Time.fixedDeltaTime;
        Vector3 up = transform.up;
        Vector3 vel = rb.linearVelocity;
        float speed = vel.magnitude;
        float vLongBody = Vector3.Dot(vel, transform.forward);

        float thr = locked ? 0f : Mathf.Clamp01(throttleIn);
        float brk = locked ? 1f : Mathf.Clamp01(brakeIn);
        bool hb = locked || handbrakeIn;

        // ---- gearbox (auto) + reverse ----
        if (!locked)
        {
            if (gear > 0 && brk > 0.5f && thr < 0.1f && vLongBody < 1f) reverseTimer += dt; else reverseTimer = 0;
            if (gear > 0 && reverseTimer > 0.35f) gear = -1;
            if (gear < 0 && thr > 0.1f && vLongBody > -1.5f) gear = 1;
        }
        if (gear < 0) { float t = thr; thr = brk; brk = t; }

        // ---- body slip angle ----
        Vector3 flatV = Vector3.ProjectOnPlane(vel, up);
        driftAngle = flatV.magnitude > 4f ? Vector3.SignedAngle(transform.forward, flatV, up) : 0f;
        if (gear < 0) driftAngle = 0;

        // Drift mode: started by the handbrake at speed, ends once the car straightens out.
        if (!locked && handbrakeIn && Mathf.Abs(vLongBody) > 8f) { driftMode = true; driftExitTimer = 0f; }
        if (driftMode)
        {
            if (Mathf.Abs(driftAngle) < 6f || Mathf.Abs(vLongBody) < 5f) driftExitTimer += dt; else driftExitTimer = 0f;
            if (driftExitTimer > 0.4f) driftMode = false;
        }

        // ---- steering ----
        float maxSteer = Mathf.Lerp(36f, 9f, Mathf.Clamp01(Mathf.Abs(vLongBody) / 55f));
        float assist = (driftMode && vLongBody > 5f) ? Mathf.Clamp(driftAngle * driftAssist, -30f, 30f) : 0f;
        float target = Mathf.Clamp(steerIn * maxSteer + assist, -42f, 42f);
        steerAngle = Mathf.MoveTowards(steerAngle, target, 200f * dt);

        // ---- engine rpm ----
        float rearOmega = (omega[2] + omega[3]) * 0.5f;
        float wheelRpm = Mathf.Abs(vLongBody) / vis.wheelRadius * GearRatio * finalDrive * 60f / (2f * Mathf.PI);
        float clutchRpm = (gear == 1 || gear < 0) ? Mathf.Lerp(Idle, 4200f, thr) : Idle;
        float targetRpm = Mathf.Max(wheelRpm, clutchRpm) + spinExtra * 2500f;
        rpm = Mathf.Clamp(Mathf.Lerp(rpm, targetRpm, 1f - Mathf.Exp(-14f * dt)), Idle, Redline + 150f);

        shiftTimer -= dt;
        if (gear > 0 && shiftTimer <= 0f)
        {
            if (rpm > 7350f && gear < ratios.Length && Mathf.Abs(vLongBody) > 3f) { gear++; shiftTimer = 0.3f; }
            else if (rpm < 3300f && gear > 1) { gear--; shiftTimer = 0.3f; }
        }

        // ---- nitro ----
        boosting = boostIn && !locked && boost > 0.01f && thr > 0.2f && gear > 0;
        if (boosting) boost = Mathf.Max(0, boost - dt / 4.5f);
        float torque = shiftTimer > 0.15f ? 0f : EngineTorque(rpm) * (boosting ? 1.55f : 1f);
        float driveTotal = gear == 0 ? 0 : torque * GearRatio * finalDrive * 0.88f / vis.wheelRadius * thr;
        if (boosting) rb.AddForce(transform.forward * mass * 2.5f, ForceMode.Force);

        // traction control: limits wheelspin, but lets you power-slide once a drift is going
        float rearGripForce = rearGrip * mass * 9.81f * 0.5f;
        bool powerDrift = driftMode || hb;
        float allowed = rearGripForce * (powerDrift ? 1.5f : (1f + tractionSlip));
        if (gear > 0 && driveTotal > allowed) driveTotal = allowed;

        // ---- wheels ----
        float staticLoad = mass * 9.81f / 4f;
        int groundedCount = 0;
        float maxRearSlip = 0f;
        float spinAccum = 0f;
        onGrass = 0;
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            Vector3 top = transform.TransformPoint(vis.wheelLocal[i] + Vector3.up * topOffset);
            float rayLen = travel + vis.wheelRadius;
            grounded[i] = Physics.Raycast(top, -up, out RaycastHit hit, rayLen, groundMask, QueryTriggerInteraction.Ignore);
            if (!grounded[i])
            {
                comp[i] = 0; prevComp[i] = 0;
                susp[i] = topOffset - travel;
                omega[i] = Mathf.MoveTowards(omega[i], 0, 5f * dt);
                slip[i] = 0;
                continue;
            }
            groundedCount++;
            comp[i] = rayLen - hit.distance;
            float compVel = (comp[i] - prevComp[i]) / dt;
            prevComp[i] = comp[i];
            float fs = springK * Mathf.Min(comp[i], travel) + damperC * compVel;
            if (comp[i] > travel) fs += (comp[i] - travel) * springK * 6f;
            fs = Mathf.Max(0f, fs);
            rb.AddForceAtPosition(up * fs, top);
            susp[i] = topOffset - (travel - Mathf.Min(comp[i], travel));

            float load = Mathf.Clamp(fs, 0f, staticLoad * 3f);
            float sGrip = SurfaceGrip(hit.collider);
            if (sGrip < 0.9f) onGrass += 0.25f;

            Vector3 n = hit.normal;
            Vector3 wf = front ? Quaternion.AngleAxis(steerAngle, up) * transform.forward : transform.forward;
            wf = Vector3.ProjectOnPlane(wf, n).normalized;
            Vector3 ws = Vector3.Cross(n, wf);
            Vector3 pv = rb.GetPointVelocity(hit.point);
            float vLong = Vector3.Dot(pv, wf);
            float vLat = Vector3.Dot(pv, ws);

            float mu = (front ? frontGrip : rearGrip) * sGrip;
            if (!front && hb) mu *= handbrakeGripMul;
            float maxF = mu * load;

            // lateral: slip-angle curve, capped "kill sideways velocity" force
            float alpha = Mathf.Atan2(Mathf.Abs(vLat), Mathf.Max(Mathf.Abs(vLong), 0.5f)) * Mathf.Rad2Deg;
            float curve = alpha < 6f ? alpha / 6f : Mathf.Lerp(1f, driftMode && !front ? 0.85f : 0.92f, (alpha - 6f) / 30f);
            float latCap = maxF * curve;
            float fy = Mathf.Clamp(-vLat * (mass / 4f) / dt, -latCap, latCap);

            // longitudinal
            float fx = 0f;
            if (!front) fx += driveTotal * 0.5f * Mathf.Sign(gear);
            float brakeF = brk * (front ? 9500f : 6500f) + (!front && hb ? 7000f : 0f);
            if (Mathf.Abs(vLong) < 0.6f) fx += Mathf.Clamp(-vLong * (mass / 4f) / dt, -brakeF, brakeF);
            else fx -= Mathf.Sign(vLong) * brakeF;
            fx -= vLong * 12f; // rolling resistance

            // friction circle -> wheelspin / lock steals lateral grip
            float mag = Mathf.Sqrt(fx * fx + fy * fy);
            float excess = 0f;
            if (mag > maxF && mag > 1f)
            {
                excess = (mag - maxF) / maxF;
                float s = maxF / mag;
                fx *= s; fy *= s;
            }

            Vector3 at = hit.point + up * 0.3f;
            rb.AddForceAtPosition(wf * fx + ws * fy, at);

            // wheel spin for visuals / rpm
            float wOmega = vLong / vis.wheelRadius;
            if (!front)
            {
                if (hb) wOmega = 0f;
                else if (thr > 0.3f && excess > 0.05f) { wOmega += Mathf.Sign(gear) * excess * 40f; spinAccum += excess; }
            }
            omega[i] = wOmega;

            float sl = 0f;
            if (Mathf.Abs(vLong) > 2f || Mathf.Abs(vLat) > 2f)
            {
                sl = Mathf.Clamp01((alpha - 14f) / 20f);
                if (!front && hb && Mathf.Abs(vLong) > 3f) sl = Mathf.Max(sl, 0.7f);
                if (!front && excess > 0.45f && thr > 0.3f) sl = Mathf.Max(sl, Mathf.Clamp01(excess - 0.35f));
            }
            if (Mathf.Abs(vLong) < 1f && !front && excess > 0.2f && thr > 0.5f) sl = Mathf.Max(sl, 0.9f); // burnout
            if (front && brk > 0.8f && Mathf.Abs(vLong) > 15f && excess > 0.1f) sl = Mathf.Max(sl, 0.5f);
            slip[i] = sl * (sGrip < 0.9f ? 0.3f : 1f);
            if (!front) maxRearSlip = Mathf.Max(maxRearSlip, alpha);
        }
        spinExtra = Mathf.Lerp(spinExtra, Mathf.Clamp01(spinAccum * 0.5f), 1f - Mathf.Exp(-8f * dt));

        // ---- anti-roll bars ----
        AntiRoll(0, 1, 22000f);
        AntiRoll(2, 3, 15000f);

        // ---- aero ----
        rb.AddForce(-vel * speed * 0.40f);
        rb.AddForce(-up * speed * speed * 1.6f);

        // ---- drift helpers ----
        float absDrift = Mathf.Abs(driftAngle);
        if (driftMode && groundedCount >= 3 && absDrift > 10f && absDrift < 100f && speed > 8f && gear > 0)
        {
            // keep momentum through the drift and refill nitro
            Vector3 dir = flatV.normalized;
            rb.AddForce(dir * mass * 2.2f * thr);
            boost = Mathf.Min(1f, boost + dt * 0.07f * Mathf.Clamp01(absDrift / 30f));
        }
        // yaw-rate limiter so spins stay catchable
        Vector3 av = rb.angularVelocity;
        float yaw = Vector3.Dot(av, up);
        float yawMax = driftMode ? 2.2f : 1.6f;
        if (Mathf.Abs(yaw) > yawMax) rb.AddTorque(-up * (yaw - Mathf.Sign(yaw) * yawMax) * 2650f * 8f);

        // drift angle holder: lets the car slide up to ~35 degrees, then pulls it back so drifts don't become spins
        if (driftMode && groundedCount >= 3 && speed > 5f)
        {
            float a = Mathf.Abs(driftAngle);
            float sgn = Mathf.Sign(driftAngle);
            if (a > 30f) rb.AddTorque(up * sgn * (a - 30f) * 2650f * 0.15f);
            if (Mathf.Sign(yaw) == -sgn && a > 15f) rb.AddTorque(-up * yaw * 2650f * 2.5f * Mathf.Clamp01((a - 15f) / 20f));
            if (a > 75f) driftMode = false; // full spin: hand back to stability control
        }

        // stability control (off while drifting): stops the car rotating more than the steering asks for
        if (!driftMode && groundedCount >= 3 && vLongBody > 4f)
        {
            float wheelbase = Mathf.Max(1.5f, vis.wheelLocal[0].z - vis.wheelLocal[2].z);
            float yawWanted = vLongBody * Mathf.Tan(steerAngle * Mathf.Deg2Rad) / wheelbase;
            float over = yaw - yawWanted;
            bool oversteer = Mathf.Abs(yaw) > Mathf.Abs(yawWanted) + 0.1f || Mathf.Sign(yaw) != Mathf.Sign(yawWanted);
            if (oversteer && Mathf.Abs(driftAngle) > 3f)
                rb.AddTorque(-up * over * 2650f * 6f);
            // bleed off sideways slide
            if (Mathf.Abs(driftAngle) > 4f)
            {
                Vector3 side = Vector3.Cross(up, flatV.normalized);
                float lat = Vector3.Dot(vel, transform.right);
                rb.AddForce(-transform.right * lat * mass * 1.5f);
            }
        }

        // ---- air control ----
        if (groundedCount == 0)
        {
            Vector3 tilt = Vector3.Cross(up, Vector3.up);
            rb.AddTorque(tilt * 6000f - av * 400f);
        }

        // ---- auto-recover when flipped ----
        if (Vector3.Dot(up, Vector3.up) < 0.25f && speed < 4f) flipTimer += dt; else flipTimer = 0f;
        if (flipTimer > 2f) { flipTimer = 0; NeedsReset = true; }
    }

    public bool NeedsReset;

    void AntiRoll(int l, int r, float k)
    {
        if (!grounded[l] && !grounded[r]) return;
        float diff = (comp[l] - comp[r]) * k;
        Vector3 up = transform.up;
        if (grounded[l]) rb.AddForceAtPosition(-up * diff, transform.TransformPoint(vis.wheelLocal[l]));
        if (grounded[r]) rb.AddForceAtPosition(up * diff, transform.TransformPoint(vis.wheelLocal[r]));
    }

    void Update()
    {
        if (vis == null) return;
        vis.Animate(steerAngle, omega, susp, Time.deltaTime);
        vis.SetBrakeLights(!locked && (brakeIn > 0.1f && gear > 0 || handbrakeIn));
    }

    public void Teleport(Vector3 pos, Quaternion rot)
    {
        rb.position = pos; rb.rotation = rot;
        transform.SetPositionAndRotation(pos, rot);
        rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
        gear = 1; rpm = Idle; steerAngle = 0;
        for (int i = 0; i < 4; i++) { comp[i] = prevComp[i] = 0; omega[i] = 0; }
    }
}
