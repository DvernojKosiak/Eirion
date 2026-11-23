using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Режим стройки.
//   B          — включить / выключить стройку
//   1..9, Q/E  — выбрать деталь
//   R          — повернуть на 90°
//   ЛКМ        — поставить (если призрак зелёный)
//   X          — снести деталь, на которую смотришь
// Вешается на Player.
public class PlayerBuilder : MonoBehaviour
{
    public List<BuildPiece> pieces = new List<BuildPiece>(); // что можно строить
    public Material ghostMaterial;  // полупрозрачный материал для «призрака»
    public float reach = 12f;       // как далеко можно строить
    public float gridSize = 1f;     // шаг сетки в метрах

    public bool IsBuilding { get; private set; }

    Camera cam;
    int selected;
    float yaw;
    GameObject ghost;
    Material ghostMat;
    bool canPlace;
    Vector3 placePos;

    static readonly Color Ok = new Color(0.3f, 1f, 0.4f, 0.4f);
    static readonly Color Bad = new Color(1f, 0.25f, 0.2f, 0.4f);

    void Start() { cam = Camera.main; }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || pieces.Count == 0) return;

        if (kb.bKey.wasPressedThisFrame) SetBuilding(!IsBuilding);
        if (!IsBuilding) return;

        for (int i = 0; i < Mathf.Min(9, pieces.Count); i++)
            if (kb[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame) Select(i);
        if (kb.qKey.wasPressedThisFrame) Select((selected - 1 + pieces.Count) % pieces.Count);
        if (kb.eKey.wasPressedThisFrame) Select((selected + 1) % pieces.Count);
        if (kb.rKey.wasPressedThisFrame) yaw = (yaw + 90f) % 360f;

        UpdateGhost();

        bool locked = Cursor.lockState == CursorLockMode.Locked;
        if (locked && mouse != null && mouse.leftButton.wasPressedThisFrame && canPlace)
            BuildingManager.Place(pieces[selected], placePos, Quaternion.Euler(0f, yaw, 0f));

        if (locked && kb.xKey.wasPressedThisFrame)
            BuildingManager.Demolish(AimHelper.LookedPiece(cam, transform, reach));
    }

    void SetBuilding(bool on)
    {
        IsBuilding = on;
        if (on) Select(selected);
        else if (ghost != null) Destroy(ghost);
    }

    void Select(int index)
    {
        selected = index;
        if (ghost != null) Destroy(ghost);
        ghost = MakeGhost(pieces[selected]);
    }

    // Призрак — копия детали без коллайдеров и прочности, полупрозрачная
    GameObject MakeGhost(BuildPiece prefab)
    {
        GameObject holder = new GameObject("GhostHolder");
        holder.SetActive(false); // чтобы копия не «ожила» раньше времени
        GameObject g = Instantiate(prefab.gameObject, holder.transform);
        DestroyImmediate(g.GetComponent<BuildPiece>());
        foreach (Collider c in g.GetComponentsInChildren<Collider>()) DestroyImmediate(c);

        if (ghostMat == null && ghostMaterial != null) ghostMat = new Material(ghostMaterial);
        foreach (Renderer r in g.GetComponentsInChildren<Renderer>())
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = ghostMat;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        g.name = "Ghost";
        g.transform.SetParent(null);
        Destroy(holder);
        g.SetActive(true);
        return g;
    }

    void UpdateGhost()
    {
        if (ghost == null) return;
        BuildPiece piece = pieces[selected];
        canPlace = false;

        if (!AimHelper.Aim(cam, transform, reach, out RaycastHit hit))
        {
            ghost.SetActive(false);
            return;
        }
        ghost.SetActive(true);

        Vector3 p = hit.point + hit.normal * 0.05f;
        p.x = Mathf.Round(p.x / gridSize) * gridSize;
        p.z = Mathf.Round(p.z / gridSize) * gridSize;

        if (hit.normal.y > 0.7f)
            p.y = hit.point.y; // ставим сверху того, во что смотрим
        else if (Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out RaycastHit down, 50f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            p.y = down.point.y; // смотрим в стену — ставим рядом на землю
        else
            p.y = hit.point.y;

        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        ghost.transform.SetPositionAndRotation(p, rot);
        placePos = p;

        // Нельзя ставить, если сильно пересекается с чем-то (допускаем небольшое касание)
        Vector3 center = p + Vector3.up * (piece.size.y * 0.5f);
        canPlace = !Physics.CheckBox(center, piece.size * 0.4f, rot, ~(1 << 2), QueryTriggerInteraction.Ignore);

        if (ghostMat != null)
        {
            Color c = canPlace ? Ok : Bad;
            ghostMat.SetColor("_BaseColor", c);
            ghostMat.SetColor("_Color", c);
        }
    }

    void OnGUI()
    {
        if (!IsBuilding)
        {
            GUI.Label(new Rect(12, Screen.height - 34, 600, 30), "B — стройка", Hud.Small);
            return;
        }

        float y = 50;
        GUI.Label(new Rect(12, y, 500, 30), "СТРОЙКА  (B — выйти)", Hud.Big);
        y += 34;
        for (int i = 0; i < pieces.Count; i++)
        {
            string line = (i < 9 ? (i + 1) + ". " : "   ") + pieces[i].displayName + "  (" + pieces[i].maxHealth + ")";
            Color old = GUI.color;
            GUI.color = i == selected ? Color.yellow : new Color(1f, 1f, 1f, 0.75f);
            GUI.Label(new Rect(16, y, 500, 24), (i == selected ? "> " : "  ") + line, Hud.Small);
            GUI.color = old;
            y += 22;
        }
        y += 10;
        GUI.Label(new Rect(16, y, 700, 24), "ЛКМ — поставить   R — повернуть   Q/E — выбрать   X — снести", Hud.Small);
    }
}
