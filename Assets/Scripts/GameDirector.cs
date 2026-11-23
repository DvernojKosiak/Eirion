using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

// Ход игры: время суток, день/ночь, деревня, поражение.
//   День 06:00–20:00 (5 минут), ночь 20:00–06:00 (2.5 минуты).
//   С наступлением ночи приходит набег. На рассвете оставшиеся твари сгорают.
//   Если от деревни осталось меньше 30% — поражение.
// Решает хост и раз в секунду рассылает всем время и состояние деревни.
public class GameDirector : MonoBehaviour
{
    public static GameDirector Instance { get; private set; }

    [Header("Время")]
    public float dayRealSeconds = 300f;   // сколько длится день в реальных секундах
    public float nightRealSeconds = 150f; // сколько длится ночь
    public float startHour = 9f;

    [Header("Деревня")]
    [Range(0f, 1f)] public float loseThreshold = 0.3f;

    [Header("Свет")]
    public Light sun;
    public Light moon;

    public int Day { get; private set; } = 1;
    public float Hour { get; private set; }
    public bool IsNight => Hour >= 20f || Hour < 6f;
    public float VillageIntegrity { get; private set; } = 1f;
    public bool IsOver { get; private set; }
    public int NightsSurvived { get; private set; }

    public event System.Action NightStarted; // для EnemyManager (только у хоста)
    public event System.Action DayStarted;

    const string MsgClock = "E.clock";
    CustomMessagingManager registered;
    float syncTimer, giTimer;
    bool wasNight;
    int villageTotal;
    Torch[] torches;
    Material skybox;
    float sunBaseIntensity = 1.1f;
    Color fogDay;

    void Awake()
    {
        Instance = this;
        Hour = startHour;
    }

    void Start()
    {
        torches = FindObjectsByType<Torch>(FindObjectsSortMode.None);
        foreach (BuildPiece p in BuildPiece.All) if (p.village) villageTotal++;
        if (sun != null) sunBaseIntensity = sun.intensity;
        fogDay = RenderSettings.fogColor;
        if (RenderSettings.skybox != null)
        {
            skybox = new Material(RenderSettings.skybox); // копия, чтобы не портить файл неба
            RenderSettings.skybox = skybox;
        }
        wasNight = IsNight;
        ApplyVisuals();
    }

    bool Running => Net.Online || NetworkManager.Singleton == null;

    void Update()
    {
        Net.EnsureRegistered(ref registered, Register);

        if (Running && !IsOver)
        {
            float secPerHour = IsNight ? nightRealSeconds / 10f : dayRealSeconds / 14f;
            Hour += Time.deltaTime / secPerHour;
            if (Hour >= 24f) Hour -= 24f;

            if (Net.IsHost)
            {
                bool night = IsNight;
                if (night && !wasNight) NightStarted?.Invoke();
                if (!night && wasNight)
                {
                    Day++;
                    NightsSurvived++;
                    DayStarted?.Invoke();
                }
                wasNight = night;

                // Деревня
                if (villageTotal > 0)
                {
                    int alive = 0;
                    foreach (BuildPiece p in BuildPiece.All) if (p.village) alive++;
                    VillageIntegrity = (float)alive / villageTotal;
                    if (VillageIntegrity < loseThreshold) IsOver = true;
                }

                syncTimer -= Time.deltaTime;
                if (syncTimer <= 0f || IsOver) { syncTimer = 1f; SendClock(); }
            }
        }

        ApplyVisuals();
    }

    // ---------- Свет и небо ----------
    void ApplyVisuals()
    {
        // Угол солнца: 06:00 — восход (0°), 13:00 — зенит, 20:00 — закат (180°), ночью — под землёй
        float angle = !IsNight
            ? Mathf.InverseLerp(6f, 20f, Hour) * 180f
            : 180f + Mathf.InverseLerp(0f, 10f, Hour >= 20f ? Hour - 20f : Hour + 4f) * 180f;
        float sunUp = Mathf.Clamp01(Mathf.Sin(angle * Mathf.Deg2Rad) * 3f); // 0 — ночь, 1 — день
        float dark = 1f - sunUp;

        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(angle, -40f, 0f);
            sun.intensity = sunBaseIntensity * sunUp;
            sun.color = Color.Lerp(new Color(1f, 0.6f, 0.4f), new Color(1f, 0.93f, 0.84f), Mathf.Clamp01(Mathf.Sin(angle * Mathf.Deg2Rad) * 2f));
            sun.enabled = sunUp > 0.01f;
        }
        if (moon != null) moon.intensity = 0.28f * dark;

        RenderSettings.fogColor = Color.Lerp(fogDay, new Color(0.04f, 0.05f, 0.09f), dark);
        RenderSettings.ambientIntensity = Mathf.Lerp(1f, 0.35f, dark);
        if (skybox != null && skybox.HasProperty("_Exposure")) skybox.SetFloat("_Exposure", Mathf.Lerp(1.3f, 0.12f, dark));

