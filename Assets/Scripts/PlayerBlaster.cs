using UnityEngine;
using UnityEngine.InputSystem;

// Бластер (клавиша 7). Стреляет квадратными пучками света: в теле выжигают дыру, по стенам растекаются.
//   ЛКМ — стрелять (держишь — очередь, разброс растёт). ПКМ — прицелиться (точнее, камера ближе).
//   Аккумулятор встроенный, его не меняют: тратится на выстрелы и сам подзаряжается, когда не стреляешь.
//   Сел до нуля — бластер перезаряжается пару секунд. R — перезарядить сразу.
public class PlayerBlaster : MonoBehaviour
{
    public static PlayerBlaster Local { get; private set; }

    public float capacity = 24f;        // выстрелов в аккумуляторе
    public float fireRate = 1.7f;        // выстрелов в секунду (было 2.8) — между выстрелами катушки успевают набрать заряд
    public float boltSpeed = 85f;
    public float rechargeTime = 1.8f;    // полная перезарядка
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

    // красота: катушки излучателя светятся и греются, затвор отходит, вспышка у дула
    Renderer[] coils;
    Renderer tip;
    Transform slide, flash;
    Vector3 slideBase;
    float heat, flashUntil;

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
            coils = new Renderer[3];
            for (int i = 0; i < 3; i++) { Transform t = gun.Find("Coil" + i); if (t != null) coils[i] = t.GetComponent<Renderer>(); }
            Transform tp = gun.Find("Tip"); if (tp != null) tip = tp.GetComponent<Renderer>();
            slide = gun.Find("Slide"); if (slide != null) slideBase = slide.localPosition;
            flash = new GameObject("MuzzleFlash").transform;
            flash.SetParent(gun, false);
            flash.localPosition = new Vector3(0f, 0.01f, 0.42f);
            Material fm = Magic.Glow(new Color(0.6f, 1f, 1f), 9f);
            Magic.Piece(PrimitiveType.Cube, Vector3.zero, Quaternion.identity, Vector3.one, fm, flash).transform.localScale = new Vector3(0.16f, 0.02f, 0.005f);
            Magic.Piece(PrimitiveType.Cube, Vector3.zero, Quaternion.identity, Vector3.one, fm, flash).transform.localScale = new Vector3(0.02f, 0.16f, 0.005f);
            Magic.Piece(PrimitiveType.Cube, Vector3.zero, Quaternion.identity, Vector3.one, fm, flash).transform.localScale = new Vector3(0.07f, 0.07f, 0.005f);
            foreach (Transform ch in flash) { ch.localPosition = Vector3.zero; ch.localRotation = Quaternion.identity; }
            flash.gameObject.SetActive(false);
        }
    }

    bool Selected => hotbar != null && hotbar.Current == Tool.Blaster;
    float emptyMsgUntil;

    void Update()
    {
        // аккумулятор сам НЕ заряжается — только перезарядка на R
        if (recharging)
        {
            if (Time.time - rechargeStart >= rechargeTime) { recharging = false; Charge = capacity; CombatFx.Play(CombatFx.Snd.Clang, transform.position + Vector3.up, 0.25f, 2.2f); }
        }
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
        bool can = mouse != null && kb != null && locked && cam != null && (combat == null || (!combat.IsHolding && combat.BashP < 0f)) && !PlayerCarry.Busy;

        bool aiming = can && mouse.rightButton.isPressed;
        aim = Mathf.MoveTowards(aim, aiming ? 1f : 0f, Time.deltaTime * 7f);

        if (can)
        {
            if (kb.rKey.wasPressedThisFrame && !recharging && Charge < capacity) StartRecharge();
            if (mouse.leftButton.isPressed && !recharging && Time.time >= nextShot)
            {
                if (Charge >= 1f) Shoot();
                else if (mouse.leftButton.wasPressedThisFrame)
                {
                    // пусто: сухой щелчок, сам не перезаряжается
                    CombatFx.Play(CombatFx.Snd.Clang, cam.transform.position, 0.15f, 3f);
                    emptyMsgUntil = Time.time + 1.5f;
                }
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
        // отдача: ствол подбрасывает вверх и чуть в сторону, в прицеле — слабее
        float rk = Mathf.Lerp(1f, 0.55f, aim);
        PlayerCamera.Punch(new Vector3(-3.2f * rk, Random.Range(-0.9f, 0.9f) * rk, Random.Range(-1.2f, 1.2f) * rk));
        PlayerCamera.Shake(0.06f * rk);
        heat = Mathf.Min(1f, heat + 0.07f);
        flashUntil = Time.time + 0.045f;
        if (flash != null) flash.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 90f));
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
        if (combat != null && combat.IsHolding)
        {
            // левая рука держит тварь — бластер опущен в правой
            pos += new Vector3(0.08f, -0.22f, -0.12f);
            rot *= Quaternion.Euler(35f, 0f, -25f);
        }
        // ПКМ коротко — удар рукоятью: вскинул бластер стволом вверх и рубанул вниз-вперёд
        if (combat != null) PlayerCombat.BashPose(combat.BashP, new Vector3(0.05f, 0.22f, -0.1f), new Vector3(-55f, 0f, 30f),
                                                   new Vector3(-0.12f, -0.08f, 0.3f), new Vector3(65f, 0f, -20f), ref pos, ref rot);
        WeaponSway.Apply(ref pos, ref rot, 1f - 0.7f * aim);   // в прицеле держишь твёрже
        float k = 1f - Mathf.Exp(-20f * Time.deltaTime);
        gun.localPosition = Vector3.Lerp(gun.localPosition, pos, k);
        gun.localRotation = Quaternion.Slerp(gun.localRotation, rot, k);

        // катушки: каждый выстрел греет их, при очереди светятся ярче и белеют
        heat = Mathf.MoveTowards(heat, 0f, Time.deltaTime * 0.35f);
        if (flash != null) flash.gameObject.SetActive(Time.time < flashUntil);
        if (slide != null) slide.localPosition = slideBase + Vector3.back * 0.035f * kick;
        if (coils != null)
            for (int i = 0; i < coils.Length; i++)
            {
                if (coils[i] == null) continue;
                float pulse = Mathf.Clamp01(kick * 1.5f - i * 0.25f);   // волна света бежит к дулу
                Color cc = Color.Lerp(new Color(0.3f, 0.85f, 1f), new Color(1f, 0.95f, 0.85f), heat * 0.7f);
                coils[i].GetPropertyBlock(block);
                block.SetColor("_EmissionColor", cc * (1.2f + 6f * pulse + 3f * heat + (recharging ? Mathf.Sin(Time.time * 18f + i) : 0f)));
                coils[i].SetPropertyBlock(block);
            }
        if (tip != null)
        {
            tip.GetPropertyBlock(block);
            block.SetColor("_EmissionColor", new Color(0.4f, 0.95f, 1f) * (1.5f + 8f * kick + 2f * heat));
            tip.SetPropertyBlock(block);
        }

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

        if (Hud.Popups && Time.time < markUntil)
            GUI.Label(new Rect(cx + 24, cy - 4, 200, 22), markText, Hud.Small);

        // аккумулятор
        float f = Charge / capacity;
        string text = recharging ? "перезарядка..." : "аккумулятор";
        Hud.Bar(new Rect(cx - 60, cy + 40, 120, 5), recharging ? Mathf.Clamp01((Time.time - rechargeStart) / rechargeTime) : f,
                f < 0.25f && !recharging ? new Color(1f, 0.3f, 0.2f) : new Color(0.4f, 0.9f, 1f), null);
        GUI.Label(new Rect(cx - 100, cy + 46, 200, 20), text, Hud.CenterTiny);
        if (!recharging && Charge < 1f)
            GUI.Label(new Rect(cx - 100, cy + 62, 200, 20), "пусто — R", Hud.CenterTiny);   // мелко под аккумулятором

        GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                  "ЛКМ — стрелять (держи — очередь)   ПКМ — прицелиться   R — перезарядить аккумулятор (сам не заряжается)", Hud.CenterTiny);
    }
}
