using System.Collections.Generic;
using UnityEngine;

// Главный по постройкам. Через него идут ВСЕ изменения: поставить, ударить, снести.
// Зачем так: когда добавим игру по сети, эти три метода станут «просьбами к хосту»,
// и больше ничего переделывать не придётся.
// Ещё он проверяет опору: если деталь ни через что не связана с землёй — она падает.
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

    public static void Damage(BuildPiece piece, float amount)
    {
        if (piece != null) piece.ApplyDamage(amount);
    }

    public static void Demolish(BuildPiece piece)
    {
        if (piece != null) piece.Break();
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

    // Ищем все детали, связанные с землёй. Остальные — рушатся.
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

        foreach (BuildPiece p in falling) p.Break();
    }
}
