using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Инвентарь игрока: ресурсы (брёвна, камень, доски, блоки, лайлилиум) и детали «в руках».
//   I — открыть сумку.
// Ресурсы, которые лежат рядом, сами прилетают в сумку, справа всплывает «+2 Бревно».
public class Inventory : MonoBehaviour
{
    public int startLogs = 10;      // с чем высаживаемся с дирижабля
    public int startArrows = 20;
    public float pickupRadius = 3f;

    readonly Dictionary<ResourceType, int> res = new Dictionary<ResourceType, int>();
    readonly Dictionary<string, int> pieces = new Dictionary<string, int>();
    readonly Dictionary<string, string> pieceNames = new Dictionary<string, string>();

    // Всплывающие «+N ресурс»
    class Note { public ResourceType type; public int amount; public float time; }
    readonly List<Note> notes = new List<Note>();

    bool open;

    void Start()
    {
        Add(ResourceType.Wood, startLogs, false);
        Add(ResourceType.Arrow, startArrows, false);
    }

    // ---------- ресурсы ----------
    public int Count(ResourceType t) => res.TryGetValue(t, out int n) ? n : 0;

    public void Add(ResourceType t, int amount, bool notify = true)
    {
        if (amount == 0) return;
        res[t] = Mathf.Max(0, Count(t) + amount);
        if (!notify || amount < 0) return;
        Note last = notes.Count > 0 ? notes[notes.Count - 1] : null;
        if (last != null && last.type == t && Time.time - last.time < 1.5f) { last.amount += amount; last.time = Time.time; }
        else notes.Add(new Note { type = t, amount = amount, time = Time.time });
        if (notes.Count > 6) notes.RemoveAt(0);
    }

    public bool Has(ResourceType t, int amount) => Count(t) >= amount;

    public bool Take(ResourceType t, int amount)
    {
        if (!Has(t, amount)) return false;
        res[t] = Count(t) - amount;
        return true;
    }

    public bool CanAfford(BuildPiece p)
    {
        for (int i = 0; i < p.costTypes.Length && i < p.costAmounts.Length; i++)
            if (Count(p.costTypes[i]) < p.costAmounts[i]) return false;
        return true;
    }

    public bool Spend(BuildPiece p)
    {
        if (!CanAfford(p)) return false;
        for (int i = 0; i < p.costTypes.Length && i < p.costAmounts.Length; i++)
            res[p.costTypes[i]] = Count(p.costTypes[i]) - p.costAmounts[i];
        return true;
    }

    // Короткая строка для HUD: «Бревно 10 · Доски 4»
    public string Summary()
    {
        string s = "";
        foreach (ResourceType t in System.Enum.GetValues(typeof(ResourceType)))
        {
            int n = Count(t);
            if (n > 0) s += (s.Length > 0 ? "   ·   " : "") + ResourcePickup.Name(t) + " " + n;
        }
        return s.Length > 0 ? s : "Сумка пуста";
    }

    // При смерти: половина каждого ресурса выпадает на землю. true — если что-то выпало
    public bool DropHalf(Vector3 position)
    {
        bool any = false;
        foreach (ResourceType t in System.Enum.GetValues(typeof(ResourceType)))
        {
            int drop = Count(t) / 2;
            if (drop <= 0) continue;
            res[t] = Count(t) - drop;
            ResourcePickup.Spawn(t, drop, position);
            any = true;
        }
        return any;
    }

    // ---------- детали в руках ----------
    public int PieceCount(string id) => id != null && pieces.TryGetValue(id, out int n) ? n : 0;

    public void AddPiece(string id, string displayName = null)
    {
        if (string.IsNullOrEmpty(id)) return;
        pieces[id] = PieceCount(id) + 1;
        if (displayName != null) pieceNames[id] = displayName;
    }

    public bool TakePiece(string id)
    {
        int n = PieceCount(id);
        if (n <= 0) return false;
        pieces[id] = n - 1;
        return true;
    }

