using UnityEngine;
using UnityEngine.InputSystem;

// Удар мечом: левая кнопка мыши. Бьёт то, что в центре экрана (прицел).
// Пока умеет ломать постройки. Потом добавим врагов.
// Ещё рисует прицел и прочность детали, на которую смотришь.
public class PlayerCombat : MonoBehaviour
{
    public float damage = 34f;     // урон за удар
    public float range = 3f;       // дальность удара
    public float cooldown = 0.45f; // пауза между ударами (сек)
    public Transform weapon;       // модель меча (для взмаха)

    Camera cam;
    PlayerBuilder builder;
    BuildPiece looked;
    float nextHitTime;
    float swing;                   // 1 — взмах только начался, 0 — меч в покое
    Quaternion weaponRestRotation;

    void Start()
    {
        cam = Camera.main;
        builder = GetComponent<PlayerBuilder>();
        if (weapon != null) weaponRestRotation = weapon.localRotation;
    }

    void Update()
    {
        bool building = builder != null && builder.IsBuilding;
        if (weapon != null) weapon.gameObject.SetActive(!building);

        looked = building ? null : AimHelper.LookedPiece(cam, transform, range);

        Mouse mouse = Mouse.current;
        if (!building && mouse != null && Cursor.lockState == CursorLockMode.Locked &&
            mouse.leftButton.wasPressedThisFrame && Time.time >= nextHitTime)
        {
            nextHitTime = Time.time + cooldown;
            swing = 1f;
            if (looked != null) BuildingManager.Damage(looked, damage);
        }

        // Анимация взмаха
        if (weapon != null)
        {
            swing = Mathf.MoveTowards(swing, 0f, Time.deltaTime / cooldown);
            float a = Mathf.Sin(swing * Mathf.PI) * 80f;
            weapon.localRotation = weaponRestRotation * Quaternion.Euler(a, -a * 0.4f, 0f);
        }
    }

    void OnGUI()
    {
        // Прицел
        GUI.Label(new Rect(Screen.width / 2f - 6, Screen.height / 2f - 12, 20, 24), "+", Hud.Big);

        if (looked == null) return;
        string text = looked.displayName + "   " + Mathf.CeilToInt(looked.Health) + " / " + looked.maxHealth;
        Hud.Bar(new Rect(Screen.width / 2f - 120, Screen.height / 2f + 30, 240, 14), looked.Health / looked.maxHealth, text);
    }
}

// Общие помощники: куда смотрит игрок, рисование подсказок на экране
public static class AimHelper
{
    // Луч из камеры в центр экрана. Пропускает самого игрока.
    public static bool Aim(Camera cam, Transform self, float reach, out RaycastHit result)
    {
        result = default;
        if (cam == null) return false;
        // В виде от 3-го лица камера стоит дальше — добавляем это расстояние
        Vector3 head = cam.transform.parent != null ? cam.transform.parent.position : cam.transform.position;
        float extra = Vector3.Distance(cam.transform.position, head);
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        RaycastHit[] hits = Physics.RaycastAll(ray, reach + extra, ~(1 << 2), QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        foreach (RaycastHit h in hits)
        {
            if (h.transform.root == self.root) continue;
            if (h.distance < best) { best = h.distance; result = h; found = true; }
        }
        return found;
    }

    public static BuildPiece LookedPiece(Camera cam, Transform self, float reach)
    {
        return Aim(cam, self, reach, out RaycastHit h) ? h.collider.GetComponentInParent<BuildPiece>() : null;
    }
}

public static class Hud
{
    static GUIStyle big, small;
    public static GUIStyle Big => big ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
    public static GUIStyle Small => small ??= new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };

    public static void Bar(Rect r, float fill, string text)
    {
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(0, 0, 0, 0.6f), 0, 0);
        Rect f = new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height);
        GUI.DrawTexture(f, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, Color.Lerp(Color.red, Color.green, fill), 0, 0);
        GUI.Label(new Rect(r.x, r.y - 24, r.width + 200, 24), text, Small);
    }
}
