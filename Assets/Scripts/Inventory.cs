using System.Collections.Generic;
using UnityEngine;

// Инвентарь игрока: ресурсы и поднятые детали построек.
// Сам подбирает ресурсы, которые лежат рядом.
public class Inventory : MonoBehaviour
{
    public int wood = 60;      // стартовые ресурсы
    public int stone = 40;
    public float pickupRadius = 3f;

    readonly Dictionary<string, int> pieces = new Dictionary<string, int>();

    // --- ресурсы ---
    public bool CanAfford(BuildPiece p) => wood >= p.costWood && stone >= p.costStone;

    public bool Spend(BuildPiece p)
    {
        if (!CanAfford(p)) return false;
        wood -= p.costWood;
        stone -= p.costStone;
        return true;
    }

    public void Add(ResourceType type, int amount)
    {
        if (type == ResourceType.Wood) wood += amount; else stone += amount;
    }

    // --- детали ---
    public int PieceCount(string id) => id != null && pieces.TryGetValue(id, out int n) ? n : 0;

    public void AddPiece(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        pieces[id] = PieceCount(id) + 1;
    }

    public bool TakePiece(string id)
    {
        int n = PieceCount(id);
        if (n <= 0) return false;
        pieces[id] = n - 1;
        return true;
    }

    void Update()
    {
        // Притягиваем ближние ресурсы и забираем их
        Vector3 me = transform.position;
        for (int i = ResourcePickup.All.Count - 1; i >= 0; i--)
        {
            ResourcePickup p = ResourcePickup.All[i];
            if (!p.CanCollect) continue;
            float d = Vector3.Distance(p.transform.position, me);
            if (d > pickupRadius) continue;
            if (d < 0.9f)
            {
                Add(p.type, p.amount);
                Destroy(p.gameObject);
                continue;
            }
            Rigidbody rb = p.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            p.transform.position = Vector3.MoveTowards(p.transform.position, me, 10f * Time.deltaTime);
        }
    }

    void OnGUI()
    {
        GUI.Label(new Rect(12, 36, 400, 26), "Дерево: " + wood + "    Камень: " + stone, Hud.Small);
    }
}
