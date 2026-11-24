using UnityEngine;
using UnityEngine.InputSystem;

// Бластер (клавиша 7). Стреляет квадратными пучками света: в теле выжигают дыру, по стенам растекаются.
//   ЛКМ — стрелять (держишь — очередь, разброс растёт). ПКМ — прицелиться (точнее, камера ближе).
//   Аккумулятор встроенный, его не меняют: тратится на выстрелы и сам подзаряжается, когда не стреляешь.
//   Сел до нуля — бластер перезаряжается пару секунд. R — перезарядить сразу.
public class PlayerBlaster : MonoBehaviour
{
    public static PlayerBlaster Local { get; private set; }

    public float capacity = 36f;        // выстрелов в аккумуляторе
    public float fireRate = 8f;          // выстрелов в секунду
    public float boltSpeed = 85f;
    public float rechargeTime = 1.8f;    // полная перезарядка
    public float trickle = 3f;           // подзарядка сама, в выстрелах в секунду
    public float zoom = 18f;

    public float Charge { get; private set; }

    PlayerHotbar hotbar;
    PlayerCombat combat;
    Camera cam;
    float baseFov;
    Transform gun;
    Renderer cell;
    MaterialPropertyBlock block;
    Vector3 restPos;
    Quaternion restRot;

    float nextShot, lastShot = -100f;
    float bloom;                         // разброс, градусы
    bool recharging;
    float rechargeStart;
    float kick;
    float aim;                           // 0..1 — прицеливание
    bool wasSelected;
    string markText;
    float markUntil;

    void OnEnable() { Local = this; }
    void OnDisable()
    {
        if (Local == this) Local = null;
        if (cam != null && baseFov > 0f) cam.fieldOfView = baseFov;
    }

    void Start()
    {
        hotbar = GetComponent<PlayerHotbar>();
        combat = GetComponent<PlayerCombat>();
        cam = Camera.main;
        if (cam != null) baseFov = cam.fieldOfView;
        Charge = capacity;
        block = new MaterialPropertyBlock();
        if (hotbar != null && hotbar.blasterModel != null)
        {
            gun = hotbar.blasterModel;
            restPos = gun.localPosition;
            restRot = gun.localRotation;
            Transform c = gun.Find("Cell");
            if (c != null) cell = c.GetComponent<Renderer>();
        }
    }

    bool Selected => hotbar != null && hotbar.Current == Tool.Blaster;

    void Update()
    {
        // аккумулятор: перезарядка и тихая подзарядка
        if (recharging)
        {
            if (Time.time - rechargeStart >= rechargeTime) { recharging = false; Charge = capacity; CombatFx.Play(CombatFx.Snd.Clang, transform.position + Vector3.up, 0.25f, 2.2f); }
        }
        else if (Time.time - lastShot > 1f) Charge = Mathf.Min(capacity, Charge + trickle * Time.deltaTime);
        bloom = Mathf.MoveTowards(bloom, 0f, Time.deltaTime * 6f);
        kick = Mathf.MoveTowards(kick, 0f, Time.deltaTime * 9f);

        if (gun == null) return;
        if (!Selected)
        {
            if (wasSelected)
            {
                wasSelected = false;
                if (cam != null && baseFov > 0f) cam.fieldOfView = baseFov;
                PlayerCamera.LookScale = 1f;
            }
            aim = 0f;
            gun.localPosition = restPos; gun.localRotation = restRot;
            return;
        }
        wasSelected = true;

        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool can = mouse != null && kb != null && locked && cam != null && (combat == null || !combat.IsHolding);

        bool aiming = can && mouse.rightButton.isPressed;
        aim = Mathf.MoveTowards(aim, aiming ? 1f : 0f, Time.deltaTime * 7f);

        if (can)
        {
            if (kb.rKey.wasPressedThisFrame && !recharging && Charge < capacity) StartRecharge();
            if (mouse.leftButton.isPressed && !recharging && Time.time >= nextShot)
            {
                if (Charge >= 1f) Shoot();
                else StartRecharge();
            }
        }

        if (cam != null && baseFov > 0f)
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, baseFov - zoom * aim, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
        PlayerCamera.LookScale = Mathf.Lerp(1f, 0.7f, aim);
        Pose();
    }

    void StartRecharge()
    {
        recharging = true;
        rechargeStart = Time.time;
        CombatFx.Play(CombatFx.Snd.Hum, transform.position + Vector3.up, 0.5f, 1.4f);
    }

