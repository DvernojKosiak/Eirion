using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Молот (клавиша 2) — строительство.
//   Q / E      — выбрать деталь
//   R          — повернуть на 90°
//   ЛКМ        — поставить (берётся из инвентаря, если есть, иначе тратятся ресурсы)
//   ПКМ (держать) — разобрать деталь под прицелом, выпадут ресурсы
//   F          — взять деталь целиком в руки, чтобы поставить в другом месте
public class PlayerBuilder : MonoBehaviour
{
    public List<BuildPiece> pieces = new List<BuildPiece>(); // что можно строить
    public Material ghostMaterial;
    public float reach = 12f;
    public float gridSize = 1f;
    public float dismantleTime = 1.2f; // сколько держать ПКМ

    public bool IsBuilding => hotbar == null || hotbar.Current == Tool.Hammer;

    Camera cam;
    PlayerHotbar hotbar;
    PlayerCombat combat;
    Inventory inventory;

    int selected;
    float yaw;
    GameObject ghost;
    int ghostFor = -1;
    Material ghostMat;
    bool canPlace;
    Vector3 placePos;

    BuildPiece looked;
    BuildPiece dismantleTarget;
    float dismantleProgress;
    float nextSwing;
    string message;
    float messageUntil;

    static readonly Color Ok = new Color(0.3f, 1f, 0.4f, 0.4f);
    static readonly Color Bad = new Color(1f, 0.25f, 0.2f, 0.4f);

    void Start()
    {
        cam = Camera.main;
        hotbar = GetComponent<PlayerHotbar>();
        combat = GetComponent<PlayerCombat>();
        inventory = GetComponent<Inventory>();
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb == null || mouse == null || pieces.Count == 0) return;

        if (!IsBuilding)
        {
            if (ghost != null) { Destroy(ghost); ghostFor = -1; }
            looked = null;
            dismantleProgress = 0f;
            return;
        }

        if (kb.qKey.wasPressedThisFrame) selected = (selected - 1 + pieces.Count) % pieces.Count;
        if (kb.eKey.wasPressedThisFrame) selected = (selected + 1) % pieces.Count;
        if (kb.rKey.wasPressedThisFrame) yaw = (yaw + 90f) % 360f;
        if (ghostFor != selected) MakeGhost();

        bool locked = Cursor.lockState == CursorLockMode.Locked;
        looked = AimHelper.LookedPiece(cam, transform, reach);

        // --- Разобрать: держим ПКМ ---
        if (locked && mouse.rightButton.isPressed && looked != null)
        {
            if (looked != dismantleTarget) { dismantleTarget = looked; dismantleProgress = 0f; }
            dismantleProgress += Time.deltaTime / dismantleTime;
            if (Time.time >= nextSwing && combat != null) { combat.PlaySwing(); nextSwing = Time.time + 0.35f; }
            if (dismantleProgress >= 1f)
            {
                BuildingManager.Dismantle(looked, 1f);
                dismantleTarget = null;
                dismantleProgress = 0f;
            }
            if (ghost != null) ghost.SetActive(false);
            return;
        }
        dismantleTarget = null;
        dismantleProgress = 0f;

        // --- Взять в руки: F ---
        if (locked && kb.fKey.wasPressedThisFrame && looked != null)
        {
            string id = BuildingManager.PickUp(looked);
            if (inventory != null) inventory.AddPiece(id);
            int index = pieces.FindIndex(p => p.pieceId == id);
            if (index >= 0) selected = index;
            Say("Взял: " + (index >= 0 ? pieces[index].displayName : id));
            looked = null;
            if (ghostFor != selected) MakeGhost();
        }

        UpdateGhost();

