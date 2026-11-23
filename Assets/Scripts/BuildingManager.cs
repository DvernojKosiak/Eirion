using System.Collections.Generic;
using UnityEngine;

// Главный по постройкам. ВСЕ изменения идут через него: поставить, разобрать, поднять, урон.
// Когда добавим сеть, эти методы станут «просьбами к хосту» — остальное не придётся переделывать.
// Ещё он проверяет опору: если деталь ни через что не связана с землёй — она рушится.
public class BuildingManager : MonoBehaviour
{
    public static BuildingManager Instance { get; private set; }

    [Tooltip("Деталь стоит на земле, если её низ ниже этой высоты")]
    public float groundHeight = 0.2f;

    bool stabilityDirty;

    void Awake() { Instance = this; }

    public static BuildPiece Place(BuildPiece prefab, Vector3 position, Quaternion rotation)
    {
        if (prefab == null) return null;
        Transform parent = Instance != null ? Instance.transform : null;
        BuildPiece piece = Instantiate(prefab, position, rotation, parent);
        piece.name = prefab.name;
        return piece;
    }

    // Разобрать: деталь рассыпается, выпадают ресурсы (dropFraction — какая часть стоимости)
    public static void Dismantle(BuildPiece piece, float dropFraction = 1f)
    {
        if (piece == null || !piece.gameObject.activeSelf) return;
        Vector3 c = piece.Center;
        ResourcePickup.Spawn(ResourceType.Wood, Mathf.RoundToInt(piece.costWood * dropFraction), c);
        ResourcePickup.Spawn(ResourceType.Stone, Mathf.RoundToInt(piece.costStone * dropFraction), c);
        piece.Remove(true);
        RequestStabilityCheck();
    }

    // Поднять целиком (в руки/инвентарь). Возвращает id детали
    public static string PickUp(BuildPiece piece)
    {
        if (piece == null || !piece.gameObject.activeSelf) return null;
        string id = piece.pieceId;
        piece.Remove(false);
        RequestStabilityCheck();
        return id;
    }

    public static void Damage(BuildPiece piece, float amount)
    {
        if (piece != null) piece.ApplyDamage(amount);
    }

    public static void RequestStabilityCheck()
    {
        if (Instance != null) Instance.stabilityDirty = true;
    }

    void LateUpdate()
    {
        if (!stabilityDirty) return;
        stabilityDirty = false;
        CheckStability();
    }

    void CheckStability()
    {
        Physics.SyncTransforms();
        var supported = new HashSet<BuildPiece>();
        var queue = new Queue<BuildPiece>();

        foreach (BuildPiece p in BuildPiece.All)
            if (p.transform.position.y < groundHeight) { supported.Add(p); queue.Enqueue(p); }

        while (queue.Count > 0)
            foreach (BuildPiece n in queue.Dequeue().GetNeighbours())
                if (supported.Add(n)) queue.Enqueue(n);

        var falling = new List<BuildPiece>();
        foreach (BuildPiece p in BuildPiece.All)
            if (!supported.Contains(p)) falling.Add(p);

        // Рухнувшее отдаёт половину ресурсов
        foreach (BuildPiece p in falling) Dismantle(p, 0.5f);
    }
}
