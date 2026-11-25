using UnityEngine;

// Тело героя: туловище, голова, руки и ноги (из кубиков), которые двигаются сами.
//   Ходьба и бег — ноги шагают, руки машут. Вбок и назад — тоже.
//   Толкнули или ударили — тело откидывает в сторону удара, руки разлетаются, потом выпрямляется.
//   В прыжке ноги поджимаются, в подкате — тело откинуто назад, одна нога вперёд.
// Работает у всех игроков: чужих видно целиком. У себя от первого лица видно ноги (и свою тень).
public class PlayerBody : MonoBehaviour
{
    public static PlayerBody Local { get; private set; }

    Transform hips, torso, head, armL, armR, foreL, foreR, thighL, thighR, shinL, shinR;
    Renderer[] upper, all, leftArm = new Renderer[0];
    Renderer capsule;
    PlayerCamera pcam;
    PlayerMovement movement;
    PlayerHealth health;
    PlayerHotbar hotbar;
    MaterialPropertyBlock block;
    bool colored;

    Vector3 lastPos, vel, lastVel, accel;
    float phase;
    Vector2 lean, leanVel;               // x — вперёд/назад, y — вбок (градусы)
    float flail;                         // руки разлетаются после удара
    float hipYaw;

    // Управляют бой и захват (только у своего игрока)
    public float KickP = -1f;            // пинок: 0..1 — сколько прошло, меньше 0 — не пинаю
    public Vector3? HoldTarget;          // держу тварь — левая кисть сюда
    public Vector3? HoldTargetR;         // правая кисть сюда (держу тварь древком лука или посоха двумя руками)
    Vector3 toolBase, toolTurn;
    Transform lastTool;          // корпус идёт за оружием: резкое движение оружия разворачивает плечи
    public float Twist;                  // разворот корпуса (бросок)
    public const float ArmReach = 0.62f;  // плечо + предплечье
    public Vector3 RightShoulder => armR != null ? armR.position : transform.position + Vector3.up * 0.5f;
    float air;

    void Start()
    {
        movement = GetComponent<PlayerMovement>();
        health = GetComponent<PlayerHealth>();
        hotbar = GetComponent<PlayerHotbar>();
        pcam = GetComponentInChildren<PlayerCamera>(true);
        capsule = GetComponent<Renderer>();
        block = new MaterialPropertyBlock();
        Build();
        lastPos = transform.position;
    }

    void Build()
    {
        Material mat = capsule != null ? capsule.sharedMaterial : null;
        hips = Node("Hips", transform, new Vector3(0f, -0.05f, 0f));
        torso = Node("Torso", hips, Vector3.zero);
        Box(torso, new Vector3(0f, 0.33f, 0f), new Vector3(0.46f, 0.62f, 0.26f), mat, 1f);
        head = Node("Head", torso, new Vector3(0f, 0.68f, 0f));
        Box(head, new Vector3(0f, 0.13f, 0f), new Vector3(0.26f, 0.28f, 0.26f), mat, 1.25f, PrimitiveType.Cube);
        armL = Node("ArmL", torso, new Vector3(-0.29f, 0.58f, 0f));
        armR = Node("ArmR", torso, new Vector3(0.29f, 0.58f, 0f));
        foreL = Limb(armL, 0.32f, 0.12f, 0.3f, 0.1f, mat, out _);
        foreR = Limb(armR, 0.32f, 0.12f, 0.3f, 0.1f, mat, out _);
        thighL = Node("ThighL", hips, new Vector3(-0.12f, 0f, 0f));
        thighR = Node("ThighR", hips, new Vector3(0.12f, 0f, 0f));
        shinL = Limb(thighL, 0.46f, 0.16f, 0.46f, 0.14f, mat, out _);
        shinR = Limb(thighR, 0.46f, 0.16f, 0.46f, 0.14f, mat, out _);
        Box(shinL, new Vector3(0f, -0.47f, 0.05f), new Vector3(0.15f, 0.07f, 0.26f), mat, 0.45f);
        Box(shinR, new Vector3(0f, -0.47f, 0.05f), new Vector3(0.15f, 0.07f, 0.26f), mat, 0.45f);

        all = hips.GetComponentsInChildren<Renderer>();
        var up = new System.Collections.Generic.List<Renderer>();
        foreach (Renderer r in all)
            if (!r.transform.IsChildOf(thighL) && !r.transform.IsChildOf(thighR)) up.Add(r);
        upper = up.ToArray();
        leftArm = armL.GetComponentsInChildren<Renderer>();
    }

