using System.Collections.Generic;
using UnityEngine;

// Displays another player's car from network snapshots (buffered interpolation + short extrapolation).
public class RemoteCar : MonoBehaviour
{
    struct Snap { public double t; public Vector3 p; public Quaternion r; public Vector3 v; public float steer, rpm, thr; }

    const double Delay = 0.1;
    readonly List<Snap> buf = new List<Snap>();
    double offset; bool hasOffset;
    public Rigidbody rb;
    public CarVisual vis;
    public CarAudio audioSrc;
    public CarEffects fx;
    float steer;
    readonly float[] omega = new float[4];
    Vector3 lastVel;

    public void Init(CarVisual v, CarAudio a, CarEffects e)
    {
        vis = v; audioSrc = a; fx = e;
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
    }

    public void Push(float senderTime, NetState s)
    {
        double now = Time.timeAsDouble;
        double off = now - senderTime;
        if (!hasOffset || off < offset) { offset = off; hasOffset = true; }
        else offset += (off - offset) * 0.01;
        double t = senderTime;
        if (buf.Count > 0 && t <= buf[buf.Count - 1].t) return;
        buf.Add(new Snap { t = t, p = s.pos, r = s.rot, v = s.vel, steer = s.steer, rpm = s.rpm, thr = s.throttle });
        if (buf.Count > 30) buf.RemoveAt(0);
        if (fx) for (int i = 0; i < 4; i++) fx.slip[i] = i >= 2 ? s.slip : s.slip * 0.4f;
        if (audioSrc) { audioSrc.rpm = s.rpm; audioSrc.throttle = s.throttle; audioSrc.slip = s.slip; audioSrc.boost = s.boost ? 1 : 0; }
        if (buf.Count == 1) { transform.SetPositionAndRotation(s.pos, s.rot); rb.position = s.pos; rb.rotation = s.rot; }
    }

    void FixedUpdate()
    {
        if (buf.Count == 0) return;
        double rt = Time.timeAsDouble - offset - Delay;
        Vector3 p; Quaternion r;
        int i = buf.Count - 1;
        while (i > 0 && buf[i - 1].t > rt) i--;
        if (rt >= buf[buf.Count - 1].t)
        {
            var last = buf[buf.Count - 1];
            float ex = (float)System.Math.Min(rt - last.t, 0.25);
            p = last.p + last.v * ex; r = last.r; lastVel = last.v; steer = last.steer;
        }
        else if (i == 0) { p = buf[0].p; r = buf[0].r; lastVel = buf[0].v; }
        else
        {
            var a = buf[i - 1]; var b = buf[i];
            float u = (float)((rt - a.t) / System.Math.Max(1e-4, b.t - a.t));
            float dt = (float)(b.t - a.t);
            // cubic Hermite using velocities
            float u2 = u * u, u3 = u2 * u;
            p = (2 * u3 - 3 * u2 + 1) * a.p + (u3 - 2 * u2 + u) * dt * a.v + (-2 * u3 + 3 * u2) * b.p + (u3 - u2) * dt * b.v;
            r = Quaternion.Slerp(a.r, b.r, u);
            lastVel = Vector3.Lerp(a.v, b.v, u);
            steer = Mathf.Lerp(a.steer, b.steer, u);
        }
        rb.MovePosition(p);
        rb.MoveRotation(r);
    }

    void Update()
    {
        if (!vis) return;
        float vLong = Vector3.Dot(lastVel, transform.forward);
        for (int i = 0; i < 4; i++) omega[i] = vLong / vis.wheelRadius;
        vis.Animate(steer, omega, null, Time.deltaTime);
    }
}
