using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Рецепт станка: из чего -> что получится
public class Recipe
{
    public string name;
    public StationType station;
    public ResourceType[] inTypes;
    public int[] inAmounts;
    public ResourceType outType;
    public int outAmount;

    public string InText()
    {
        string s = "";
        for (int i = 0; i < inTypes.Length; i++)
            s += (s.Length > 0 ? " + " : "") + inAmounts[i] + " " + ResourcePickup.Name(inTypes[i]).ToLower();
        return s;
    }

    public int MaxTimes(Inventory inv)
    {
        int max = 999;
        for (int i = 0; i < inTypes.Length; i++) max = Mathf.Min(max, inv.Count(inTypes[i]) / inAmounts[i]);
        return max;
    }

    public bool Craft(Inventory inv, int times)
    {
        times = Mathf.Min(times, MaxTimes(inv));
        if (times <= 0) return false;
        for (int i = 0; i < inTypes.Length; i++) inv.Take(inTypes[i], inAmounts[i] * times);
        inv.Add(outType, outAmount * times, true);
        return true;
    }

    // Все рецепты игры. Новые добавляем сюда.
    public static readonly List<Recipe> All = new List<Recipe>
    {
        new Recipe { name = "Доски", station = StationType.Workbench,
                     inTypes = new[] { ResourceType.Wood }, inAmounts = new[] { 2 },
                     outType = ResourceType.Plank, outAmount = 4 },
        new Recipe { name = "Каменный блок", station = StationType.Stonecutter,
                     inTypes = new[] { ResourceType.Stone }, inAmounts = new[] { 3 },
                     outType = ResourceType.StoneBlock, outAmount = 2 },
        new Recipe { name = "Блоки из обломков", station = StationType.Stonecutter,
                     inTypes = new[] { ResourceType.Stone, ResourceType.Plank }, inAmounts = new[] { 5, 1 },
                     outType = ResourceType.StoneBlock, outAmount = 4 },
    };
}

// E — работать на станке, на который смотришь (верстак, каменотёс).
// Открывается окно с рецептами: жмёшь «×1», «×5» или «Всё».
public class PlayerInteract : MonoBehaviour
{
    public float reach = 4f;

    Camera cam;
    Inventory inventory;
    BuildPiece lookedStation;
    BuildPiece openStation;
    string message;
    float messageUntil;

    void Start()
    {
        cam = Camera.main;
        inventory = GetComponent<Inventory>();
    }

    void OnDisable() { if (openStation != null) Close(false); }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || inventory == null) return;

        if (openStation != null)
        {
            if (!Panels.Is("station")) { openStation = null; return; }
            bool far = openStation == null || !openStation.enabled ||
                       Vector3.Distance(openStation.Center, transform.position) > reach + 3f;
            if (kb.eKey.wasPressedThisFrame || far) Close(true);
            else if (kb.escapeKey.wasPressedThisFrame) Close(false);
            return;
        }

        lookedStation = null;
        if (Cursor.lockState == CursorLockMode.Locked && AimHelper.Aim(cam, transform, reach, out RaycastHit hit))
        {
            BuildPiece bp = hit.collider.GetComponentInParent<BuildPiece>();
            if (bp != null && bp.station != StationType.None) lookedStation = bp;
        }

        if (lookedStation != null && kb.eKey.wasPressedThisFrame && Panels.Open("station"))
            openStation = lookedStation;
    }

    void Close(bool relock)
    {
        openStation = null;
        Panels.Close("station", relock);
    }

    // Есть ли станок нужного вида рядом с точкой (для стройки)
    public static bool StationNear(StationType type, Vector3 pos, float radius)
    {
        if (type == StationType.None) return true;
        foreach (BuildPiece p in BuildPiece.All)
            if (p.station == type && (p.transform.position - pos).sqrMagnitude < radius * radius) return true;
        return false;
    }

    void OnGUI()
    {
        if (openStation != null) DrawPanel();
        else if (lookedStation != null && !Panels.Any)
            GUI.Label(new Rect(0, Screen.height / 2f + 60, Screen.width, 26),
                      "E — " + BuildPiece.StationName(lookedStation.station), Hud.Center);
        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 300, Screen.height / 2f - 70, 600, 30), message, Hud.CenterBig);
    }

    void DrawPanel()
    {
        var list = Recipe.All.FindAll(r => r.station == openStation.station);
        float W = Mathf.Min(640f, Screen.width - 40f), H = Mathf.Min(150f + list.Count * 96f, Screen.height - 60f);
        Rect panel = new Rect((Screen.width - W) / 2f, (Screen.height - H) / 2f, W, H);
        Hud.Fill(panel, new Color(0.09f, 0.07f, 0.05f, 0.95f), 14);
        GUI.Label(new Rect(panel.x + 24, panel.y + 16, 400, 30), BuildPiece.StationName(openStation.station), Hud.Big);
        GUI.Label(new Rect(panel.x + 24, panel.y + 48, W - 48, 24), inventory.Summary(), Hud.Tiny);

        float y = panel.y + 80;
        foreach (Recipe r in list)
        {
            Rect card = new Rect(panel.x + 24, y, W - 48, 84);
            Hud.Fill(card, new Color(1f, 1f, 1f, 0.07f), 10);
            Hud.Fill(new Rect(card.x + 14, card.y + 20, 44, 44), ResourcePickup.ColorOf(r.outType), 6);
            GUI.Label(new Rect(card.x + 72, card.y + 10, 300, 28), r.name + "  ×" + r.outAmount, Hud.Title);
            int can = r.MaxTimes(inventory);
            Color old = GUI.color;
            GUI.color = can > 0 ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.5f, 0.45f);
            GUI.Label(new Rect(card.x + 72, card.y + 44, 300, 24), "нужно: " + r.InText(), Hud.Small);
            GUI.color = old;

            float bx = card.xMax - 3 * 78;
            DrawButton(new Rect(bx, card.y + 22, 70, 40), "×1", r, 1, can);
            DrawButton(new Rect(bx + 78, card.y + 22, 70, 40), "×5", r, 5, can);
            DrawButton(new Rect(bx + 156, card.y + 22, 70, 40), "Всё", r, 999, can);
            y += 96;
        }

        GUI.Label(new Rect(panel.x, panel.yMax - 34, W, 24), "E / Esc — закрыть", Hud.CenterTiny);
    }

    void DrawButton(Rect b, string text, Recipe r, int times, int can)
    {
        bool ok = can > 0;
        bool hover = b.Contains(Event.current.mousePosition);
        Hud.Fill(b, ok ? (hover ? new Color(0.95f, 0.75f, 0.3f) : new Color(0.75f, 0.55f, 0.2f)) : new Color(1f, 1f, 1f, 0.08f), 8);
        GUI.Label(b, text, Hud.Center);
        if (GUI.Button(b, GUIContent.none, GUIStyle.none))
        {
            if (!r.Craft(inventory, times)) { message = "Не хватает: " + r.InText(); messageUntil = Time.time + 1.5f; }
        }
    }
}