        // --- Поставить: ЛКМ ---
        if (locked && mouse.leftButton.wasPressedThisFrame && canPlace)
        {
            BuildPiece p = pieces[selected];
            bool fromHands = inventory != null && inventory.TakePiece(p.pieceId);
            if (fromHands || inventory == null || inventory.Spend(p))
            {
                BuildingManager.Place(p, placePos, Quaternion.Euler(0f, yaw, 0f));
                if (combat != null) combat.PlaySwing();
            }
            else Say("Не хватает ресурсов: нужно " + p.CostText);
        }
    }

    void Say(string text) { message = text; messageUntil = Time.time + 2f; }

    // Призрак — копия детали без коллайдеров, полупрозрачная
    void MakeGhost()
    {
        if (ghost != null) Destroy(ghost);
        ghostFor = selected;
        BuildPiece prefab = pieces[selected];

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
        ghost = g;
    }

    void UpdateGhost()
    {
        canPlace = false;
        if (ghost == null) return;
        BuildPiece piece = pieces[selected];

        if (!AimHelper.Aim(cam, transform, reach, out RaycastHit hit)) { ghost.SetActive(false); return; }
        ghost.SetActive(true);

        Vector3 p = hit.point + hit.normal * 0.05f;
        p.x = Mathf.Round(p.x / gridSize) * gridSize;
        p.z = Mathf.Round(p.z / gridSize) * gridSize;

        if (hit.normal.y > 0.7f)
            p.y = hit.point.y; // сверху того, во что смотрим
        else if (Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out RaycastHit down, 50f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            p.y = down.point.y; // смотрим в стену — ставим рядом
        else
            p.y = hit.point.y;

        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        ghost.transform.SetPositionAndRotation(p, rot);
        placePos = p;

        // Нельзя, если сильно пересекается с чем-то (лёгкое касание можно)
        Vector3 center = p + Vector3.up * (piece.size.y * 0.5f);
        canPlace = !Physics.CheckBox(center, piece.size * 0.4f, rot, ~(1 << 2), QueryTriggerInteraction.Ignore);

        bool affordable = inventory == null || inventory.PieceCount(piece.pieceId) > 0 || inventory.CanAfford(piece);
        if (ghostMat != null)
        {
            Color c = canPlace && affordable ? Ok : Bad;
            ghostMat.SetColor("_BaseColor", c);
            ghostMat.SetColor("_Color", c);
        }
    }

    void OnGUI()
    {
        if (!IsBuilding) return;

        // Список деталей слева
        float y = 70;
        GUI.Label(new Rect(12, y, 500, 30), "МОЛОТ — стройка", Hud.Big);
        y += 32;
        string lastCat = null;
        for (int i = 0; i < pieces.Count; i++)
        {
            BuildPiece p = pieces[i];
            if (p.category != lastCat)
            {
                lastCat = p.category;
                GUI.Label(new Rect(16, y, 300, 22), "— " + p.category + " —", Hud.Small);
                y += 20;
            }
            int have = inventory != null ? inventory.PieceCount(p.pieceId) : 0;
            string line = (i == selected ? "> " : "   ") + p.displayName + "   " + (have > 0 ? "в руках: " + have : p.CostText);
            Color old = GUI.color;
            GUI.color = i == selected ? Color.yellow : new Color(1f, 1f, 1f, 0.7f);
            GUI.Label(new Rect(16, y, 600, 22), line, Hud.Small);
            GUI.color = old;
            y += 19;
        }
        y += 8;
        GUI.Label(new Rect(16, y, 900, 22), "Q/E — выбрать   R — повернуть   ЛКМ — поставить", Hud.Small);
        GUI.Label(new Rect(16, y + 20, 900, 22), "ПКМ (держать) — разобрать   F — взять в руки", Hud.Small);

        // Что под прицелом
        if (looked != null)
        {
            Rect r = new Rect(Screen.width / 2f - 120, Screen.height / 2f + 40, 240, 12);
            Hud.Bar(r, dismantleProgress, new Color(1f, 0.7f, 0.2f),
                    looked.displayName + "  —  ПКМ держать: разобрать,  F: взять");
        }

        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 200, Screen.height / 2f - 70, 600, 30), message, Hud.Big);
    }
}
