using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Молот (клавиша 2) — строительство.
//   Tab        — меню построек (выбрать деталь мышкой)
//   R          — повернуть на 90°
//   Деревянное строится только рядом с верстаком, каменное — рядом с каменотёсом
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
    public float stationRadius = 30f;  // как далеко можно строить от станка

    public bool IsBuilding => hotbar == null || hotbar.Current == Tool.Hammer;

    public static PlayerBuilder Local { get; private set; } // строитель своего игрока

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
    bool stationOk;
    Vector3 placePos;

    BuildPiece looked;
    BuildPiece dismantleTarget;
    float dismantleProgress;
    float nextSwing;
    string message;
    float messageUntil;

    bool menuOpen;
    string menuTab;

    static readonly Color Ok = new Color(0.3f, 1f, 0.4f, 0.4f);
    static readonly Color Bad = new Color(1f, 0.25f, 0.2f, 0.4f);

    void OnEnable() { Local = this; }
    void OnDisable() { if (Local == this) Local = null; }

    void Start()
    {
        cam = Camera.main;
        if (pieces.Count == 0 && BuildingManager.Instance != null)
            pieces = BuildingManager.Instance.catalog.FindAll(p => p != null && p.buildable);
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
            if (menuOpen) CloseMenu(true);
            if (ghost != null) { Destroy(ghost); ghostFor = -1; }
            looked = null;
            dismantleProgress = 0f;
            return;
        }

        // Меню построек
        if (kb.tabKey.wasPressedThisFrame) { if (menuOpen) CloseMenu(true); else OpenMenu(); }
        if (menuOpen && !Panels.Is("build")) menuOpen = false;
        if (menuOpen && kb.escapeKey.wasPressedThisFrame) CloseMenu(false);
        if (menuOpen)
        {
            if (ghost != null) ghost.SetActive(false);
            return;
        }

        if (kb.rKey.wasPressedThisFrame) yaw = (yaw + 90f) % 360f;
        if (ghostFor != selected) MakeGhost();

        bool locked = Cursor.lockState == CursorLockMode.Locked;
        looked = AimHelper.LookedPiece(cam, transform, reach);
        repairTarget = looked;

        // --- Починить: G (+25% прочности за 1 ресурс, из которого сделана деталь). Деревню чинить можно!
        if (locked && kb.gKey.wasPressedThisFrame && repairTarget != null) TryRepair(repairTarget);
        if (looked != null && looked.locked)
        {
            if ((mouse.rightButton.wasPressedThisFrame || kb.fKey.wasPressedThisFrame) && locked) Say("Это деревня — её разбирать нельзя, её надо защищать!");
            looked = null;
        }

        // --- Разобрать: держим ПКМ ---
        if (locked && mouse.rightButton.isPressed && looked != null)
        {
            if (looked != dismantleTarget) { dismantleTarget = looked; dismantleProgress = 0f; }
            dismantleProgress += Time.deltaTime / dismantleTime;
            if (Time.time >= nextSwing && combat != null) { combat.PlaySwing(); nextSwing = Time.time + 0.35f; }
            if (dismantleProgress >= 1f)
            {
                BuildingManager.RequestDismantle(looked);
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
            BuildingManager.RequestPickUp(looked); // хост уберёт деталь и пришлёт её нам (ReceivePiece)
            looked = null;
        }

        UpdateGhost();

        // --- Поставить: ЛКМ ---
        if (locked && mouse.leftButton.wasPressedThisFrame && canPlace)
        {
            BuildPiece p = pieces[selected];
            bool hasInHands = inventory != null && inventory.PieceCount(p.pieceId) > 0;
            if (!stationOk && !hasInHands)
            {
                Say("Нужен " + BuildPiece.StationName(p.requiredStation).ToLower() + " рядом (до " + stationRadius + " м)");
                return;
            }
            bool fromHands = inventory != null && inventory.TakePiece(p.pieceId);
            if (fromHands || inventory == null || inventory.Spend(p))
            {
                BuildingManager.RequestPlace(p, placePos, Quaternion.Euler(0f, yaw, 0f));
                if (combat != null) combat.PlaySwing();
            }
            else Say("Не хватает ресурсов: нужно " + p.CostText);
        }
    }

    // Деталь, которую мы взяли, пришла в руки
    public void ReceivePiece(BuildPiece prefab)
    {
        if (inventory != null) inventory.AddPiece(prefab.pieceId, prefab.displayName);
        int index = pieces.FindIndex(p => p.pieceId == prefab.pieceId);
        if (index >= 0) selected = index;
        Say("Взял: " + prefab.displayName);
    }

    BuildPiece repairTarget;

    void TryRepair(BuildPiece p)
    {
        if (p.Health >= p.maxHealth - 0.5f) { Say("Целая — чинить не надо"); return; }
        if (p.costTypes.Length == 0) { BuildingManager.RequestRepair(p); return; }
        ResourceType t = p.costTypes[0];
        if (inventory != null && !inventory.Take(t, 1)) { Say("Для починки нужен 1 ресурс: " + ResourcePickup.Name(t).ToLower()); return; }
        BuildingManager.RequestRepair(p);
        if (combat != null) combat.PlaySwing();
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

        bool inHands = inventory != null && inventory.PieceCount(piece.pieceId) > 0;
        bool affordable = inventory == null || inHands || inventory.CanAfford(piece);
        stationOk = PlayerInteract.StationNear(piece.requiredStation, p, stationRadius);
        if (ghostMat != null)
        {
            Color c = canPlace && affordable && (stationOk || inHands) ? Ok : Bad;
            ghostMat.SetColor("_BaseColor", c);
            ghostMat.SetColor("_Color", c);
        }
    }

    void OpenMenu()
    {
        if (!Panels.Open("build")) return;
        menuOpen = true;
        if (menuTab == null) menuTab = pieces[selected].category;
    }

    void CloseMenu(bool relock)
    {
        menuOpen = false;
        Panels.Close("build", relock);
    }

    void OnGUI()
    {
        if (!IsBuilding) return;

        if (menuOpen) { DrawMenu(); return; }

        // Короткая подсказка над панелью предметов
        BuildPiece cur = pieces[selected];
        int have = inventory != null ? inventory.PieceCount(cur.pieceId) : 0;
        string info = cur.displayName + "  ·  " + (have > 0 ? "в руках: " + have : cur.CostText);
        if (have == 0 && !stationOk && cur.requiredStation != StationType.None)
            info += "  ·  нужен " + BuildPiece.StationName(cur.requiredStation).ToLower() + " рядом";
        GUI.Label(new Rect(0, Screen.height - 112, Screen.width, 26), info, Hud.Center);
        GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                  "Tab — постройки   R — повернуть   ЛКМ — поставить   ПКМ (держать) — разобрать   F — взять   G — починить", Hud.CenterTiny);

        // Что под прицелом
        BuildPiece shown = looked != null ? looked : repairTarget;
        if (shown != null)
        {
            Rect r = new Rect(Screen.width / 2f - 100, Screen.height / 2f + 40, 200, 10);
            if (dismantleProgress > 0f) Hud.Bar(r, dismantleProgress, new Color(1f, 0.7f, 0.2f), shown.displayName);
            else Hud.Bar(r, shown.Health / shown.maxHealth, new Color(0.4f, 0.85f, 0.4f),
                         shown.displayName + "  ·  прочность " + Mathf.CeilToInt(shown.Health) + "/" + Mathf.CeilToInt(shown.maxHealth));
        }

        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 300, Screen.height / 2f - 70, 600, 30), message, Hud.CenterBig);
    }

    // Меню построек: вкладки по материалу и карточки деталей
    void DrawMenu()
    {
        float W = Mathf.Min(860f, Screen.width - 40f), H = Mathf.Min(500f, Screen.height - 60f);
        Rect panel = new Rect((Screen.width - W) / 2f, (Screen.height - H) / 2f, W, H);
        Hud.Fill(panel, new Color(0.09f, 0.075f, 0.06f, 0.94f), 14);

        GUI.Label(new Rect(panel.x + 24, panel.y + 16, 300, 30), "Постройки", Hud.Big);
        if (inventory != null)
            GUI.Label(new Rect(panel.x + 200, panel.y + 24, W - 224, 24), inventory.Summary(), Hud.Tiny);

        // Вкладки
        var cats = new List<string>();
        foreach (BuildPiece p in pieces) if (!cats.Contains(p.category)) cats.Add(p.category);
        float tx = panel.x + 24;
        foreach (string c in cats)
        {
            Rect tab = new Rect(tx, panel.y + 58, 130, 34);
            bool on = c == menuTab;
            Hud.Fill(tab, on ? new Color(0.85f, 0.65f, 0.2f, 0.95f) : new Color(1f, 1f, 1f, 0.08f), 8);
            GUI.Label(tab, c, Hud.Center);
            if (GUI.Button(tab, GUIContent.none, GUIStyle.none)) menuTab = c;
            tx += 140;
        }

        // Карточки
        const int cols = 4;
        float gap = 12f;
        float cardW = (W - 48f - gap * (cols - 1)) / cols, cardH = 92f;
        float top = panel.y + 108;
        int k = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            BuildPiece p = pieces[i];
            if (p.category != menuTab) continue;
            Rect card = new Rect(panel.x + 24 + (k % cols) * (cardW + gap), top + (k / cols) * (cardH + gap), cardW, cardH);
            k++;

            bool sel = i == selected;
            int have = inventory != null ? inventory.PieceCount(p.pieceId) : 0;
            bool afford = inventory == null || have > 0 || inventory.CanAfford(p);

            if (sel) Hud.Fill(new Rect(card.x - 3, card.y - 3, card.width + 6, card.height + 6), new Color(0.95f, 0.75f, 0.25f), 12);
            Hud.Fill(card, card.Contains(Event.current.mousePosition) ? new Color(1f, 1f, 1f, 0.16f) : new Color(1f, 1f, 1f, 0.07f), 10);
            Color stripe = p.category == "Камень" ? new Color(0.6f, 0.6f, 0.65f) : p.category == "Станки" ? new Color(0.85f, 0.6f, 0.2f) : new Color(0.6f, 0.42f, 0.22f);
            Hud.Fill(new Rect(card.x, card.y, 6, card.height), stripe, 3);

            GUI.Label(new Rect(card.x + 16, card.y + 8, card.width - 24, 44), p.displayName, Hud.Title);
            if (p.requiredStation != StationType.None)
                GUI.Label(new Rect(card.x + 16, card.y + card.height - 50, card.width - 24, 20), "у станка: " + BuildPiece.StationName(p.requiredStation).ToLower(), Hud.Tiny);
            Color old = GUI.color;
            GUI.color = have > 0 ? new Color(0.6f, 0.85f, 1f) : afford ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.5f, 0.45f);
            GUI.Label(new Rect(card.x + 16, card.y + card.height - 30, card.width - 24, 24), have > 0 ? "в руках: " + have : p.CostText, Hud.Small);
            GUI.color = old;

            if (GUI.Button(card, GUIContent.none, GUIStyle.none))
            {
                selected = i;
                CloseMenu(true);
            }
        }

        GUI.Label(new Rect(panel.x, panel.yMax - 34, W, 24), "Клик по детали — выбрать      Tab / Esc — закрыть", Hud.CenterTiny);
    }
}
