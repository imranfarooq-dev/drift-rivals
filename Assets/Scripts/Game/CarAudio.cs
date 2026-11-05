using UnityEngine;

// Engine: two real recorded loops (low + high RPM) crossfaded and pitched by RPM,
// with throttle-dependent volume and muffling (on/off load) and a rev-limiter stutter.
// Tyres: synthesized squeal driven by slip.
public class CarAudio : MonoBehaviour
{
    public volatile float rpm = 900f, throttle, slip, boost;

    // RPM each recording was made at (measured from the loops' base frequency).
    const float LowRef = 2000f, HighRef = 3360f;

    AudioSource low, high, tyre;
    AudioLowPassFilter lowLp, highLp;
    float thrSmooth, rpmSmooth = 900f;
    bool isLocal;

    // tyre synth state (audio thread)
    int sr;
    uint noiseState = 22222u;
    float bp1, bp2, sqPhase, lfo, slipSmooth;

    public void Init(bool local)
    {
        isLocal = local;
        sr = AudioSettings.outputSampleRate;
        low = Layer("EngineLow", Resources.Load<AudioClip>("Audio/engine_low"), out lowLp);
        high = Layer("EngineHigh", Resources.Load<AudioClip>("Audio/engine_high"), out highLp);

        var tgo = new GameObject("Tyres");
        tgo.transform.SetParent(transform, false);
        tyre = tgo.AddComponent<AudioSource>();
        tyre.clip = AudioClip.Create("tyre", sr, 1, sr, true, TyreRead);
        Setup(tyre, 0.22f);
        tyre.Play();
    }

    AudioSource Layer(string name, AudioClip clip, out AudioLowPassFilter lp)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        Setup(s, 0f);
        lp = go.AddComponent<AudioLowPassFilter>();
        lp.cutoffFrequency = 6000f;
        lp.lowpassResonanceQ = 1f;
        if (clip) { s.time = Random.Range(0f, clip.length); s.Play(); }
        return s;
    }

    void Setup(AudioSource s, float vol)
    {
        s.loop = true;
        s.volume = vol;
        s.spatialBlend = isLocal ? 0f : 1f;
        s.dopplerLevel = isLocal ? 0f : 0.5f;
        s.minDistance = 7f;
        s.maxDistance = 300f;
        s.rolloffMode = AudioRolloffMode.Logarithmic;
    }

    void Update()
    {
        if (low == null) return;
        float dt = Time.deltaTime;
        rpmSmooth = Mathf.Lerp(rpmSmooth, rpm, 1f - Mathf.Exp(-18f * dt));
        thrSmooth = Mathf.Lerp(thrSmooth, Mathf.Clamp01(throttle), 1f - Mathf.Exp(-10f * dt));

        float r = rpmSmooth;
        // crossfade 3000 -> 5200 rpm (equal power)
        float x = Mathf.Clamp01((r - 3000f) / 2200f);
        float gLow = Mathf.Cos(x * Mathf.PI * 0.5f);
        float gHigh = Mathf.Sin(x * Mathf.PI * 0.5f);

        float load = Mathf.Lerp(0.5f, 1f, thrSmooth);           // off-throttle is quieter
        float rev = Mathf.Lerp(0.75f, 1f, Mathf.Clamp01(r / 7000f)); // louder as it revs
        float limiter = (r > CarController.Redline - 120f && thrSmooth > 0.5f && Mathf.Repeat(Time.time * 22f, 1f) < 0.4f) ? 0.55f : 1f;
        float master = (isLocal ? 0.62f : 0.9f) * load * rev * limiter;

        low.pitch = Mathf.Clamp(r / LowRef, 0.4f, 3f);
        high.pitch = Mathf.Clamp(r / HighRef, 0.4f, 3f);
        low.volume = master * gLow;
        high.volume = master * gHigh;

        // muffled on overrun, open and raspy under load (nitro opens it fully)
        float cutoff = Mathf.Lerp(1800f, 9000f, thrSmooth) + boost * 6000f;
        lowLp.cutoffFrequency = cutoff;
        highLp.cutoffFrequency = cutoff + 2000f;
    }

    float Noise()
    {
        noiseState ^= noiseState << 13; noiseState ^= noiseState >> 17; noiseState ^= noiseState << 5;
        return (noiseState / (float)uint.MaxValue) * 2f - 1f;
    }

    void TyreRead(float[] data)
    {
        float target = slip;
        float f = 2f * Mathf.Sin(Mathf.PI * 1250f / sr);
        const float q = 0.09f;
        for (int i = 0; i < data.Length; i++)
        {
            slipSmooth += (target - slipSmooth) * 0.0005f;
            lfo += 3.7f / sr;
            float wob = Mathf.Sin(lfo * Mathf.PI * 2f) * 0.6f + Mathf.Sin(lfo * Mathf.PI * 5.3f) * 0.4f;
            sqPhase += (1050f + wob * 60f + slipSmooth * 150f) / sr;
            if (sqPhase > 1f) sqPhase -= 1f;
            float n = Noise();
            float hp = n - bp2 - q * bp1;
            bp1 += f * hp;
            bp2 += f * bp1;
            float tone = Mathf.Sin(sqPhase * Mathf.PI * 2f) * 0.25f + Mathf.Sin(sqPhase * Mathf.PI * 4.01f) * 0.1f + Mathf.Sin(sqPhase * Mathf.PI * 6.03f) * 0.05f;
            float amp = slipSmooth * slipSmooth;
            data[i] = Mathf.Clamp((bp1 * 0.35f + tone * (0.5f + 0.5f * Mathf.Abs(wob))) * amp, -1f, 1f);
        }
    }

    public void SetMuted(bool m)
    {
        if (low) low.mute = m;
        if (high) high.mute = m;
        if (tyre) tyre.mute = m;
    }
}
