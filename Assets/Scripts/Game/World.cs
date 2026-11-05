using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Sky, sun, fog, reflections and the post-processing stack.
public static class World
{
    public static Camera Cam;
    public static ReflectionProbe CarProbe;

    public static void Build(Vector3 trackCenter)
    {
        Application.targetFrameRate = 120;
        QualitySettings.vSyncCount = 1;
        Time.fixedDeltaTime = 0.01f;
        Physics.defaultSolverIterations = 10;

        // Sky
        var sky = Resources.Load<Material>("Materials/Sky");
        if (sky) RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 1f;
        DynamicGI.UpdateEnvironment();

        // Sun (direction measured from the HDRI by the editor setup)
        var sunGo = new GameObject("Sun");
        var sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.94f, 0.86f);
        sun.intensity = 2.5f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.92f;
        sun.shadowBias = 0.04f;
        sun.shadowNormalBias = 0.3f;
        Vector3 sunDir = new Vector3(0.5f, 0.55f, 0.4f).normalized;
        var txt = Resources.Load<TextAsset>("sun");
        if (txt != null)
        {
            var parts = txt.text.Trim().Split(' ');
            if (parts.Length == 3 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
                && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                sunDir = new Vector3(x, Mathf.Clamp(y, 0.25f, 0.9f), z).normalized;
        }
        sunGo.transform.rotation = Quaternion.LookRotation(-sunDir);
        RenderSettings.sun = sun;

        // Atmospheric haze
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.00032f;
        RenderSettings.fogColor = new Color(0.74f, 0.82f, 0.92f);

        // Static reflection probe for the whole track
        var probeGo = new GameObject("TrackProbe");
        probeGo.transform.position = trackCenter + Vector3.up * 30f;
        var tp = probeGo.AddComponent<ReflectionProbe>();
        tp.mode = ReflectionProbeMode.Realtime;
        tp.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        tp.size = new Vector3(3000, 600, 3000);
        tp.resolution = 256;
        tp.hdr = true;
        tp.importance = 0;
        tp.cullingMask = ~(1 << CarVisual.CarLayer);
        tp.RenderProbe();
        new GameObject("ProbeUpdater").AddComponent<ProbeUpdater>();

        // Moving probe that follows the player's car for live paint reflections
        var cp = new GameObject("CarProbe");
        CarProbe = cp.AddComponent<ReflectionProbe>();
        CarProbe.mode = ReflectionProbeMode.Realtime;
        CarProbe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        CarProbe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
        CarProbe.size = new Vector3(40, 40, 40);
        CarProbe.resolution = 64;
        CarProbe.hdr = true;
        CarProbe.importance = 1;
        CarProbe.farClipPlane = 300;
        CarProbe.cullingMask = ~(1 << CarVisual.CarLayer);

        // Keep 4K/Retina screens fast: render at most ~1440p and upscale with FSR.
        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
        {
            float scale = Mathf.Min(1f, 1440f / Mathf.Max(1, Screen.height));
            urp.renderScale = scale;
            if (scale < 1f) urp.upscalingFilter = UpscalingFilterSelection.FSR;
        }

        // Camera
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        Cam = camGo.AddComponent<Camera>();
        Cam.nearClipPlane = 0.1f;
        Cam.farClipPlane = 4000f;
        Cam.fieldOfView = 60f;
        Cam.allowHDR = true;
        Cam.allowMSAA = true;
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<ChaseCamera>();
        var data = Cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.renderShadows = true;
        data.antialiasing = AntialiasingMode.TemporalAntiAliasing;
        data.dithering = true;

        // Post-processing
        var volGo = new GameObject("PostFX");
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.priority = 1;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        var tm = profile.Add<Tonemapping>(true); tm.mode.value = TonemappingMode.ACES;
        var bl = profile.Add<Bloom>(true); bl.intensity.value = 0.55f; bl.threshold.value = 1.05f; bl.scatter.value = 0.72f;
        var ca = profile.Add<ColorAdjustments>(true); ca.postExposure.value = 0.35f; ca.contrast.value = 10f; ca.saturation.value = -4f;
        ca.colorFilter.value = Color.white; ca.hueShift.value = 0f;
        var vg = profile.Add<Vignette>(true); vg.intensity.value = 0.26f; vg.smoothness.value = 0.45f; vg.color.value = Color.black;
        var mb = profile.Add<MotionBlur>(true); mb.intensity.value = 0.08f; mb.quality.value = MotionBlurQuality.Medium; mb.clamp.value = 0.05f;
        var ch = profile.Add<ChromaticAberration>(true); ch.intensity.value = 0f;
        var wb = profile.Add<WhiteBalance>(true); wb.temperature.value = 4f; wb.tint.value = 0f;
        vol.sharedProfile = profile;
        Chroma = ch;
    }

    public static ChromaticAberration Chroma;
}

// Re-renders the car's reflection probe a few times per second (one face per frame) instead of constantly.
public class ProbeUpdater : MonoBehaviour
{
    float next;
    int pending = -1;
    void Update()
    {
        var p = World.CarProbe;
        if (p == null) return;
        if (pending >= 0 && !p.IsFinishedRendering(pending)) return;
        if (Time.time < next) return;
        next = Time.time + 0.4f;
        pending = p.RenderProbe();
    }
}
