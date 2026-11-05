using UnityEngine;

// Simple racing-line follower with curvature-based speed planning.
public class AIDriver
{
    public float skill;      // multiplies cornering grip estimate
    float lane, laneTarget, laneTimer;
    float stuckTimer;
    int hint = -1;
    readonly System.Random rnd;

    public AIDriver(int seed, float skill)
    {
        rnd = new System.Random(seed);
        this.skill = skill;
        laneTarget = lane = (float)(rnd.NextDouble() * 6 - 3);
    }

    public void Drive(CarController c, Track t, float dt, bool racing)
    {
        Vector3 pos = c.transform.position;
        hint = t.Nearest(pos, hint);
        float myDist = t.DistAlong(pos, hint);
        float speed = c.rb.linearVelocity.magnitude;

        laneTimer -= dt;
        if (laneTimer <= 0) { laneTimer = 4f + (float)rnd.NextDouble() * 5f; laneTarget = (float)(rnd.NextDouble() * 7 - 3.5); }
        lane = Mathf.MoveTowards(lane, laneTarget, dt * 1.2f);

        float look = 9f + speed * 0.5f;
        Vector3 target = t.PointAt(myDist + look, lane);
        Vector3 local = c.transform.InverseTransformPoint(target);
        float ang = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        c.steerIn = Mathf.Clamp(ang / 22f, -1f, 1f);

        // speed planning
        float muG = 8.2f * skill;
        float targetSpeed = 88f;
        for (float d = 5f; d < 40f + speed * 3.2f; d += 8f)
        {
            Vector3 f0 = t.ForwardAt(myDist + d);
            Vector3 f1 = t.ForwardAt(myDist + d + 12f);
            float theta = Vector3.Angle(new Vector3(f0.x, 0, f0.z), new Vector3(f1.x, 0, f1.z)) * Mathf.Deg2Rad;
            float k = theta / 12f;
            if (k < 1e-4f) continue;
            float vc = Mathf.Sqrt(muG / k);
            float allowed = Mathf.Sqrt(vc * vc + 2f * 7f * Mathf.Max(0f, d - 6f));
            targetSpeed = Mathf.Min(targetSpeed, allowed);
        }
        // stay off the walls
        float lat = t.Lateral(pos, hint);
        if (Mathf.Abs(lat) > Track.HalfWidth + 1f) targetSpeed = Mathf.Min(targetSpeed, 25f);

        if (speed < targetSpeed - 1f) { c.throttleIn = 1f; c.brakeIn = 0f; }
        else if (speed > targetSpeed + 2f) { c.throttleIn = 0f; c.brakeIn = Mathf.Clamp01((speed - targetSpeed) / 6f); }
        else { c.throttleIn = 0.4f; c.brakeIn = 0f; }
        c.handbrakeIn = false;
        c.boostIn = racing && Mathf.Abs(ang) < 4f && speed > 25f && speed < targetSpeed - 10f;

        // stuck recovery
        if (racing && speed < 1.5f) stuckTimer += dt; else stuckTimer = 0f;
        if (stuckTimer > 3f) { stuckTimer = 0f; c.NeedsReset = true; }
    }
}