    static Transform Node(string name, Transform parent, Vector3 pos)
    {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        return t;
    }

    // Кубик тела. shade — светлее/темнее основного цвета (штаны, сапоги, лицо)
    void Box(Transform parent, Vector3 pos, Vector3 size, Material mat, float shade, PrimitiveType type = PrimitiveType.Cube)
    {
        GameObject g = GameObject.CreatePrimitive(type);
        Destroy(g.GetComponent<Collider>());
        g.layer = 2;
        g.name = "Shade" + shade.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos;
        g.transform.localScale = size;
        if (mat != null) g.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // Сустав из двух частей: верх и низ (плечо—предплечье, бедро—голень). Возвращает нижний сустав
    Transform Limb(Transform top, float len1, float w1, float len2, float w2, Material mat, out Transform lower)
    {
        Box(top, new Vector3(0f, -len1 / 2f, 0f), new Vector3(w1, len1, w1), mat, 0.85f);
        lower = Node(top.name + "Low", top, new Vector3(0f, -len1, 0f));
        Box(lower, new Vector3(0f, -len2 / 2f, 0f), new Vector3(w2, len2, w2), mat, 0.6f);
        return lower;
    }

    // Удар по телу (вызывает своё здоровье): откидывает в сторону удара
    public void Hit(Vector3 worldDir, float strength)
    {
        Vector3 l = transform.InverseTransformDirection(worldDir);
        leanVel += new Vector2(l.z, -l.x) * 420f * strength;
        flail = Mathf.Max(flail, Mathf.Clamp01(strength));
    }

    void LateUpdate()
    {
        if (hips == null) return;
        float dt = Mathf.Max(0.0001f, Time.deltaTime);

        bool local = pcam != null && pcam.enabled;
        if (local) Local = this;

        // цвет игрока (сеть красит капсулу — берём оттуда) и прячем капсулу
        if (!colored && capsule != null)
        {
            capsule.GetPropertyBlock(block);
            Color baseC = block.GetColor("_BaseColor");
            if (baseC.a <= 0.001f && capsule.sharedMaterial != null && capsule.sharedMaterial.HasProperty("_BaseColor")) baseC = capsule.sharedMaterial.GetColor("_BaseColor");
            foreach (Renderer r in all)
            {
                if (!float.TryParse(r.name.Replace("Shade", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float shade)) shade = 1f;
                Color c = shade > 1.1f ? new Color(0.85f, 0.7f, 0.58f) : baseC * shade;
                c.a = 1f;
                MaterialPropertyBlock b = new MaterialPropertyBlock();
                b.SetColor("_BaseColor", c); b.SetColor("_Color", c);
                r.SetPropertyBlock(b);
            }
            capsule.enabled = false;
            colored = Time.time > 1f;            // пару раз за первую секунду — сеть могла покрасить позже
        }

        // от первого лица верх тела не рисуем (только тень), чтобы не мешал
        bool fp = local && !pcam.IsThirdPerson;
        // но если держу тварь — левую руку видно: она её и держит
        bool showLeft = local && HoldTarget.HasValue;
        foreach (Renderer r in upper)
            r.shadowCastingMode = fp && !(showLeft && System.Array.IndexOf(leftArm, r) >= 0) ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;

        // скорость и ускорение — по тому, как сдвинулись (работает и для чужих игроков)
        Vector3 v = (transform.position - lastPos) / dt;
        lastPos = transform.position;
        if (v.magnitude > 40f) v = Vector3.zero;   // телепорт (возрождение)
        vel = Vector3.Lerp(vel, v, 1f - Mathf.Exp(-14f * dt));
        Vector3 a = (vel - lastVel) / dt;
        lastVel = vel;
        accel = Vector3.Lerp(accel, a, 1f - Mathf.Exp(-10f * dt));
        Vector3 lv = transform.InverseTransformDirection(vel);
        Vector3 la = transform.InverseTransformDirection(accel);
        Vector2 flat = new Vector2(lv.x, lv.z);
        float speed = flat.magnitude;
        bool grounded = Mathf.Abs(vel.y) < 1.5f;
        air = Mathf.MoveTowards(air, grounded ? 0f : 1f, dt * 5f);

        // наклон: тело «отстаёт» от ускорения, на бегу чуть вперёд; пружина — откинуло и выпрямилось
        Vector2 want = new Vector2(Mathf.Clamp(la.z * 1.6f + lv.z * 1.2f, -30f, 30f), Mathf.Clamp(-la.x * 1.6f - lv.x * 0.8f, -25f, 25f));
        leanVel += ((want - lean) * 90f - leanVel * 13f) * dt;
        lean += leanVel * dt;
        lean = Vector2.ClampMagnitude(lean, 45f);
        flail = Mathf.MoveTowards(flail, 0f, dt * 1.8f);
        if (Mathf.Abs(la.x) + Mathf.Abs(la.z) > 25f) flail = Mathf.Max(flail, 0.5f);   // резко толкнули

        // шаги
        float amp = Mathf.Clamp01(speed / 4f) * (1f - air);
        float dirSign = lv.z < -0.3f ? -1f : 1f;
        phase += speed * dt * 2.1f * dirSign;
        float s = Mathf.Sin(phase);
        float moveYaw = speed > 0.5f ? Mathf.Atan2(lv.x, Mathf.Abs(lv.z)) * Mathf.Rad2Deg : 0f;
        hipYaw = Mathf.Lerp(hipYaw, Mathf.Clamp(moveYaw, -55f, 55f), 1f - Mathf.Exp(-8f * dt));

        // подкат и смерть
        float slide = local && movement != null ? movement.SlideLow : 0f;
        bool dead = local && health != null && health.IsDead;

        Vector3 hipPos = new Vector3(0f, -0.05f - 0.6f * slide - Mathf.Abs(Mathf.Sin(phase)) * 0.03f * amp, 0f);
        Quaternion hipRot = Quaternion.Euler(lean.x * 0.3f - 30f * slide, hipYaw, lean.y * 0.3f);
        if (dead) { hipPos = new Vector3(0f, -0.8f, 0f); hipRot = Quaternion.Euler(-80f, 0f, 0f); }

        // пинок: колено к груди, потом нога выстреливает вперёд, потом назад
        float kickT = -1f, kickBend = 0f, kick = 0f;
        if (local && KickP >= 0f)
        {
            float p = KickP;
            if (p < 0.3f) { float w = p / 0.3f; kickT = -75f * w; kickBend = 100f * w; kick = w; }
            else if (p < 0.55f) { float w = (p - 0.3f) / 0.25f; kickT = Mathf.Lerp(-75f, -105f, w); kickBend = Mathf.Lerp(100f, 0f, w * w); kick = 1f; }
            else if (p < 0.7f) { kickT = -105f; kickBend = 0f; kick = 1f; }
            else { float w = (p - 0.7f) / 0.3f; kickT = Mathf.Lerp(-105f, 0f, w); kickBend = Mathf.Lerp(0f, 20f, Mathf.Sin(w * Mathf.PI)); kick = 1f - w; }
        }

        // замах и удар — всем корпусом: оружие ушло вправо-назад — плечи развернулись туда же, ударил — провернулись следом
        Transform toolNow = hotbar != null ? hotbar.CurrentModel : null;
        if (toolNow != null && toolNow.gameObject.activeInHierarchy && !dead)
        {
            Vector3 tl = transform.InverseTransformPoint(toolNow.position);
            if (toolNow != lastTool) { toolBase = tl; lastTool = toolNow; }   // сменил оружие — не дёргаться
            toolBase = Vector3.Lerp(toolBase, tl, 1f - Mathf.Exp(-1.5f * dt));   // медленная «средняя» поза
            Vector3 off = tl - toolBase;
            Vector3 wantTurn = new Vector3(Mathf.Clamp(-off.y * 45f + off.z * 35f, -25f, 30f), Mathf.Clamp(off.x * 85f, -45f, 45f), Mathf.Clamp(-off.x * 15f, -10f, 10f));
            toolTurn = Vector3.Lerp(toolTurn, wantTurn, 1f - Mathf.Exp(-14f * dt));
        }
        else toolTurn = Vector3.Lerp(toolTurn, Vector3.zero, 1f - Mathf.Exp(-8f * dt));

        Quaternion torsoRot = Quaternion.Euler(lean.x * 0.7f - 25f * slide - 15f * kick + toolTurn.x, -hipYaw * 0.8f + s * 5f * amp + Twist + toolTurn.y, lean.y * 0.7f + toolTurn.z);
        hipRot *= Quaternion.Euler(0f, Twist * 0.4f, 0f);

        // ноги
        float thigh = 42f * amp, knee = 60f * amp;
        Quaternion tL = Quaternion.Euler(-s * thigh - 35f * air, 0f, 0f);
        Quaternion tR = Quaternion.Euler(s * thigh - 25f * air, 0f, 0f);
        Quaternion kL = Quaternion.Euler(Mathf.Max(0f, Mathf.Sin(phase - 1.2f)) * knee + 60f * air + 5f, 0f, 0f);
        Quaternion kR = Quaternion.Euler(Mathf.Max(0f, -Mathf.Sin(phase - 1.2f)) * knee + 50f * air + 5f, 0f, 0f);
        if (slide > 0.01f)
        {
            tL = Quaternion.Slerp(tL, Quaternion.Euler(-85f, 0f, 0f), slide); kL = Quaternion.Slerp(kL, Quaternion.Euler(5f, 0f, 0f), slide);
            tR = Quaternion.Slerp(tR, Quaternion.Euler(-20f, 0f, 0f), slide); kR = Quaternion.Slerp(kR, Quaternion.Euler(95f, 0f, 0f), slide);
        }

        if (kickT > -1f || kick > 0f)
        {
            tR = Quaternion.Euler(kickT, 0f, 0f); kR = Quaternion.Euler(kickBend, 0f, 0f);
            tL = Quaternion.Euler(5f * kick, 0f, 0f); kL = Quaternion.Euler(8f * kick, 0f, 0f);  // опорная нога прямая
        }

        // руки: машут при ходьбе, разлетаются от удара
        float swing = 30f * amp;
        float fl = flail * flail;
        Quaternion aL = Quaternion.Euler(s * swing - 60f * fl - 40f * air, 0f, -12f - 70f * fl - 30f * air);
        Quaternion aR = Quaternion.Euler(-s * swing - 60f * fl - 40f * air, 0f, 12f + 70f * fl + 30f * air);
        Quaternion eL = Quaternion.Euler(-20f - 15f * amp, 0f, 0f), eR = eL;

        if (kick > 0f) { aL *= Quaternion.Euler(0f, 0f, -40f * kick); aR *= Quaternion.Euler(0f, 0f, 40f * kick); } // руки в стороны — держит равновесие
        if (dead) { aL = Quaternion.Euler(-15f, 0f, -70f); aR = Quaternion.Euler(-25f, 0f, 65f); eL = Quaternion.Euler(-10f, 0f, 0f); eR = Quaternion.Euler(-30f, 0f, 0f); } // упал — руки раскинуты по земле

        // с оружием в руке (видно от третьего лица и у чужих): правая рука тянется к оружию
        Transform tool = hotbar != null ? hotbar.CurrentModel : null;
        if (local && HoldTarget.HasValue && !dead)
        {
            // держу тварь левой рукой: кисть ровно в точке захвата (локоть сгибается сам — по длине руки)
            Vector3 reachL = torso.InverseTransformPoint(HoldTarget.Value) - armL.localPosition;
            float reachLen = reachL.magnitude;
            if (reachLen > 0.01f)
            {
                const float upLen = 0.32f, loLen = 0.3f;
                float reachD = Mathf.Clamp(reachLen, 0.08f, upLen + loLen - 0.001f);
                float elbow = Mathf.Acos(Mathf.Clamp((upLen * upLen + loLen * loLen - reachD * reachD) / (2f * upLen * loLen), -1f, 1f)) * Mathf.Rad2Deg; // угол в локте
                float lift = Mathf.Acos(Mathf.Clamp((upLen * upLen + reachD * reachD - loLen * loLen) / (2f * upLen * reachD), -1f, 1f)) * Mathf.Rad2Deg;  // плечо чуть ниже прямой линии
                aL = Quaternion.FromToRotation(Vector3.down, reachL / reachLen) * Quaternion.Euler(lift, 0f, 0f);
                eL = Quaternion.Euler(-(180f - elbow), 0f, 0f);
            }
        }
        if (tool != null && tool.gameObject.activeInHierarchy && !dead)
        {
            Vector3 to = torso.InverseTransformPoint(tool.position) - armR.localPosition;
            if (local && HoldTargetR.HasValue) to = torso.InverseTransformPoint(HoldTargetR.Value) - armR.localPosition; // правая — на конце древка
            if (to.sqrMagnitude > 0.01f) Reach(to, out aR, out eR);   // кисть ровно на рукояти, локоть сгибается
            bool twoHands = hotbar.Current == Tool.Bow || hotbar.Current == Tool.Staff || hotbar.Current == Tool.Blaster;
            if (twoHands && !(local && HoldTarget.HasValue))   // левая занята тварью — оружие в одной правой
            {
                Vector3 toL = torso.InverseTransformPoint(tool.position) - armL.localPosition;
                if (toL.sqrMagnitude > 0.01f) Reach(toL, out aL, out eL);
            }
        }

        float k = 1f - Mathf.Exp(-18f * dt);
        hips.localPosition = Vector3.Lerp(hips.localPosition, hipPos, k);
        hips.localRotation = Quaternion.Slerp(hips.localRotation, hipRot, k);
        torso.localRotation = Quaternion.Slerp(torso.localRotation, torsoRot, k);
        head.localRotation = Quaternion.Slerp(head.localRotation, Quaternion.Euler(-lean.x * 0.4f, hipYaw * 0.3f, -lean.y * 0.4f), k);
        thighL.localRotation = Quaternion.Slerp(thighL.localRotation, tL, k);
        thighR.localRotation = Quaternion.Slerp(thighR.localRotation, tR, k);
        shinL.localRotation = Quaternion.Slerp(shinL.localRotation, kL, k);
        shinR.localRotation = Quaternion.Slerp(shinR.localRotation, kR, k);
        armL.localRotation = Quaternion.Slerp(armL.localRotation, aL, k);
        armR.localRotation = Quaternion.Slerp(armR.localRotation, aR, k);
        foreL.localRotation = Quaternion.Slerp(foreL.localRotation, eL, k);
        foreR.localRotation = Quaternion.Slerp(foreR.localRotation, eR, k);
    }

    // Рука тянется кистью в точку (от плеча, в осях туловища): локоть сгибается сам, по длине руки
    static void Reach(Vector3 to, out Quaternion shoulder, out Quaternion elbowRot)
    {
        const float upLen = 0.32f, loLen = 0.3f;
        float len = to.magnitude;
        float d = Mathf.Clamp(len, 0.08f, upLen + loLen - 0.001f);
        float elbow = Mathf.Acos(Mathf.Clamp((upLen * upLen + loLen * loLen - d * d) / (2f * upLen * loLen), -1f, 1f)) * Mathf.Rad2Deg;
        float lift = Mathf.Acos(Mathf.Clamp((upLen * upLen + d * d - loLen * loLen) / (2f * upLen * d), -1f, 1f)) * Mathf.Rad2Deg;
        shoulder = Quaternion.FromToRotation(Vector3.down, to / len) * Quaternion.Euler(lift, 0f, 0f);
        elbowRot = Quaternion.Euler(-(180f - elbow), 0f, 0f);
    }
}