    void Shoot()
    {
        nextShot = Time.time + 1f / fireRate;
        lastShot = Time.time;
        Charge -= 1f;

        Vector3 muzzle = gun.TransformPoint(new Vector3(0f, 0.02f, 0.42f));
        // целимся туда, куда смотрит прицел
        Ray r = new Ray(cam.transform.position, cam.transform.forward);
        Vector3 target = r.GetPoint(120f);
        RaycastHit[] hits = Physics.RaycastAll(r, 120f, ~(1 << 2), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            if (h.transform.root == transform.root) continue;
            if (h.collider.isTrigger && h.collider.GetComponentInParent<BodyPart>() == null) continue;
            if (h.collider.GetComponentInParent<Enemy>() != null && h.collider.GetComponentInParent<BodyPart>() == null) continue;
            target = h.point; break;
        }
        // слишком близко к стене — стреляем прямо из центра, чтобы не пролетело сквозь
        Vector3 origin = Vector3.Distance(cam.transform.position, target) < 1.2f ? cam.transform.position : muzzle;
        Vector3 dir = (target - origin).normalized;
        float spread = (0.4f + bloom) * Mathf.Lerp(1f, 0.3f, aim);
        dir = Quaternion.AngleAxis(Random.Range(-spread, spread), cam.transform.up) * Quaternion.AngleAxis(Random.Range(-spread, spread), cam.transform.right) * dir;
        EnemyManager.CastSpell(Magic.Bolt, origin, dir * boltSpeed, 0f, transform.root);

        bloom = Mathf.Min(3.5f, bloom + 0.55f);
        kick = 1f;
        PlayerCamera.Punch(new Vector3(-1.1f * Mathf.Lerp(1f, 0.5f, aim), Random.Range(-0.4f, 0.4f), 0f));
        CombatFx.Play(CombatFx.Snd.Laser, muzzle, 0.55f, Random.Range(0.95f, 1.1f));
        Magic.Flash(muzzle, new Color(0.4f, 0.95f, 1f), 3f, 4f, 0.06f);
    }

    void Pose()
    {
        Vector3 aimPos = new Vector3(0f, -0.17f, 0.42f);
        Vector3 pos = Vector3.Lerp(restPos, aimPos, aim) + Vector3.back * 0.05f * kick + Vector3.up * 0.01f * kick;
        Quaternion rot = Quaternion.Slerp(restRot, Quaternion.identity, aim) * Quaternion.Euler(-6f * kick, 0f, 0f);
        if (recharging)
        {
            // перезарядка: бластер наклонён, аккумулятор мигает
            float t = (Time.time - rechargeStart) / rechargeTime;
            rot *= Quaternion.Euler(20f * Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI), 0f, 35f * Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI));
        }
        float k = 1f - Mathf.Exp(-20f * Time.deltaTime);
        gun.localPosition = Vector3.Lerp(gun.localPosition, pos, k);
        gun.localRotation = Quaternion.Slerp(gun.localRotation, rot, k);

        if (cell != null)
        {
            float f = Charge / capacity;
            Color c = Color.Lerp(new Color(1f, 0.2f, 0.1f), new Color(0.3f, 0.9f, 1f), f);
            float glow = recharging ? (Mathf.Sin(Time.time * 18f) > 0f ? 4f : 0.5f) : 1f + 3f * f;
            cell.GetPropertyBlock(block);
            block.SetColor("_EmissionColor", c * glow);
            block.SetColor("_BaseColor", c);
            cell.SetPropertyBlock(block);
        }
    }

    public void HitMarker(BodyPartType part)
    {
        markText = part == BodyPartType.Head ? "В ГОЛОВУ" : BodyPart.IsLeg(part) ? "нога" : BodyPart.IsArm(part) ? "рука" : "тело";
        markUntil = Time.time + 0.5f;
    }

    void OnGUI()
    {
        if (!Selected) return;
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        // прицел — квадратные скобки, расходятся от разброса
        float g = 8f + bloom * 6f * Mathf.Lerp(1f, 0.3f, aim);
        Color c = new Color(0.6f, 0.95f, 1f, 0.9f);
        Hud.Fill(new Rect(cx - g - 2, cy - g - 2, 7, 2), c); Hud.Fill(new Rect(cx - g - 2, cy - g - 2, 2, 7), c);
        Hud.Fill(new Rect(cx + g - 5, cy - g - 2, 7, 2), c); Hud.Fill(new Rect(cx + g, cy - g - 2, 2, 7), c);
        Hud.Fill(new Rect(cx - g - 2, cy + g, 7, 2), c); Hud.Fill(new Rect(cx - g - 2, cy + g - 5, 2, 7), c);
        Hud.Fill(new Rect(cx + g - 5, cy + g, 7, 2), c); Hud.Fill(new Rect(cx + g, cy + g - 5, 2, 7), c);
        Hud.Fill(new Rect(cx - 1, cy - 1, 2, 2), c);

        if (Time.time < markUntil)
            GUI.Label(new Rect(cx + 24, cy - 4, 200, 22), markText, Hud.Small);

        // аккумулятор
        float f = Charge / capacity;
        string text = recharging ? "перезарядка..." : "аккумулятор";
        Hud.Bar(new Rect(cx - 60, cy + 40, 120, 5), recharging ? Mathf.Clamp01((Time.time - rechargeStart) / rechargeTime) : f,
                f < 0.25f && !recharging ? new Color(1f, 0.3f, 0.2f) : new Color(0.4f, 0.9f, 1f), null);
        GUI.Label(new Rect(cx - 100, cy + 46, 200, 20), text, Hud.CenterTiny);

        GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                  "ЛКМ — стрелять (держи — очередь)   ПКМ — прицелиться   R — перезарядить аккумулятор (сам тоже заряжается)", Hud.CenterTiny);
    }
}
