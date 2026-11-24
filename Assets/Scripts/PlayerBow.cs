using UnityEngine;
using UnityEngine.InputSystem;

// Лук (клавиша 5).
//   Зажми ЛКМ — натягиваешь тетиву: видно, как стрела отходит назад, камера чуть приближает.
//   Отпусти — выстрел. Чем сильнее натянул, тем быстрее и дальше летит стрела и тем больнее бьёт.
//   Полностью натянутый лук (прицел стал золотым) — мощный выстрел: может пригвоздить тварь к стене.
//   Стрела летит по дуге (падает), втыкается туда, куда попала, и торчит. Свои стрелы из стен можно подобрать.
//   Стрелы делаются на верстаке: 1 бревно → 6 стрел.
public class PlayerBow : MonoBehaviour
{
    public static PlayerBow Local { get; private set; }

    public float maxDrawTime = 0.9f;     // сколько тянуть до полного натяжения
    public float minSpeed = 14f;
    public float maxSpeed = 48f;
    public float zoom = 12f;             // на сколько градусов приближает камера при натяжении
    public float holdStamina = 6f;
    public bool infiniteArrows = true;   // пока отлаживаем бой — стрелы не кончаются       // в секунду, пока держишь полностью натянутым

    PlayerHotbar hotbar;
    Inventory inventory;
    PlayerCombat combat;
    PlayerHealth health;
    Camera cam;
    float baseFov;

    Transform bow;
    Vector3 restPos;
    Quaternion restRot;
    LineRenderer stringTop, stringBottom;
    Transform nocked;                   // стрела на тетиве

    bool drawing;
    float drawStart;
    float draw;                         // 0..1 — насколько натянут
    float shotKick;                     // отдача после выстрела
    float noArrowsMsgUntil;
    bool wasSelected;

    static string markText;
    static float markUntil;
    static bool markHeavy;

    // Тетива крепится к концам плеч (в координатах модели лука)
    static readonly Vector3 TipTop = new Vector3(0f, 0.54f, 0.02f);
    static readonly Vector3 TipBottom = new Vector3(0f, -0.54f, 0.02f);
    const float RestNockZ = 0.02f;
    const float PullZ = 0.45f;

    void OnEnable() { Local = this; }
    void OnDisable()
    {
        if (Local == this) Local = null;
        if (cam != null && baseFov > 0f) cam.fieldOfView = baseFov;
    }

    void Start()
    {
        hotbar = GetComponent<PlayerHotbar>();
        inventory = GetComponent<Inventory>();
        combat = GetComponent<PlayerCombat>();
        health = GetComponent<PlayerHealth>();
        cam = Camera.main;
        if (cam != null) baseFov = cam.fieldOfView;

        if (hotbar != null && hotbar.bowModel != null)
        {
            bow = hotbar.bowModel;
            restPos = bow.localPosition;
            restRot = bow.localRotation;
            stringTop = MakeString("StringTop");
            stringBottom = MakeString("StringBottom");
            GameObject n = new GameObject("NockedArrow");
            n.transform.SetParent(bow, false);
            nocked = n.transform;
            Arrow.BuildModel(nocked, false);
        }
    }

    LineRenderer MakeString(string name)
    {
        GameObject g = new GameObject(name);
        g.layer = 2;
        g.transform.SetParent(bow, false);
        LineRenderer lr = g.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.positionCount = 2;
        lr.startWidth = lr.endWidth = 0.006f;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = new Color(0.9f, 0.88f, 0.8f);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return lr;
    }

    bool Selected => hotbar != null && hotbar.Current == Tool.Bow;
    int Arrows => infiniteArrows || inventory == null ? 999 : inventory.Count(ResourceType.Arrow);

    void Update()
    {
        if (bow == null) return;
        if (!Selected)
        {
            // лук убран: всё в исходное состояние
            drawing = false; draw = 0f; shotKick = 0f;
            bow.localPosition = restPos; bow.localRotation = restRot;
            if (wasSelected)
            {
                PlayerCamera.LookScale = 1f; wasSelected = false;
                if (cam != null && baseFov > 0f) cam.fieldOfView = baseFov; // сразу вернуть обзор (бластер тоже меняет его)
            }
            return;
        }

        Mouse mouse = Mouse.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool busy = combat != null && (combat.IsHolding || combat.IsAttacking);
        bool blocking = health != null && health.IsBlocking;
        bool can = mouse != null && locked && !busy && !blocking;

        if (!can) drawing = false;
        else
        {
            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (Arrows > 0) { drawing = true; drawStart = Time.time; }
                else noArrowsMsgUntil = Time.time + 1.5f;
            }
            if (drawing && !mouse.leftButton.isPressed)
            {
                drawing = false;
                if (draw > 0.15f) Shoot();
            }
        }

        // натяжение: быстро в начале, тяжело в конце; отпустил не стреляя — тетива возвращается
        if (drawing)
        {
            float t = Mathf.Clamp01((Time.time - drawStart) / maxDrawTime);
            draw = 1f - (1f - t) * (1f - t);
            // держать полностью натянутым тяжело — тратит выносливость, кончилась — руки дрожат и тетива ползёт назад
            if (draw >= 0.99f && health != null && !health.UseStamina(holdStamina * Time.deltaTime, false))
                drawStart = Time.time - maxDrawTime * 0.8f;
        }
        else draw = Mathf.MoveTowards(draw, 0f, Time.deltaTime * 6f);

