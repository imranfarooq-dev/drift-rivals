using System.Collections.Generic;
using System.IO;
using System.Net;
using UnityEngine;

public enum Phase : byte { Menu = 0, Lobby = 1, Countdown = 2, Racing = 3, Results = 4 }
public enum Mode { None, Solo, Host, Client }

public class Racer
{
    public byte id;
    public string name;
    public int color;
    public bool bot;
    public bool inRace;
    public bool owned;           // simulated on this machine
    public IPEndPoint ep;        // host-side: client's address
    public float lastHeard;

    public GameObject go;
    public CarController car;
    public RemoteCar remote;
    public CarVisual vis;
    public CarEffects fx;
    public CarAudio au;
    public AIDriver ai;

    public int lap;
    public float dist;
    public float finish = -1f;
    public bool halfway = true;
    public int hint = -1;
    public float lapStart, bestLap = -1f, lastLap = -1f;
    public NetState pending;     // latest network state (host relays it)
    public bool hasPending;

    public float Progress(float len)
    {
        if (finish >= 0) return 1e7f - finish;
        float d = (lap == 0 && dist > len * 0.5f) ? dist - len : dist;
        return lap * len + d;
    }
}

public class GameManager : MonoBehaviour
{
    // ---------- state ----------
    Mode mode = Mode.None;
    Phase phase = Phase.Menu;
    LanNet net;
    Track track;
    ChaseCamera chase;
    readonly List<Racer> racers = new List<Racer>();
    byte myId;
    int laps = 3;
    int botCount = 3;
    string playerName;
    int myColor;
    float greenTime;
    int raceSeq, localSeq = -1;
    float firstFinish = -1f;
    float sendTimer, lobbyTimer, discTimer, joinTimer, joinElapsed;
    readonly Dictionary<string, HostInfo> hosts = new Dictionary<string, HostInfo>();
    string joinIp = "";
    string status = "";
    IPEndPoint hostEp;
    float hostLastHeard;
    bool welcomed;
    bool paused;
    bool muted;
    Texture2D minimap;
    Rect mapWorld;
    GameObject showroomCar;
    float orbit;
    float steerSmooth;
    PhysicsMaterial carPhys;

    // drift scoring (local player)
    float driftCombo, driftTimer, driftTotal, driftFlash;
    string driftPopup = ""; float driftPopupTime;

    static readonly string[] BotNames = { "Kaito", "Vega", "Nova", "Rook", "Blaze", "Mira", "Juno", "Axel", "Sora", "Zed" };

    float RaceTime => Time.time - greenTime;
    bool Authority => mode == Mode.Solo || mode == Mode.Host;
    Racer Me => racers.Find(r => r.id == myId);