    // ---------- сбор ----------
    void Update()
    {
        Vector3 me = transform.position;
        for (int i = ResourcePickup.All.Count - 1; i >= 0; i--)
        {
            ResourcePickup p = ResourcePickup.All[i];
            if (!p.CanCollect) continue;
            float d = Vector3.Distance(p.transform.position, me);
            if (d > pickupRadius) continue;
            if (d < 0.9f)
            {
                Add(p.type, p.amount, true);
                Destroy(p.gameObject);
                continue;
            }
            Rigidbody rb = p.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            p.transform.position = Vector3.MoveTowards(p.transform.position, me, 10f * Time.deltaTime);
        }

        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.iKey.wasPressedThisFrame)
        {
            if (open) Close(true);
            else if (Panels.Open("inventory")) open = true;
        }
        if (open && kb.escapeKey.wasPressedThisFrame) Close(false);
        if (open && !Panels.Is("inventory")) open = false;
    }

    void Close(bool relock)
    {
        open = false;
        Panels.Close("inventory", relock);
    }

    void OnDisable() { if (open) Close(false); }

    // ---------- экран ----------
    void OnGUI()
    {
        // Строка ресурсов слева сверху
        GUI.Label(new Rect(12, 36, 900, 26), Summary() + "      (I — сумка)", Hud.Small);

        // Всплывашки справа
        float y = Screen.height * 0.35f;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            Note n = notes[i];
            float age = Time.time - n.time;
            if (age > 3f) { if (Event.current.type == EventType.Repaint) notes.RemoveAt(i); continue; }
            float a = Mathf.Clamp01(3f - age);
            Rect r = new Rect(Screen.width - 230, y, 210, 30);
            Hud.Fill(r, new Color(0f, 0f, 0f, 0.55f * a), 8);
            Color c = ResourcePickup.ColorOf(n.type); c.a = a;
            Hud.Fill(new Rect(r.x + 8, r.y + 7, 16, 16), c, 4);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.Label(new Rect(r.x + 32, r.y + 3, 180, 24), "+" + n.amount + "  " + ResourcePickup.Name(n.type), Hud.Small);
            GUI.color = old;
            y += 36;
        }

        if (open) DrawPanel();
    }

    void DrawPanel()
    {
        float W = Mathf.Min(720f, Screen.width - 40f), H = Mathf.Min(560f, Screen.height - 60f);
        Rect panel = new Rect((Screen.width - W) / 2f, (Screen.height - H) / 2f, W, H);
        Hud.Fill(panel, new Color(0.07f, 0.06f, 0.07f, 0.95f), 14);
        GUI.Label(new Rect(panel.x + 24, panel.y + 16, 300, 30), "Сумка", Hud.Big);

        // Плитки ресурсов
        float tile = 112f, gap = 12f;
        float x = panel.x + 24, top = panel.y + 60;
        int k = 0;
        foreach (ResourceType t in System.Enum.GetValues(typeof(ResourceType)))
        {
            Rect r = new Rect(x + (k % 5) * (tile + gap), top + (k / 5) * (tile + gap), tile, tile);
            k++;
            int n = Count(t);
            Hud.Fill(r, new Color(1f, 1f, 1f, n > 0 ? 0.09f : 0.03f), 10);
            Color c = ResourcePickup.ColorOf(t);
            if (n == 0) c = Color.Lerp(c, Color.black, 0.65f);
            Hud.Fill(new Rect(r.x + tile / 2f - 22, r.y + 14, 44, 44), c, t == ResourceType.Wood ? 22 : 6);
            GUI.Label(new Rect(r.x, r.y + 62, tile, 22), ResourcePickup.Name(t), Hud.CenterTiny);
            GUI.Label(new Rect(r.x, r.y + 82, tile, 26), n.ToString(), Hud.Center);
        }

        // Детали, взятые молотом (F)
        int rows = (k + 4) / 5;
        float py = top + rows * (tile + gap) + 4;
        GUI.Label(new Rect(panel.x + 24, py, 400, 26), "Детали в руках", Hud.Title);
        py += 30;
        float px = panel.x + 24;
        bool any = false;
        foreach (var kv in pieces)
        {
            if (kv.Value <= 0) continue;
            any = true;
            string name = pieceNames.TryGetValue(kv.Key, out string nm) ? nm : kv.Key;
            string text = name + "  ×" + kv.Value;
            float w = Mathf.Max(120f, text.Length * 9f + 24f);
            if (px + w > panel.xMax - 24) { px = panel.x + 24; py += 40; }
            Rect chip = new Rect(px, py, w, 32);
            Hud.Fill(chip, new Color(0.4f, 0.6f, 0.9f, 0.25f), 8);
            GUI.Label(chip, text, Hud.Center);
            px += w + 10;
        }
        if (!any) GUI.Label(new Rect(panel.x + 24, py, 600, 24), "Пусто. Молотом (2) наведи на деталь и нажми F — она попадёт сюда.", Hud.Tiny);

        GUI.Label(new Rect(panel.x, panel.yMax - 58, W, 22),
                  "Доски делаются на верстаке, каменные блоки — у каменотёса. Подойди к станку и нажми E.", Hud.CenterTiny);
        GUI.Label(new Rect(panel.x, panel.yMax - 34, W, 24), "I / Esc — закрыть", Hud.CenterTiny);
    }
}

// Какое окно сейчас открыто (сумка, станок, меню построек). Одновременно — только одно.
// Пока окно открыто, курсор свободен и камера не крутится.
public static class Panels
{
    public static string Current { get; private set; }

    public static bool Is(string name) => Current == name;
    public static bool Any => Current != null;

    public static bool Open(string name)
    {
        if (Current != null && Current != name) return false;
        Current = name;
        PlayerCamera.BlockCursorLock = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        return true;
    }

    public static void Close(string name, bool relock)
    {
        if (Current != name) return;
        Current = null;
        PlayerCamera.BlockCursorLock = false;
        if (relock)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