        bool lit = Hour >= 19f || Hour < 7f;
        if (torches != null) foreach (Torch t in torches) if (t != null) t.SetLit(lit);

        giTimer -= Time.deltaTime;
        if (giTimer <= 0f) { giTimer = 2f; DynamicGI.UpdateEnvironment(); }
    }

    // ---------- Сеть ----------
    void SendClock()
    {
        if (!Net.Online) return;
        FastBufferWriter w = Net.Writer(64);
        try
        {
            w.WriteValueSafe(Day); w.WriteValueSafe(Hour); w.WriteValueSafe(VillageIntegrity);
            w.WriteValueSafe(IsOver); w.WriteValueSafe(NightsSurvived);
            Net.ToAllClients(MsgClock, w);
        }
        finally { w.Dispose(); }
    }

    void Register(CustomMessagingManager cmm)
    {
        cmm.RegisterNamedMessageHandler(MsgClock, (sender, r) =>
        {
            r.ReadValueSafe(out int day); r.ReadValueSafe(out float hour); r.ReadValueSafe(out float village);
            r.ReadValueSafe(out bool over); r.ReadValueSafe(out int nights);
            if (Net.IsHost) return;
            Day = day; Hour = hour; VillageIntegrity = village; IsOver = over; NightsSurvived = nights;
        });
    }

    public float SecondsUntilNight => IsNight ? 0f : (20f - Hour) * dayRealSeconds / 14f;
    public float SecondsUntilDawn => !IsNight ? 0f : ((Hour >= 20f ? 30f - Hour : 6f - Hour)) * nightRealSeconds / 10f;

    // ---------- Выход в меню / новый мир ----------
    public static void ReturnToMenu()
    {
        PlayerCamera.BlockCursorLock = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.Shutdown();
            Destroy(nm.gameObject); // в новой сцене будет свой
        }
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    // ---------- Экран ----------
    static string Clock(float hour)
    {
        int h = Mathf.FloorToInt(hour);
        int m = Mathf.FloorToInt((hour - h) * 60f);
        return h.ToString("00") + ":" + m.ToString("00");
    }

    static string Time2(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        return Mathf.FloorToInt(seconds / 60f) + ":" + Mathf.FloorToInt(seconds % 60f).ToString("00");
    }

    void OnGUI()
    {
        if (!Running) return;

        if (IsOver)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            PlayerCamera.BlockCursorLock = true;
            Hud.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.05f, 0f, 0.02f, 0.85f));
            GUI.Label(new Rect(0, Screen.height / 2f - 90, Screen.width, 40), "ДЕРЕВНЯ ПАЛА", Hud.CenterBig);
            GUI.Label(new Rect(0, Screen.height / 2f - 40, Screen.width, 30),
                      "Вы продержались дней: " + Day + "    Отбито ночей: " + NightsSurvived, Hud.Center);
            Rect b = new Rect(Screen.width / 2f - 120, Screen.height / 2f + 20, 240, 44);
            if (GUI.Button(b, "В главное меню")) ReturnToMenu();
            return;
        }

        int alive = EnemyManager.Instance != null ? EnemyManager.Instance.AliveCount : 0;
        string line1 = "День " + Day + "  ·  " + Clock(Hour) + (IsNight ? "  ·  НОЧЬ" : "");
        string line2 = IsNight
            ? (alive > 0 ? "НАБЕГ!  Тварей: " + alive + "   ·   рассвет через " + Time2(SecondsUntilDawn) : "Рассвет через " + Time2(SecondsUntilDawn))
            : "Ночь (набег) через " + Time2(SecondsUntilNight);
        GUI.Label(new Rect(0, 8, Screen.width, 26), line1, Hud.Center);
        GUI.Label(new Rect(0, 32, Screen.width, 30), line2, IsNight && alive > 0 ? Hud.CenterBig : Hud.Center);

        Rect vb = new Rect(Screen.width / 2f - 110, 70, 220, 10);
        Color vc = VillageIntegrity > 0.6f ? new Color(0.3f, 0.8f, 0.35f) : VillageIntegrity > 0.4f ? new Color(0.95f, 0.75f, 0.2f) : new Color(0.9f, 0.25f, 0.2f);
        Hud.Bar(vb, VillageIntegrity, vc, null);
        GUI.Label(new Rect(0, 80, Screen.width, 20), "Деревня " + Mathf.RoundToInt(VillageIntegrity * 100f) + "%   ·   N — позвать набег", Hud.CenterTiny);

        // На Esc — кнопка выхода
        if (Cursor.lockState != CursorLockMode.Locked && !PlayerCamera.BlockCursorLock)
        {
            if (GUI.Button(new Rect(Screen.width - 190, Screen.height - 60, 170, 40), "Выйти в меню")) ReturnToMenu();
        }
    }
}