    // ---------- boot ----------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<GameManager>() != null) return;
        new GameObject("GameManager").AddComponent<GameManager>();
    }

    void Awake()
    {
        Application.runInBackground = true;
        playerName = PlayerPrefs.GetString("name", "");
        if (string.IsNullOrEmpty(playerName)) playerName = System.Environment.UserName;
        if (string.IsNullOrEmpty(playerName)) playerName = "Driver";
        if (playerName.Length > 16) playerName = playerName.Substring(0, 16);
        myColor = PlayerPrefs.GetInt("color", 0);
        laps = PlayerPrefs.GetInt("laps", 3);
        botCount = PlayerPrefs.GetInt("bots", 3);
        joinIp = PlayerPrefs.GetString("joinIp", "");

        track = Track.Build();
        World.Build(track.Center);
        SkidMarks.Create();
        chase = World.Cam.GetComponent<ChaseCamera>();
        minimap = track.MakeMinimap(256, out mapWorld);
        carPhys = new PhysicsMaterial("Car") { dynamicFriction = 0.15f, staticFriction = 0.15f, bounciness = 0.1f,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Average };
        ShowShowroom(true);

        // Test hooks: -autosolo (AI drives your car), -autohost, -autojoin <ip>
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-cam") autoCam = int.Parse(args[i + 1]);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-autosolo") { StartSolo(); var me = Me; if (me != null) { me.ai = new AIDriver(99, 1f); me.car.tractionSlip = 0.05f; } }
            if (args[i] == "-drifttest") { botCount = 0; StartSolo(); driftTest = true; }
            if (args[i] == "-autohost") { StartHost(); autoStart = true; autoPilot = true; }
            if (args[i] == "-autojoin" && i + 1 < args.Length && IPAddress.TryParse(args[i + 1], out var ip)) { StartJoin(new IPEndPoint(ip, LanNet.GamePort)); autoPilot = true; }
            if (args[i] == "-cam" && i + 1 < args.Length) autoCam = int.Parse(args[i + 1]);
            if (args[i] == "-shots" && i + 1 < args.Length) shotDir = args[i + 1];
            if (args[i] == "-shotint" && i + 1 < args.Length) shotInt = float.Parse(args[i + 1]);
        }
    }

    int autoCam = -1;
    bool autoStart, autoPilot, driftTest;
    float testLog;
    string shotDir;
    int shotIndex;
    float nextShot = 5f, shotInt = 5f;

    void TestShots()
    {
        if (shotDir == null || Time.realtimeSinceStartup < nextShot) return;
        shotIndex++;
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shotDir, "shot" + shotIndex + ".png"));
        var me = Me;
        if (me != null && me.car != null)
            Debug.Log($"[shot{shotIndex}] t={RaceTime:F1} speed={me.car.SpeedKmh:F0} gear={me.car.gear} rpm={me.car.rpm:F0} drift={me.car.driftAngle:F0} lap={me.lap} dist={me.dist:F0} best={me.bestLap:F1} pos={Standings().IndexOf(me) + 1} grass={me.car.onGrass:F1} fps={1f / Time.smoothDeltaTime:F0}");
        nextShot += shotInt;
        if (shotIndex == 4 && autoCam >= 0 && autoCam < 3) chase.mode = (chase.mode + 1) % 3;
        if (shotIndex >= 12) Application.Quit();
    }

    void OnApplicationQuit() { Leave(false); }

    // ---------- showroom (menu background) ----------
    void ShowShowroom(bool on)
    {
        if (showroomCar) Destroy(showroomCar);
        showroomCar = null;
        if (!on) return;
        showroomCar = new GameObject("Showroom");
        CarVisual.Create(showroomCar, myColor);
        track.GridSlot(0, out Vector3 p, out Quaternion q);
        showroomCar.transform.SetPositionAndRotation(p - Vector3.up * 0.25f, q);
        chase.enabled = false;
    }

    // ---------- main loop ----------
    void Update()
    {
        TestShots();
        if (autoStart && mode == Mode.Host && phase == Phase.Lobby && racers.FindAll(r => !r.bot).Count >= 2) { autoStart = false; StartRaceAsHost(); }
        PollNetwork();

        if (Input.GetKeyDown(KeyCode.F)) Screen.fullScreen = !Screen.fullScreen;
        if (Input.GetKeyDown(KeyCode.M)) { muted = !muted; AudioListener.volume = muted ? 0 : 1; }

        if (phase == Phase.Menu || ((phase == Phase.Lobby) && showroomCar))
        {
            if (showroomCar)
            {
                orbit += Time.deltaTime * 9f;
                Vector3 c = showroomCar.transform.position + Vector3.up * 0.6f;
                Vector3 off = Quaternion.Euler(0, orbit, 0) * new Vector3(0, 0.9f, 6.8f);
                World.Cam.transform.position = c + off;
                World.Cam.transform.LookAt(c);
                World.Cam.fieldOfView = 45f;
                World.CarProbe.transform.position = c + Vector3.up;
            }
        }

        if (phase == Phase.Countdown || phase == Phase.Racing || phase == Phase.Results)
            RaceUpdate();

        if (Authority) AuthorityUpdate();
        NetSend();
    }

    void RaceUpdate()
    {
        float dt = Time.deltaTime;
        bool countdown = RaceTime < 0f;
        if (Authority && phase == Phase.Countdown && !countdown) phase = Phase.Racing;

        foreach (var r in racers)
        {
            if (r.go == null) continue;
            if (r.owned && r.car != null)
            {
                var c = r.car;
                c.locked = countdown;
                bool human = !r.bot && r.finish < 0 && r.id == myId;
                if (r.ai != null) r.ai.Drive(c, track, dt, !countdown);
                else if (driftTest && human) ScriptedInput(c);
                else if (human) ReadInput(c);

                if (r.au) { r.au.rpm = c.rpm; r.au.throttle = c.locked ? Mathf.Clamp01(c.throttleIn) : c.throttleIn; r.au.slip = Mathf.Max(Mathf.Max(c.slip[2], c.slip[3]), Mathf.Max(c.slip[0], c.slip[1]) * 0.6f); r.au.boost = c.boosting ? 1 : 0; }
                for (int i = 0; i < 4; i++) r.fx.slip[i] = c.slip[i];

                if (c.NeedsReset || c.transform.position.y < -30f) { c.NeedsReset = false; ResetCar(r); }
                TrackLaps(r);
            }
        }

        // local player extras
        var me = Me;
        if (me != null && me.car != null)
        {
            var c = me.car;
            if (!paused)
            {
                if (Input.GetKeyDown(KeyCode.R) && !countdown) ResetCar(me);
                if (Input.GetKeyDown(KeyCode.V)) chase.mode = (chase.mode + 1) % 3;
                chase.lookBack = Input.GetKey(KeyCode.C);
            }
            chase.boostKick = Mathf.Lerp(chase.boostKick, c.boosting ? 1f : 0f, 1f - Mathf.Exp(-5f * dt));
            if (World.Chroma) World.Chroma.intensity.value = chase.boostKick * 0.25f;
            World.CarProbe.transform.position = c.transform.position + Vector3.up * 1.2f;
            DriftScoring(c, dt);
        }
        if (Input.GetKeyDown(KeyCode.Escape) && phase != Phase.Menu) { paused = !paused; if (mode == Mode.Solo) Time.timeScale = paused ? 0 : 1; }
    }

    void ScriptedInput(CarController c)
    {
        float t = RaceTime;
        c.throttleIn = 1f; c.brakeIn = 0f; c.handbrakeIn = false; c.boostIn = false; c.steerIn = 0f;
        if (t > 4f && t < 5f) c.steerIn = 1f;
        else if (t > 5f && t < 6f) c.steerIn = -1f;
        else if (t > 7.5f && t < 7.9f) { c.handbrakeIn = true; c.steerIn = 1f; }
        else if (t > 7.9f && t < 10f) c.steerIn = 0.3f;
        if (t > 0 && Time.time > testLog)
        {
            testLog = Time.time + 0.25f;
            Debug.Log($"[test] t={t:F2} kmh={c.SpeedKmh:F0} steerIn={c.steerIn:F1} hb={c.handbrakeIn} driftAng={c.driftAngle:F0} mode={c.driftMode} yaw={Vector3.Dot(c.rb.angularVelocity, c.transform.up):F2}");
        }
        if (t > 11f) Application.Quit();
    }

    void DriftScoring(CarController c, float dt)
    {
        float ang = Mathf.Abs(c.driftAngle);
        float spd = c.rb.linearVelocity.magnitude;
        bool drifting = ang > 12f && ang < 110f && spd > 9f && c.grounded[2] && !c.locked && c.onGrass < 0.5f;
        if (drifting)
        {
            driftCombo += dt * ang * spd * 0.12f;
            driftTimer = 1.3f;
        }
        else if (driftTimer > 0)
        {
            driftTimer -= dt;
            if (spd < 5f || c.onGrass > 0.5f) { driftCombo = 0; driftTimer = 0; }
            if (driftTimer <= 0 && driftCombo > 0)
            {
                int pts = Mathf.RoundToInt(driftCombo);
                driftTotal += pts;
                driftPopup = "+" + pts.ToString("N0") + " DRIFT";
                driftPopupTime = Time.time;
                driftCombo = 0;
            }
        }
    }

    void ReadInput(CarController c)
    {
        if (paused) { c.throttleIn = 0; c.brakeIn = 0; c.handbrakeIn = false; c.boostIn = false; return; }
        float h = 0, v = 0;
        try { h = Input.GetAxisRaw("Horizontal"); v = Input.GetAxisRaw("Vertical"); } catch { }
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h = -1;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h = 1;
        if ((Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) && (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))) h = 0;
        bool digital = Mathf.Abs(h) < 0.01f || Mathf.Abs(Mathf.Abs(h) - 1f) < 0.01f;
        float rate = (Mathf.Sign(h) != Mathf.Sign(steerSmooth) || Mathf.Abs(h) < 0.01f) ? 7f : 3.2f;
        steerSmooth = digital ? Mathf.MoveTowards(steerSmooth, h, rate * Time.deltaTime) : h;
        c.steerIn = steerSmooth;
        bool up = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) || v > 0.2f;
        bool down = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) || v < -0.2f;
        c.throttleIn = up ? (v > 0.2f && !Input.GetKey(KeyCode.W) && !Input.GetKey(KeyCode.UpArrow) ? v : 1f) : 0f;
        c.brakeIn = down ? 1f : 0f;
        c.handbrakeIn = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.JoystickButton0) || Input.GetKey(KeyCode.JoystickButton1);
        c.boostIn = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || Input.GetKey(KeyCode.N) || Input.GetKey(KeyCode.JoystickButton2);
    }

    void TrackLaps(Racer r)
    {
        Vector3 pos = r.car.transform.position;
        r.hint = track.Nearest(pos, r.hint);
        float d = track.DistAlong(pos, r.hint);
        float L = track.Length;
        if (d > L * 0.4f && d < L * 0.6f) r.halfway = true;
        if (r.dist > L * 0.8f && d < L * 0.2f)
        {
            if (r.halfway && phase != Phase.Countdown)
            {
                r.lap++;
                r.halfway = false;
                float now = RaceTime;
                if (r.lap >= 2)
                {
                    r.lastLap = now - r.lapStart;
                    if (r.bestLap < 0 || r.lastLap < r.bestLap) r.bestLap = r.lastLap;
                }
                r.lapStart = now;
                if (r.lap > laps && r.finish < 0)
                {
                    r.finish = now;
                    if (r.id == myId && r.ai == null) r.ai = new AIDriver(r.id * 7 + 1, 0.9f); // autopilot after finishing
                }
            }
        }
        else if (r.dist < L * 0.2f && d > L * 0.8f)
        {
            if (r.lap > 0 && r.finish < 0) { r.lap--; r.halfway = true; }
        }
        r.dist = d;
    }

    void ResetCar(Racer r)
    {
        if (r.car == null) return;
        Vector3 pos = r.car.transform.position;
        int i = track.Nearest(pos, r.hint);
        float lat = Mathf.Clamp(track.Lateral(pos, i), -4f, 4f);
        Vector3 p = track.P[i] + track.R[i] * lat + Vector3.up * 0.8f;
        r.car.Teleport(p, Quaternion.LookRotation(track.F[i]));
        if (r.id == myId) { driftCombo = 0; chase.Snap(); }
    }

    List<Racer> Standings()
    {
        var list = racers.FindAll(r => r.inRace && r.go != null);
        float L = track.Length;
        list.Sort((a, b) => b.Progress(L).CompareTo(a.Progress(L)));
        return list;
    }

    void AuthorityUpdate()
    {
        if (phase != Phase.Racing) return;
        var inRace = racers.FindAll(r => r.inRace && r.go != null);
        if (inRace.Count == 0) return;
        bool allDone = true;
        foreach (var r in inRace)
        {
            if (r.finish >= 0) { if (firstFinish < 0) firstFinish = Time.time; }
            else allDone = false;
        }
        var me = Me;
        bool humansDone = true;
        foreach (var r in inRace) if (!r.bot && r.finish < 0) humansDone = false;
        if (allDone || (firstFinish > 0 && Time.time - firstFinish > 45f) || (humansDone && firstFinish > 0 && Time.time - firstFinish > 8f))
        {
            phase = Phase.Results;
            BroadcastLobby();
        }
    }

    // ---------- race setup ----------
    void SpawnRace(float raceTimeNow)
    {
        DespawnCars();
        ShowShowroom(false);
        firstFinish = -1f;
        driftCombo = 0; driftTotal = 0; driftPopup = "";
        paused = false; Time.timeScale = 1;
        var list = racers.FindAll(r => r.inRace);
        list.Sort((a, b) => a.id.CompareTo(b.id));
        // human player first on the grid in solo
        int slot = 0;
        foreach (var r in list)
        {
            r.lap = 0; r.halfway = true; r.finish = -1; r.hint = -1; r.bestLap = -1; r.lastLap = -1; r.lapStart = 0; r.hasPending = false;
            SpawnCar(r, slot++);
            r.dist = track.DistAlong(r.go.transform.position, track.Nearest(r.go.transform.position));
        }
        greenTime = Time.time - raceTimeNow;
        var me = Me;
        if (autoPilot && me != null && me.car != null) { me.ai = new AIDriver(77 + me.id, 1f); me.car.tractionSlip = 0.05f; }
        if (me != null && me.car != null)
        {
            chase.enabled = true;
            chase.target = me.car.rb;
            chase.mode = autoCam >= 0 ? autoCam : PlayerPrefs.GetInt("cam", 0);
            chase.Snap();
        }
        else
        {
            chase.enabled = true;
            var first = list.Count > 0 ? list[0] : null;
            chase.target = first != null && first.go ? first.go.GetComponent<Rigidbody>() : null;
        }
    }

    void SpawnCar(Racer r, int slot)
    {
        track.GridSlot(slot, out Vector3 pos, out Quaternion rot);
        var go = new GameObject("Car_" + r.name);
        var vis = CarVisual.Create(go, r.color);
        var bc = go.AddComponent<BoxCollider>();
        Bounds b = vis.body;
        float bottom = b.min.y + 0.2f;
        bc.center = new Vector3(0, (bottom + b.max.y) * 0.5f, b.center.z);
        bc.size = new Vector3(b.size.x * 0.92f, Mathf.Max(0.3f, b.max.y - bottom), b.size.z * 0.96f);
        bc.sharedMaterial = carPhys;
        go.AddComponent<Rigidbody>();
        var fx = go.AddComponent<CarEffects>(); fx.Init(vis);
        var au = go.AddComponent<CarAudio>(); au.Init(r.owned && !r.bot && r.id == myId);
        r.go = go; r.vis = vis; r.fx = fx; r.au = au;
        if (r.owned)
        {
            var c = go.AddComponent<CarController>();
            c.Init(vis);
            c.Teleport(pos, rot);
            c.locked = true;
            r.car = c;
            r.ai = r.bot ? new AIDriver(r.id * 31 + 7, Random.Range(0.85f, 1.02f)) : null;
            if (r.bot) { c.tractionSlip = 0.05f; c.driftAssist = 0.8f; }
        }
        else
        {
            go.transform.SetPositionAndRotation(pos, rot);
            var rc = go.AddComponent<RemoteCar>();
            rc.Init(vis, au, fx);
            r.remote = rc;
        }
    }

    void DespawnCars()
    {
        foreach (var r in racers)
        {
            if (r.go) Destroy(r.go);
            r.go = null; r.car = null; r.remote = null; r.vis = null; r.fx = null; r.au = null; r.ai = null;
        }
    }

    void RemoveRacer(Racer r)
    {
        if (r.go) Destroy(r.go);
        racers.Remove(r);
    }

    void SyncBots()
    {
        racers.RemoveAll(r => { if (r.bot) { if (r.go) Destroy(r.go); return true; } return false; });
        for (int k = 0; k < botCount; k++)
        {
            racers.Add(new Racer
            {
                id = (byte)(100 + k), name = BotNames[k % BotNames.Length], bot = true, owned = true,
                color = (myColor + 1 + k) % CarVisual.Palette.Length
            });
        }
    }

    // ---------- mode entry ----------
    void StartSolo()
    {
        mode = Mode.Solo;
        racers.Clear();
        myId = 0;
        racers.Add(new Racer { id = 0, name = playerName, color = myColor, owned = true });
        SyncBots();
        foreach (var r in racers) r.inRace = true;
        phase = Phase.Countdown;
        SpawnRace(-4f);
    }

    void StartHost()
    {
        net = new LanNet();
        if (!net.StartHost()) { status = "Could not open port " + LanNet.GamePort + ": " + net.Error; net = null; return; }
        mode = Mode.Host;
        racers.Clear();
        myId = 0;
        racers.Add(new Racer { id = 0, name = playerName, color = myColor, owned = true });
        SyncBots();
        phase = Phase.Lobby;
        status = "";
    }

    void StartJoin(IPEndPoint ep)
    {
        if (net != null) { net.Dispose(); net = null; }
        net = new LanNet();
        if (!net.StartClient()) { status = "Network error: " + net.Error; net = null; return; }
        mode = Mode.Client;
        hostEp = ep;
        welcomed = false;
        joinTimer = 0; joinElapsed = 0;
        hostLastHeard = Time.time;
        racers.Clear();
        phase = Phase.Lobby;
        localSeq = -1;
        status = "Connecting to " + ep.Address + " ...";
    }

    void StartRaceAsHost()
    {
        foreach (var r in racers) r.inRace = true;
        raceSeq++;
        phase = Phase.Countdown;
        SpawnRace(-4f);
        BroadcastLobby();
    }

    void BackToLobby()
    {
        DespawnCars();
        foreach (var r in racers) r.inRace = false;
        phase = mode == Mode.Solo ? Phase.Menu : Phase.Lobby;
        if (mode == Mode.Solo) { mode = Mode.None; racers.Clear(); }
        chase.enabled = false;
        paused = false; Time.timeScale = 1;
        ShowShowroom(true);
        if (mode == Mode.Host) BroadcastLobby();
    }

    void Leave(bool showMenu, string why = "")
    {
        if (net != null)
        {
            var w = LanNet.Begin(LanNet.MLeave);
            var d = LanNet.End(w);
            if (mode == Mode.Host) foreach (var r in racers) { if (r.ep != null) net.Send(d, r.ep); }
            else if (mode == Mode.Client && hostEp != null) net.Send(d, hostEp);
            net.Dispose();
            net = null;
        }
        DespawnCars();
        racers.Clear();
        mode = Mode.None;
        phase = Phase.Menu;
        paused = false; Time.timeScale = 1;
        status = why;
        if (showMenu)
        {
            chase.enabled = false;
            ShowShowroom(true);
        }
    }

    // ---------- networking ----------
    void PollNetwork()
    {
        if (phase == Phase.Menu)
        {
            if (net == null && discoveryNet == null) { discoveryNet = new LanNet(); discoveryNet.StartDiscoveryListen(); }
        }
        else if (discoveryNet != null) { discoveryNet.Dispose(); discoveryNet = null; }

        if (discoveryNet != null)
            while (discoveryNet.Discovered.TryDequeue(out var h)) { h.lastSeen = Time.time; hosts[h.ep.ToString()] = h; }

        if (net == null) return;
        int guard = 0;
        while (net != null && guard++ < 2000 && net.Inbox.TryDequeue(out var pkt))
        {
            try { Handle(pkt); } catch (System.Exception e) { Debug.LogWarning("Bad packet: " + e.Message); }
        }

        if (mode == Mode.Client && net != null)
        {
            if (!welcomed)
            {
                joinTimer -= Time.deltaTime; joinElapsed += Time.deltaTime;
                if (joinTimer <= 0)
                {
                    joinTimer = 0.5f;
                    var w = LanNet.Begin(LanNet.MJoin);
                    w.Write(playerName); w.Write((byte)myColor);
                    net.Send(LanNet.End(w), hostEp);
                }
                if (joinElapsed > 10f) Leave(true, "Couldn't reach the host. Same Wi-Fi? Firewall allowed?");
            }
            else if (Time.time - hostLastHeard > 6f) Leave(true, "Lost connection to host.");
        }
        if (mode == Mode.Host)
        {
            var gone = racers.FindAll(r => r.ep != null && Time.time - r.lastHeard > 6f);
            foreach (var r in gone) { RemoveRacer(r); BroadcastLobby(); }
        }
    }

    LanNet discoveryNet;

    void Handle(Packet pkt)
    {
        byte type = pkt.data[4];
        var br = new BinaryReader(new MemoryStream(pkt.data, 5, pkt.data.Length - 5));
        if (mode == Mode.Host)
        {
            var sender = racers.Find(r => r.ep != null && r.ep.Equals(pkt.ep));
            if (sender != null) sender.lastHeard = Time.time;
            switch (type)
            {
                case LanNet.MJoin:
                {
                    string name = br.ReadString(); int col = br.ReadByte();
                    if (sender == null)
                    {
                        int humans = racers.FindAll(r => !r.bot).Count;
                        if (humans >= 8)
                        {
                            var rw = LanNet.Begin(LanNet.MReject); rw.Write("Lobby is full");
                            net.Send(LanNet.End(rw), pkt.ep);
                            return;
                        }
                        byte id = 1;
                        while (racers.Exists(r => r.id == id)) id++;
                        col %= CarVisual.Palette.Length;
                        for (int k = 0; k < CarVisual.Palette.Length && racers.Exists(r => !r.bot && r.color == col); k++) col = (col + 1) % CarVisual.Palette.Length;
                        sender = new Racer { id = id, name = Clean(name), color = col, ep = pkt.ep, lastHeard = Time.time };
                        racers.Add(sender);
                    }
                    var w = LanNet.Begin(LanNet.MWelcome); w.Write(sender.id);
                    net.Send(LanNet.End(w), pkt.ep);
                    BroadcastLobby();
                    break;
                }
                case LanNet.MState:
                {
                    if (sender == null) return;
                    float t = br.ReadSingle(); int n = br.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        var s = LanNet.ReadState(br);
                        if (s.id != sender.id) continue;
                        ApplyRemote(sender, t, s);
                    }
                    break;
                }
                case LanNet.MLeave:
                    if (sender != null) { RemoveRacer(sender); BroadcastLobby(); }
                    break;
            }
        }
        else if (mode == Mode.Client)
        {
            if (!pkt.ep.Address.Equals(hostEp.Address)) return;
            hostLastHeard = Time.time;
            switch (type)
            {
                case LanNet.MWelcome:
                    myId = br.ReadByte();
                    welcomed = true;
                    status = "";
                    break;
                case LanNet.MReject:
                    Leave(true, br.ReadString());
                    break;
                case LanNet.MLeave:
                    Leave(true, "The host ended the session.");
                    break;
                case LanNet.MLobby:
                    if (welcomed) ReadLobby(br);
                    break;
                case LanNet.MState:
                {
                    float t = br.ReadSingle(); int n = br.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        var s = LanNet.ReadState(br);
                        if (s.id == myId) continue;
                        var r = racers.Find(x => x.id == s.id);
                        if (r != null) ApplyRemote(r, t, s);
                    }
                    break;
                }
            }
        }
    }

    static string Clean(string n)
    {
        n = (n ?? "Driver").Trim();
        if (n.Length == 0) n = "Driver";
        return n.Length > 16 ? n.Substring(0, 16) : n;
    }

    void ApplyRemote(Racer r, float t, NetState s)
    {
        if (r.remote != null) r.remote.Push(t, s);
        r.lap = s.lap; r.dist = s.dist; r.finish = s.finish;
        r.pending = s; r.hasPending = true;
    }

    void ReadLobby(BinaryReader br)
    {
        var ph = (Phase)br.ReadByte();
        laps = br.ReadByte();
        int seq = br.ReadInt32();
        float raceTime = br.ReadSingle();
        int n = br.ReadByte();
        var seen = new HashSet<byte>();
        for (int i = 0; i < n; i++)
        {
            byte id = br.ReadByte(); string name = br.ReadString(); int col = br.ReadByte(); bool bot = br.ReadBoolean(); bool inRace = br.ReadBoolean();
            seen.Add(id);
            var r = racers.Find(x => x.id == id);
            if (r == null) { r = new Racer { id = id }; racers.Add(r); }
            r.name = name; r.color = col; r.bot = bot; r.owned = id == myId;
            if (r.inRace != inRace && phase != Phase.Lobby && seq == localSeq && !inRace && r.go) { Destroy(r.go); r.go = null; }
            r.inRace = inRace;
        }
        var gone = racers.FindAll(r => !seen.Contains(r.id));
        foreach (var r in gone) RemoveRacer(r);

        var me = Me;
        if ((ph == Phase.Countdown || ph == Phase.Racing || ph == Phase.Results) && seq != localSeq && me != null && me.inRace)
        {
            localSeq = seq;
            phase = ph;
            SpawnRace(raceTime);
        }
        else if (ph == Phase.Lobby && phase != Phase.Lobby)
        {
            phase = Phase.Lobby;
            DespawnCars();
            chase.enabled = false;
            ShowShowroom(true);
        }
        else if (seq == localSeq) phase = ph;
        if (me == null || !me.inRace) { if (phase != Phase.Lobby && localSeq != seq) phase = Phase.Lobby; }
        spectating = ph != Phase.Lobby && (me == null || !me.inRace);
    }

    bool spectating;

    void BroadcastLobby()
    {
        if (mode != Mode.Host || net == null) return;
        var w = LanNet.Begin(LanNet.MLobby);
        w.Write((byte)phase); w.Write((byte)laps); w.Write(raceSeq);
        w.Write(phase == Phase.Lobby ? 0f : RaceTime);
        w.Write((byte)racers.Count);
        foreach (var r in racers)
        {
            w.Write(r.id); w.Write(r.name); w.Write((byte)r.color); w.Write(r.bot); w.Write(r.inRace);
        }
        var d = LanNet.End(w);
        foreach (var r in racers) if (r.ep != null) net.Send(d, r.ep);
    }

    NetState OwnState(Racer r)
    {
        var c = r.car;
        return new NetState
        {
            id = r.id, pos = c.rb.position, rot = c.rb.rotation, vel = c.rb.linearVelocity,
            steer = c.steerAngle, rpm = c.rpm, throttle = c.throttleIn,
            slip = Mathf.Max(c.slip[2], c.slip[3]), boost = c.boosting,
            lap = (byte)Mathf.Clamp(r.lap, 0, 255), dist = r.dist, finish = r.finish
        };
    }

    void NetSend()
    {
        if (net == null) return;
        sendTimer -= Time.deltaTime;
        lobbyTimer -= Time.deltaTime;
        discTimer -= Time.deltaTime;

        if (mode == Mode.Host)
        {
            if (discTimer <= 0) { discTimer = 1f; net.SendDiscovery(playerName + "'s race", racers.FindAll(r => !r.bot).Count, (byte)phase); }
            if (lobbyTimer <= 0) { lobbyTimer = 0.5f; BroadcastLobby(); }
            if (sendTimer <= 0)
            {
                sendTimer = 1f / 30f;
                var states = new List<NetState>();
                foreach (var r in racers)
                {
                    if (!r.inRace || r.go == null) continue;
                    if (r.owned && r.car != null) states.Add(OwnState(r));
                    else if (r.hasPending) states.Add(r.pending);
                }
                var w = LanNet.Begin(LanNet.MState);
                w.Write(Time.time); w.Write((byte)states.Count);
                foreach (var s in states) LanNet.WriteState(w, s);
                var d = LanNet.End(w);
                foreach (var r in racers) if (r.ep != null) net.Send(d, r.ep);
            }
        }
        else if (mode == Mode.Client && welcomed)
        {
            if (sendTimer <= 0)
            {
                sendTimer = 1f / 30f;
                var me = Me;
                var w = LanNet.Begin(LanNet.MState);
                w.Write(Time.time);
                if (me != null && me.car != null) { w.Write((byte)1); LanNet.WriteState(w, OwnState(me)); }
                else w.Write((byte)0);
                net.Send(LanNet.End(w), hostEp);
            }
        }
    }

    // ---------- UI ----------
    GUIStyle sTitle, sSub, sLabel, sSmall, sBig, sHuge, sBtn, sField, sCenter, sRight;
    Texture2D texPanel, texWhite;
    float uiScale;
    float VW => Screen.width / uiScale;
    float VH => Screen.height / uiScale;

    Font fontBold, fontSemi;
    Texture2D texGrad, texAccent;

    void InitStyles()
    {
        if (sTitle != null) return;
        fontBold = Resources.Load<Font>("Fonts/Rajdhani-Bold");
        fontSemi = Resources.Load<Font>("Fonts/Rajdhani-SemiBold");
        texPanel = MakeTex(new Color(0.02f, 0.025f, 0.035f, 0.78f));
        texWhite = MakeTex(Color.white);
        texAccent = MakeTex(new Color(1f, 0.42f, 0.08f));
        texGrad = new Texture2D(256, 1) { wrapMode = TextureWrapMode.Clamp };
        for (int x = 0; x < 256; x++) texGrad.SetPixel(x, 0, new Color(0.01f, 0.012f, 0.02f, Mathf.Lerp(0.92f, 0f, Mathf.Pow(x / 255f, 1.6f))));
        texGrad.Apply();

        GUIStyle Lbl(int size, Font f, Color c, TextAnchor a = TextAnchor.UpperLeft)
        {
            var st = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = a, wordWrap = false };
            if (f) st.font = f;
            st.normal.textColor = c;
            return st;
        }
        Color txt = new Color(0.93f, 0.94f, 0.96f);
        sTitle = Lbl(96, fontBold, Color.white);
        sSub = Lbl(26, fontBold, new Color(1f, 0.45f, 0.1f));
        sLabel = Lbl(30, fontSemi, txt);
        sSmall = Lbl(22, fontSemi, new Color(0.78f, 0.8f, 0.85f)); sSmall.wordWrap = true;
        sBig = Lbl(44, fontBold, Color.white);
        sHuge = Lbl(170, fontBold, Color.white, TextAnchor.MiddleCenter);
        sCenter = Lbl(44, fontBold, Color.white, TextAnchor.MiddleCenter);
        sRight = Lbl(44, fontBold, Color.white, TextAnchor.MiddleRight);

        sBtn = new GUIStyle(GUI.skin.button) { fontSize = 30, fixedHeight = 62, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(26, 16, 4, 4), border = new RectOffset(0, 0, 0, 0) };
        if (fontBold) sBtn.font = fontBold;
        var bN = MakeTex(new Color(1f, 1f, 1f, 0.08f));
        var bH = MakeTex(new Color(1f, 0.42f, 0.08f, 0.95f));
        var bA = MakeTex(new Color(1f, 0.6f, 0.25f, 1f));
        sBtn.normal.background = bN; sBtn.normal.textColor = Color.white;
        sBtn.hover.background = bH; sBtn.hover.textColor = Color.black;
        sBtn.active.background = bA; sBtn.active.textColor = Color.black;
        sBtn.focused.background = bN; sBtn.focused.textColor = Color.white;
        sBtn.onNormal.background = bN;

        sField = new GUIStyle(GUI.skin.textField) { fontSize = 30, fixedHeight = 54, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(16, 12, 6, 6) };
        if (fontSemi) sField.font = fontSemi;
        var fN = MakeTex(new Color(0f, 0f, 0f, 0.5f));
        sField.normal.background = fN; sField.focused.background = MakeTex(new Color(0f, 0f, 0f, 0.7f)); sField.hover.background = fN;
        sField.normal.textColor = sField.focused.textColor = sField.hover.textColor = Color.white;
    }

    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t;
    }

    void Panel(Rect r)
    {
        GUI.DrawTexture(r, texPanel);
        GUI.DrawTexture(new Rect(r.x, r.y, 4, r.height), texAccent);
    }

    // Arc tachometer: lit segments up to the current RPM.
    void Tach(Vector2 c, float radius, float rpmN)
    {
        int segs = 44;
        float a0 = 220f, a1 = -40f;
        var m = GUI.matrix;
        var old = GUI.color;
        for (int i = 0; i < segs; i++)
        {
            float f = i / (float)(segs - 1);
            float ang = Mathf.Lerp(a0, a1, f);
            bool lit = f <= rpmN;
            Color col = f > 0.88f ? new Color(1f, 0.15f, 0.1f) : f > 0.7f ? new Color(1f, 0.55f, 0.1f) : Color.white;
            GUI.color = lit ? col : new Color(1, 1, 1, 0.13f);
            float rad = ang * Mathf.Deg2Rad;
            Vector2 p = c + new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad)) * radius;
            // rotate in GUI space (RotateAroundPivot ignores our UI scale)
            GUI.matrix = m * Matrix4x4.TRS(p, Quaternion.Euler(0, 0, -ang + 90f), Vector3.one) * Matrix4x4.TRS(-p, Quaternion.identity, Vector3.one);
            float len = i % 4 == 0 ? 24f : 16f;
            GUI.DrawTexture(new Rect(p.x - 3f, p.y - len / 2f, 6f, len), texWhite);
        }
        GUI.matrix = m;
        GUI.color = old;
    }

    void Bar(Rect r, float v, Color c)
    {
        var old = GUI.color;
        GUI.color = new Color(1, 1, 1, 0.15f); GUI.DrawTexture(r, texWhite);
        GUI.color = c; GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), texWhite);
        GUI.color = old;
    }

    void OnGUI()
    {
        InitStyles();
        uiScale = Mathf.Max(0.5f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1));

        switch (phase)
        {
            case Phase.Menu: DrawMenu(); break;
            case Phase.Lobby: DrawLobby(); break;
            default:
                DrawHud();
                if (phase == Phase.Results) DrawResults();
                else if (Me != null && Me.finish >= 0) DrawResults();
                if (paused) DrawPause();
                break;
        }
    }

    void DrawMenu()
    {
        float x = 70, y = 60, w = 620;
        GUI.DrawTexture(new Rect(0, 0, 1150, VH), texGrad);
        GUI.Label(new Rect(x, y, 900, 110), "DRIFT RIVALS", sTitle);
        GUI.DrawTexture(new Rect(x + 4, y + 108, 90, 5), texAccent);
        GUI.Label(new Rect(x + 104, y + 96, 800, 40), "LAN RACING  //  DRIFT  //  macOS", sSub);
        y += 175;

        GUILayout.BeginArea(new Rect(x, y, w, VH - y - 20));
        GUILayout.Label("Driver name", sSmall);
        string n = GUILayout.TextField(playerName, 16, sField);
        if (n != playerName) { playerName = n; PlayerPrefs.SetString("name", n); }
        GUILayout.Space(10);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("<", sBtn, GUILayout.Width(70))) SetColor(myColor - 1);
        GUILayout.Label("Paint: " + CarVisual.PaletteNames[myColor], new GUIStyle(sLabel) { alignment = TextAnchor.MiddleCenter, fixedHeight = 64 });
        if (GUILayout.Button(">", sBtn, GUILayout.Width(70))) SetColor(myColor + 1);
        GUILayout.EndHorizontal();
        GUILayout.Space(8);
        Stepper("Laps", ref laps, 1, 20, "laps");
        Stepper("AI rivals", ref botCount, 0, 7, "bots");
        GUILayout.Space(16);
        if (GUILayout.Button("SOLO RACE", sBtn)) StartSolo();
        GUILayout.Space(6);
        if (GUILayout.Button("HOST LAN RACE", sBtn)) StartHost();
        GUILayout.Space(18);
        GUILayout.Label("Races on your Wi-Fi:", sSmall);
        int shown = 0;
        foreach (var h in hosts.Values)
        {
            if (Time.time - h.lastSeen > 3.5f) continue;
            shown++;
            string ph = h.phase == (byte)Phase.Lobby ? "in lobby" : "racing";
            if (GUILayout.Button(h.name + "  (" + h.players + " players, " + ph + ")", sBtn)) StartJoin(h.ep);
        }
        if (shown == 0) GUILayout.Label("Searching...  (ask a friend to press HOST LAN RACE)", sSmall);
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        joinIp = GUILayout.TextField(joinIp, 40, sField, GUILayout.Width(w - 190));
        if (GUILayout.Button("JOIN IP", sBtn, GUILayout.Width(180)))
        {
            if (IPAddress.TryParse(joinIp.Trim(), out var ip)) { PlayerPrefs.SetString("joinIp", joinIp.Trim()); StartJoin(new IPEndPoint(ip, LanNet.GamePort)); }
            else status = "Type the host's IP address, e.g. 192.168.1.20";
        }
        GUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(status)) { GUILayout.Space(8); GUILayout.Label(status, new GUIStyle(sSmall) { normal = { textColor = new Color(1f, 0.6f, 0.3f) } }); }
        GUILayout.FlexibleSpace();
        GUILayout.Label("W/S or arrows: throttle & brake  •  A/D steer  •  SPACE handbrake (drift)\nSHIFT nitro  •  V camera  •  C look back  •  R reset  •  ESC menu  •  F fullscreen  •  M mute", sSmall);
        GUILayout.EndArea();
        GUI.Label(new Rect(VW - 620, VH - 40, 600, 30), "Car: \"Car Concept\" by Eric Chadwick (CC-BY 4.0) • Sky & textures: Poly Haven (CC0) • Engine audio: domasx2 (CC0)", new GUIStyle(sSmall) { alignment = TextAnchor.LowerRight, fontSize = 16 });
    }

    void SetColor(int c)
    {
        int n = CarVisual.Palette.Length;
        myColor = ((c % n) + n) % n;
        PlayerPrefs.SetInt("color", myColor);
        if (showroomCar) showroomCar.GetComponent<CarVisual>().Paint(CarVisual.Palette[myColor]);
        var me = Me;
        if (me != null) me.color = myColor;
    }

    void Stepper(string label, ref int value, int min, int max, string pref)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, new GUIStyle(sLabel) { fixedHeight = 64, alignment = TextAnchor.MiddleLeft }, GUILayout.Width(250));
        if (GUILayout.Button("-", sBtn, GUILayout.Width(70))) value = Mathf.Max(min, value - 1);
        GUILayout.Label(value.ToString(), new GUIStyle(sLabel) { fixedHeight = 64, alignment = TextAnchor.MiddleCenter }, GUILayout.Width(80));
        if (GUILayout.Button("+", sBtn, GUILayout.Width(70))) value = Mathf.Min(max, value + 1);
        GUILayout.EndHorizontal();
        PlayerPrefs.SetInt(pref, value);
    }

    void DrawLobby()
    {
        float x = 70, y = 60, w = 720;
        GUI.DrawTexture(new Rect(0, 0, 1250, VH), texGrad);
        GUI.Label(new Rect(x, y, 900, 110), mode == Mode.Host ? "HOSTING" : "LOBBY", sTitle);
        y += 130;
        GUILayout.BeginArea(new Rect(x, y, w, VH - y - 20));
        if (mode == Mode.Host)
        {
            var ips = LanNet.LocalIPs();
            GUILayout.Label("Friends on the same Wi-Fi will see your race automatically.\nManual join IP: " + (ips.Count > 0 ? string.Join("  /  ", ips) : "unknown"), sSmall);
        }
        else if (!welcomed) GUILayout.Label(status, sLabel);
        else if (spectating) GUILayout.Label("A race is in progress — you'll join the next one.", sLabel);
        else GUILayout.Label("Waiting for the host to start the race...  (" + laps + " laps)", sLabel);
        GUILayout.Space(14);
        foreach (var r in racers)
        {
            GUILayout.BeginHorizontal();
            var old = GUI.color; GUI.color = CarVisual.Palette[r.color % CarVisual.Palette.Length];
            GUILayout.Box(texWhite, GUIStyle.none, GUILayout.Width(26), GUILayout.Height(26));
            GUI.color = old;
            GUILayout.Label("  " + r.name + (r.bot ? "  (AI)" : "") + (r.id == myId ? "  (you)" : "") + (r.id == 0 && !r.bot ? "  [host]" : ""), sLabel);
            GUILayout.EndHorizontal();
        }
        GUILayout.Space(14);
        if (mode == Mode.Host)
        {
            int oldBots = botCount;
            Stepper("Laps", ref laps, 1, 20, "laps");
            Stepper("AI rivals", ref botCount, 0, 7, "bots");
            if (botCount != oldBots) { SyncBots(); BroadcastLobby(); }
            GUILayout.Space(10);
            if (GUILayout.Button("START RACE", sBtn)) StartRaceAsHost();
        }
        GUILayout.Space(6);
        if (GUILayout.Button("LEAVE", sBtn)) Leave(true);
        GUILayout.EndArea();
    }

    static string FmtTime(float t)
    {
        if (t < 0) return "--:--.---";
        int m = (int)(t / 60); float s = t - m * 60;
        return m.ToString("00") + ":" + s.ToString("00.000");
    }

    void DrawHud()
    {
        var me = Me;
        var st = Standings();
        float L = track.Length;
        float rt = RaceTime;

        // top-left: position / lap / time
        Panel(new Rect(20, 20, 360, 190));
        int pos = me != null ? st.IndexOf(me) + 1 : 0;
        GUI.Label(new Rect(40, 28, 340, 60), "POS  " + (pos > 0 ? pos : 0) + " / " + st.Count, sBig);
        int lapShow = me != null ? Mathf.Clamp(me.lap, 1, laps) : 1;
        GUI.Label(new Rect(40, 78, 340, 50), "LAP  " + lapShow + " / " + laps, sLabel);
        GUI.Label(new Rect(40, 112, 340, 40), "TIME  " + FmtTime(Mathf.Max(0, me != null && me.finish >= 0 ? me.finish : rt)), sLabel);
        GUI.Label(new Rect(40, 146, 340, 40), "BEST  " + FmtTime(me != null ? me.bestLap : -1) + "   DRIFT " + driftTotal.ToString("N0"), sSmall);

        // standings list
        float sy = 225;
        Panel(new Rect(20, sy, 360, 12 + st.Count * 32));
        for (int i = 0; i < st.Count; i++)
        {
            var r = st[i];
            var old = GUI.color; GUI.color = CarVisual.Palette[r.color % CarVisual.Palette.Length];
            GUI.DrawTexture(new Rect(34, sy + 12 + i * 32, 8, 22), texWhite);
            GUI.color = old;
            string fin = r.finish >= 0 ? "  " + FmtTime(r.finish) : "";
            var style = new GUIStyle(sSmall) { fontStyle = r == me ? FontStyle.Bold : FontStyle.Normal };
            GUI.Label(new Rect(50, sy + 8 + i * 32, 320, 30), (i + 1) + ". " + r.name + fin, style);
        }

        // minimap
        float ms = 260;
        Rect mr = new Rect(20, VH - ms - 20, ms, ms);
        Panel(mr);
        GUI.DrawTexture(mr, minimap);
        foreach (var r in st)
        {
            if (r.go == null) continue;
            Vector3 p = r.go.transform.position;
            float u = (p.x - mapWorld.x) / mapWorld.width, v = (p.z - mapWorld.y) / mapWorld.height;
            float dsz = r == me ? 16 : 11;
            var old = GUI.color; GUI.color = r == me ? Color.white : CarVisual.Palette[r.color % CarVisual.Palette.Length];
            GUI.DrawTexture(new Rect(mr.x + u * ms - dsz / 2, mr.y + (1 - v) * ms - dsz / 2, dsz, dsz), texWhite);
            GUI.color = old;
        }

        // speedo
        if (me != null && me.car != null)
        {
            var c = me.car;
            Vector2 tc = new Vector2(VW - 200, VH - 175);
            var oldc = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(tc.x - 175, tc.y - 175, 350, 330), texPanel);
            GUI.color = oldc;
            Tach(tc, 140f, c.rpm / CarController.Redline);
            GUI.Label(new Rect(tc.x - 150, tc.y - 70, 300, 120), Mathf.RoundToInt(c.SpeedKmh).ToString(), new GUIStyle(sHuge) { fontSize = 110 });
            GUI.Label(new Rect(tc.x - 150, tc.y + 30, 300, 40), "KM/H", new GUIStyle(sSmall) { alignment = TextAnchor.MiddleCenter, fontSize = 24 });
            string g = c.gear < 0 ? "R" : c.gear.ToString();
            GUI.Label(new Rect(tc.x - 40, tc.y - 135, 80, 60), g, new GUIStyle(sCenter) { fontSize = 54, normal = { textColor = new Color(1f, 0.5f, 0.12f) } });
            GUI.Label(new Rect(tc.x - 130, tc.y + 110, 120, 30), "NITRO", new GUIStyle(sSmall) { fontSize = 20 });
            Bar(new Rect(tc.x - 60, tc.y + 118, 190, 14), c.boost, c.boosting ? new Color(0.3f, 0.95f, 1f) : new Color(0.2f, 0.6f, 1f));
            if (c.onGrass > 0.4f) GUI.Label(new Rect(0, VH - 140, VW, 40), "OFF TRACK", new GUIStyle(sCenter) { fontSize = 34, normal = { textColor = new Color(1f, 0.6f, 0.2f) } });
        }

        // drift combo
        if (driftCombo > 1f)
        {
            GUI.Label(new Rect(VW / 2 - 300, 120, 600, 60), "DRIFT  " + Mathf.RoundToInt(driftCombo).ToString("N0"), new GUIStyle(sCenter) { fontSize = 46, normal = { textColor = new Color(1f, 0.75f, 0.2f) } });
        }
        else if (Time.time - driftPopupTime < 1.6f && driftPopup.Length > 0)
        {
            float a = 1f - (Time.time - driftPopupTime) / 1.6f;
            GUI.Label(new Rect(VW / 2 - 300, 120 - (1 - a) * 30, 600, 60), driftPopup, new GUIStyle(sCenter) { fontSize = 40, normal = { textColor = new Color(0.4f, 1f, 0.5f, a) } });
        }

        // countdown
        if (rt < 0f && rt > -3.5f)
        {
            int n = Mathf.CeilToInt(-rt);
            if (n <= 3) GUI.Label(new Rect(0, VH / 2 - 150, VW, 260), n.ToString(), sHuge);
        }
        else if (rt >= 0f && rt < 1.2f)
            GUI.Label(new Rect(0, VH / 2 - 150, VW, 260), "GO!", new GUIStyle(sHuge) { normal = { textColor = new Color(0.4f, 1f, 0.4f) } });

        // last lap flash
        if (me != null && me.lastLap > 0 && rt - me.lapStart < 3f && me.lap >= 2 && me.finish < 0)
            GUI.Label(new Rect(0, 200, VW, 50), "LAP " + FmtTime(me.lastLap) + (me.lastLap <= me.bestLap ? "  BEST!" : ""), new GUIStyle(sCenter) { fontSize = 34 });

        // name tags
        var cam = World.Cam;
        foreach (var r in st)
        {
            if (r == me || r.go == null) continue;
            Vector3 wp = r.go.transform.position + Vector3.up * 1.9f;
            Vector3 s = cam.WorldToScreenPoint(wp);
            if (s.z <= 0 || s.z > 160f) continue;
            float sx = s.x / uiScale, syy = (Screen.height - s.y) / uiScale;
            GUI.Label(new Rect(sx - 150, syy - 30, 300, 30), r.name, new GUIStyle(sSmall) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false });
        }

        if (spectating && mode == Mode.Client)
            GUI.Label(new Rect(0, VH - 80, VW, 40), "Spectating — you'll join the next race", new GUIStyle(sCenter) { fontSize = 28 });
    }

    void DrawResults()
    {
        var st = Standings();
        float w = 700, h = 160 + st.Count * 46;
        Rect r = new Rect(VW / 2 - w / 2, VH / 2 - h / 2, w, h);
        Panel(r);
        var me = Me;
        string title = phase == Phase.Results ? "RACE RESULTS" : "FINISHED  P" + (st.IndexOf(me) + 1);
        GUI.Label(new Rect(r.x, r.y + 14, w, 60), title, new GUIStyle(sCenter) { fontSize = 48 });
        for (int i = 0; i < st.Count; i++)
        {
            var x = st[i];
            string t = x.finish >= 0 ? FmtTime(x.finish) : "racing...";
            string best = x.bestLap > 0 ? "   best " + FmtTime(x.bestLap) : "";
            var style = new GUIStyle(sLabel) { fontStyle = x == me ? FontStyle.Bold : FontStyle.Normal };
            GUI.Label(new Rect(r.x + 40, r.y + 90 + i * 46, w - 80, 44), (i + 1) + ".  " + x.name, style);
            GUI.Label(new Rect(r.x + 40, r.y + 90 + i * 46, w - 80, 44), t + best, new GUIStyle(style) { alignment = TextAnchor.UpperRight, fontSize = 22 });
        }
        if (phase == Phase.Results)
        {
            float by = r.yMax - 66;
            if (mode == Mode.Solo)
            {
                if (GUI.Button(new Rect(r.x + 40, by, w / 2 - 50, 56), "RACE AGAIN", sBtn)) { DespawnCars(); StartSolo(); }
                if (GUI.Button(new Rect(r.x + w / 2 + 10, by, w / 2 - 50, 56), "MAIN MENU", sBtn)) BackToLobby();
            }
            else if (mode == Mode.Host)
            {
                if (GUI.Button(new Rect(r.x + 40, by, w - 80, 56), "BACK TO LOBBY", sBtn)) BackToLobby();
            }
            else GUI.Label(new Rect(r.x, by, w, 56), "Waiting for host...", new GUIStyle(sCenter) { fontSize = 26 });
        }
    }

    void DrawPause()
    {
        float w = 520, h = 360;
        Rect r = new Rect(VW / 2 - w / 2, VH / 2 - h / 2, w, h);
        Panel(r);
        GUILayout.BeginArea(new Rect(r.x + 30, r.y + 24, w - 60, h - 40));
        GUILayout.Label("PAUSED", new GUIStyle(sCenter) { fontSize = 46 });
        if (GUILayout.Button("RESUME", sBtn)) { paused = false; Time.timeScale = 1; }
        GUILayout.Space(6);
        if (GUILayout.Button("RESET CAR", sBtn)) { paused = false; Time.timeScale = 1; var me = Me; if (me != null) ResetCar(me); }
        GUILayout.Space(6);
        string leave = mode == Mode.Host ? "END SESSION" : mode == Mode.Client ? "LEAVE RACE" : "QUIT TO MENU";
        if (GUILayout.Button(leave, sBtn)) { PlayerPrefs.SetInt("cam", chase.mode); Leave(true); }
        GUILayout.Space(6);
        if (GUILayout.Button("QUIT GAME", sBtn)) { Leave(false); Application.Quit(); }
        GUILayout.EndArea();
    }
}