        shotKick = Mathf.MoveTowards(shotKick, 0f, Time.deltaTime * 5f);
        SetFov(draw);
        PlayerCamera.LookScale = Mathf.Lerp(1f, 0.6f, draw); // натянул — целишься точнее
        wasSelected = true;
        Pose();
    }

    void SetFov(float k)
    {
        if (cam == null || baseFov <= 0f) return;
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, baseFov - zoom * k, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
    }

    void Pose()
    {
        // при натяжении лук выходит ближе к центру экрана и встаёт ровнее
        float s = draw * draw * (3f - 2f * draw);
        Vector3 aimPos = new Vector3(0.12f, -0.16f, 0.62f);
        Quaternion aimRot = Quaternion.Euler(0f, -3f, 6f);
        Vector3 shake = draw >= 0.99f ? Random.insideUnitSphere * 0.0025f : Vector3.zero;
        Vector3 bp = Vector3.Lerp(restPos, aimPos, s) + shake + Vector3.back * 0.06f * shotKick;
        Quaternion br = Quaternion.Slerp(restRot, aimRot, s) * Quaternion.Euler(-10f * shotKick, 0f, 0f);
        WeaponSway.Apply(ref bp, ref br, 1f - 0.6f * s);   // натянул — держишь твёрже
        bow.localPosition = bp;
        bow.localRotation = br;

        Vector3 nock = new Vector3(0f, 0f, RestNockZ - draw * PullZ);
        stringTop.SetPosition(0, TipTop); stringTop.SetPosition(1, nock);
        stringBottom.SetPosition(0, TipBottom); stringBottom.SetPosition(1, nock);

        // стрела лежит на тетиве, пока она есть; сразу после выстрела её нет
        bool show = Arrows > 0 && shotKick < 0.5f;
        nocked.gameObject.SetActive(show);
        nocked.localPosition = new Vector3(-0.03f, 0f, nock.z + 0.8f);
        nocked.localRotation = Quaternion.identity;
    }

    void Shoot()
    {
        if (!infiniteArrows && inventory != null && !inventory.Take(ResourceType.Arrow, 1)) return;
        bool heavy = draw >= 0.95f;
        Vector3 dir = cam.transform.forward;
        // не до конца натянул — стрела идёт чуть неточно
        float spread = (1f - draw) * 2.5f;
        dir = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f) * dir;
        Vector3 origin = cam.transform.position + cam.transform.forward * 0.6f;
        Vector3 vel = dir * Mathf.Lerp(minSpeed, maxSpeed, draw);
        EnemyManager.ShootArrow(origin, vel, heavy, transform.root);
        shotKick = 1f;
        CombatFx.Play(CombatFx.Snd.Twang, cam.transform.position, 0.5f + 0.5f * draw, Mathf.Lerp(1.25f, 0.95f, draw));
        draw = 0f;
        PlayerCamera.Shake(heavy ? 0.12f : 0.05f);
    }

    // Попадание моей стрелой — надпись у прицела
    public static void HitMarker(BodyPartType part, bool heavy)
    {
        markText = part == BodyPartType.Head ? "В ГОЛОВУ!" : part == BodyPartType.Sack ? "В МЕШОК!" : BodyPart.IsLeg(part) ? "в ногу" : BodyPart.IsArm(part) ? "в руку" : "в тело";
        markHeavy = heavy || part == BodyPartType.Head;
        markUntil = Time.time + 0.9f;
    }

    void OnGUI()
    {
        if (!Selected) return;
        float cx = Screen.width / 2f, cy = Screen.height / 2f;

        // прицел: четыре чёрточки сходятся при натяжении, на полном — золотые
        float gap = Mathf.Lerp(22f, 4f, draw);
        Color c = draw >= 0.95f ? new Color(1f, 0.8f, 0.3f) : new Color(1f, 1f, 1f, 0.85f);
        Hud.Fill(new Rect(cx - 1, cy - gap - 8, 2, 8), c);
        Hud.Fill(new Rect(cx - 1, cy + gap, 2, 8), c);
        Hud.Fill(new Rect(cx - gap - 8, cy - 1, 8, 2), c);
        Hud.Fill(new Rect(cx + gap, cy - 1, 8, 2), c);
        Hud.Fill(new Rect(cx - 1, cy - 1, 2, 2), c);

        if (Time.time < markUntil)
        {
            Color old = GUI.color;
            GUI.color = markHeavy ? new Color(1f, 0.75f, 0.3f, Mathf.Clamp01((markUntil - Time.time) / 0.3f)) : new Color(1f, 1f, 1f, Mathf.Clamp01((markUntil - Time.time) / 0.3f));
            GUI.Label(new Rect(cx + 26, cy - 4, 300, 22), markText, Hud.Small);
            GUI.color = old;
        }

        int n = Arrows;
        GUI.Label(new Rect(cx - 150, Screen.height - 120, 300, 24), (infiniteArrows ? "Стрелы: без конца" : "Стрел: " + n), Hud.Center);
        GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                  "зажми ЛКМ — натянуть, отпусти — выстрел   (до золотого прицела — мощный, пригвождает к стене)   свои стрелы подбираются", Hud.CenterTiny);
        if (Time.time < noArrowsMsgUntil || (n == 0 && drawing))
            GUI.Label(new Rect(cx - 300, cy - 70, 600, 30), "Нет стрел — сделай на верстаке (1 бревно → 6 стрел)", Hud.CenterBig);
    }
}
